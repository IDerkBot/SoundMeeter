using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services.Logging;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace SoundMeeter.Services;

/// <summary>
/// Сервер док-панели OBS поверх TcpListener: своя минимальная реализация HTTP
/// и WebSocket вместо HttpListener/Kestrel.
///
/// Почему так: <see cref="HttpListener"/> не умеет апгрейд в WebSocket, а
/// Kestrel/AspNetCore ради трёх статических файлов и одного сокета заставил бы
/// тащить в portable-сборку лишний фреймворк (и ломает single-file publish).
/// Наш сервер — это ~200 строк: читаем первые строки запроса, на
/// <c>Upgrade: websocket</c> отвечаем 101 и дальше говорим на кадрах,
/// иначе отдаём файл ресурса и закрываем соединение.
///
/// Панель в OBS — это Browser Dock (CEF), то есть обычная страница: WebSocket
/// ей доступен, а на случай его недоступности есть запасной путь — опрос
/// <c>/api/state</c> тем же JSON.
/// </summary>
public sealed class ObsDockServer : IObsDockServer
{
    private const int MaxHeaderBytes = 4 * 1024;
    private const int SendQueueDepth = 4;
    private const string HandshakeGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly ILogger _logger = AppLog.For<ObsDockServer>();
    private readonly ObsDockJson _json = new();
    private readonly ConcurrentDictionary<long, DockClient> _clients = new();
    private readonly object _gate = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private long _clientSeq;
    private byte[] _lastPayload = "{}"u8.ToArray();
    private int _port;

    public bool IsRunning
    {
        get { lock (_gate) return _listener != null; }
    }

    public int Port
    {
        get { lock (_gate) return _port; }
    }

    public string Url => $"http://127.0.0.1:{Port}/";

    public int ClientCount => _clients.Count;

    public event Action<ObsDockCommand>? CommandReceived;
    public event Action<int>? ClientsChanged;

    public void Start(int port)
    {
        lock (_gate)
        {
            if (_listener != null)
            {
                if (_port == port) return;
                StopCore();
            }
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                _listener = listener;
                _port = ((IPEndPoint)listener.LocalEndpoint).Port;
                _cts = new CancellationTokenSource();
            }
            catch (SocketException ex)
            {
                _port = 0;
                throw new IOException(
                    $"Не удалось занять порт {port} для док-панели OBS: {ex.SocketErrorCode}. " +
                    "Выберите другой порт в настройках дока.", ex);
            }

            var token = _cts.Token;
            _ = AcceptLoopAsync(listener, token);
        }

        _logger.LogInformation("Док OBS: сервер слушает {Url}", Url);
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_gate) wasRunning = StopCore();

        // Shutdown() и Dispose() зовут Stop() подряд — второй раз молчим,
        // иначе в журнале будет по две записи на каждое закрытие приложения.
        if (wasRunning) _logger.LogInformation("Док OBS: сервер остановлен");
    }

    private bool StopCore()
    {
        bool wasRunning = _listener != null;

        _cts?.Cancel();
        try { _cts?.Dispose(); } catch { /* уже освобождён */ }
        _cts = null;

        try { _listener?.Stop(); } catch { /* сокет уже закрыт */ }
        _listener = null;
        _port = 0;

        foreach (var client in _clients.Values) client.Close();
        _clients.Clear();
        return wasRunning;
    }

    public void Publish(in ObsDockState state)
    {
        byte[] payload;
        lock (_json) payload = _json.Serialize(state);
        _lastPayload = payload;

        if (_clients.IsEmpty) return;
        foreach (var client in _clients.Values) client.Enqueue(payload);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                _logger.LogWarning(ex, "Док OBS: ошибка приёма подключения");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(tcp, token), token);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken token)
    {
        DockClient? client = null;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            var request = await HttpRequest.ReadAsync(stream, token).ConfigureAwait(false);
            if (request is null)
            {
                await WriteResponseAsync(stream, 400, "text/plain; charset=utf-8", "bad request"u8.ToArray())
                    .ConfigureAwait(false);
                return;
            }

            if (request.IsWebSocketUpgrade)
            {
                client = await StartWebSocketAsync(tcp, stream, request, token).ConfigureAwait(false);
                if (client is null) return;

                // Два независимых конца сокета: читаем команды от панели и
                // одновременно сливаем очередь состояния в сокет.
                var reader = ReadWebSocketLoopAsync(stream, client, token);
                var writer = WriteWebSocketLoopAsync(stream, client, token);
                await Task.WhenAny(reader, writer).ConfigureAwait(false);
                client.Close();
                return;
            }

            var (status, contentType, body) = ResolveHttpRoute(request.Path);
            await WriteResponseAsync(stream, status, contentType, body).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Док OBS: соединение закрыто ({Message})", ex.Message);
        }
        catch (OperationCanceledException)
        {
            // Остановка сервера или приложения — это штатная ситуация.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Док OBS: необработанная ошибка обслуживания подключения");
        }
        finally
        {
            if (client is not null)
            {
                _clients.TryRemove(client.Id, out _);
                client.Close();
                LogClientClosed(_clients.Count);
                ClientsChanged?.Invoke(_clients.Count);
            }

            try { tcp.Close(); } catch { /* уже закрыт */ }
        }
    }

    /// <summary>Отдача статики панели и запасного polled-эндпоинта состояния.</summary>
    private (int Status, string ContentType, byte[] Body) ResolveHttpRoute(string path)
    {
        switch (path)
        {
            case "/" or "/index.html":
                return (200, "text/html; charset=utf-8", ObsDockAssets.Get(ObsDockAssets.IndexHtml));
            case "/dock.css":
                return (200, "text/css; charset=utf-8", ObsDockAssets.Get(ObsDockAssets.DockCss));
            case "/dock.js":
                return (200, "application/javascript; charset=utf-8", ObsDockAssets.Get(ObsDockAssets.DockJs));
            case "/api/state":
                return (200, "application/json; charset=utf-8", _lastPayload);
            case "/favicon.ico":
                return (204, "image/x-icon", Array.Empty<byte>());
            default:
                return (404, "text/plain; charset=utf-8", "not found"u8.ToArray());
        }
    }

    private async Task<DockClient?> StartWebSocketAsync(TcpClient tcp, NetworkStream stream,
        HttpRequest request, CancellationToken token)
    {
        var key = request.Header("sec-websocket-key");
        if (string.IsNullOrEmpty(key)) return null;

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + HandshakeGuid)));
        var handshake =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(handshake), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        var client = new DockClient(Interlocked.Increment(ref _clientSeq));
        _clients[client.Id] = client;
        ClientsChanged?.Invoke(_clients.Count);
        _logger.LogInformation("Док OBS: панель подключена ({Count} активных)", _clients.Count);
        return client;
    }

    /// <summary>Слишком длинная голова запроса — отказ, чтобы мусор в сокете не съедал память.</summary>
    private const int HeaderBufferSize = MaxHeaderBytes;

    /// <summary>
    /// Цикл записи: снимает состояние из очереди и отправляет панели. Очередь
    /// ограничена и при переполнении выбрасывает САМЫЕ СТАРЫЕ кадры — свежие
    /// метры важнее истории.
    /// </summary>
    private static async Task WriteWebSocketLoopAsync(NetworkStream stream, DockClient client, CancellationToken token)
    {
        try
        {
            while (await client.Queue.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (client.Queue.TryRead(out var payload))
                    await stream.WriteAsync(payload, token).ConfigureAwait(false);

                await stream.FlushAsync(token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка сервера.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            // Панель отвалилась (перезагрузка дока, закрытие OBS) — это норма.
        }
    }

    private async Task ReadWebSocketLoopAsync(NetworkStream stream, DockClient client, CancellationToken token)
    {
        var continuation = new MemoryStream();
        while (!token.IsCancellationRequested)
        {
            var read = await WebSocketFrame.ReadAsync(stream, token).ConfigureAwait(false);
            if (read is null) return;
            var frame = read.Value;

            switch (frame.Opcode)
            {
                case WebSocketFrame.Close:
                    return;
                case WebSocketFrame.Ping:
                    client.SendControl(WebSocketFrame.Pong, frame.Payload);
                    continue;
                case WebSocketFrame.Pong:
                    continue;
                case WebSocketFrame.Text or WebSocketFrame.Continuation:
                    if (frame.Opcode == WebSocketFrame.Continuation && continuation.Length == 0)
                    {
                        // «Продолжение» без начала — мусор в канале, игнорируем.
                        continue;
                    }

                    continuation.Write(frame.Payload, 0, frame.Payload.Length);
                    if (!frame.Fin) continue;

                    var text = Encoding.UTF8.GetString(continuation.ToArray());
                    continuation.SetLength(0);
                    HandleCommand(text);
                    continue;
                default:
                    continue;
            }
        }
    }

    private void HandleCommand(string json)
    {
        var command = ObsDockCommand.TryParse(json);
        if (command is null)
        {
            _logger.LogDebug("Док OBS: нераспознанная команда {Json}", json);
            return;
        }

        try
        {
            CommandReceived?.Invoke(command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Док OBS: команда {Op} для {Id} не применена", command.Op, command.Id);
        }
    }

    private void LogClientClosed(int remaining)
    {
        _logger.LogInformation("Док OBS: панель отключена ({Count} активных)", remaining);
    }

    /// <summary>Подключённая панель: очередь исходящих кадров + своя задача записи.</summary>
    private sealed class DockClient
    {
        private readonly Channel<byte[]> _queue =
            Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueDepth)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        public DockClient(long id) => Id = id;

        public long Id { get; }

        public ChannelReader<byte[]> Queue => _queue.Reader;

        /// <summary>Очередь хранит ГОТОВЫЕ кадры: её сливает цикл записи в сокет как есть.</summary>
        public void Enqueue(byte[] payload) =>
            _queue.Writer.TryWrite(WebSocketFrame.Build(WebSocketFrame.Text, payload));

        public void SendControl(byte opcode, ReadOnlyMemory<byte> payload) =>
            _queue.Writer.TryWrite(WebSocketFrame.Build(opcode, payload));

        public void Close() => _queue.Writer.TryComplete();
    }

    private sealed class HttpRequest
    {
        public string Path { get; set; } = "/";
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        public bool IsWebSocketUpgrade =>
            string.Equals(Header("upgrade"), "websocket", StringComparison.OrdinalIgnoreCase);

        /// <summary>Читает голову запроса до пустой строки. null — соединение закрыто или голова слишком велика.</summary>
        public static async Task<HttpRequest?> ReadAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[HeaderBufferSize];
            var read = 0;

            while (read < buffer.Length)
            {
                int n;
                try
                {
                    n = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    return null;
                }

                if (n <= 0) return null;
                read += n;

                var head = Encoding.ASCII.GetString(buffer, 0, read);
                var end = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end < 0)
                {
                    if (read >= MaxHeaderBytes) return null;
                    continue;
                }

                return Parse(head[..end]);
            }

            return null;
        }

        private static HttpRequest Parse(string head)
        {
            var lines = head.Split("\r\n");
            var request = new HttpRequest();

            if (lines.Length > 0)
            {
                var parts = lines[0].Split(' ');
                if (parts.Length >= 2) request.Path = NormalizePath(parts[1]);
            }

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            return request;
        }

        /// <summary>Отрезает query-строку и защищает от выхода за пределы ожидаемых маршрутов.</summary>
        private static string NormalizePath(string raw)
        {
            int query = raw.IndexOf('?');
            var path = query >= 0 ? raw[..query] : raw;
            if (path.Length == 0) return "/";
            return path.Length <= 64 ? path : "/";
        }
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string contentType, byte[] body)
    {
        var reason = status switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            _ => "Not Found"
        };

        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(head).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }
}

/// <summary>Фреймы WebSocket: чтение (маскированные) и сборка (без маски).</summary>
internal static class WebSocketFrame
{
    public const byte Continuation = 0x0;
    public const byte Text = 0x1;
    public const byte Binary = 0x2;
    public const byte Close = 0x8;
    public const byte Ping = 0x9;
    public const byte Pong = 0xA;

    public readonly record struct Frame(byte Opcode, bool Fin, byte[] Payload);

    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(stream, header, token).ConfigureAwait(false)) return null;

        byte b0 = header[0];
        byte b1 = header[1];
        bool fin = (b0 & 0x80) != 0;
        byte opcode = (byte)(b0 & 0x0F);
        bool masked = (b1 & 0x80) != 0;
        long length = b1 & 0x7F;

        if (length == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, token).ConfigureAwait(false)) return null;
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, token).ConfigureAwait(false)) return null;
            length = 0;
            for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
        }

        // Защита от абсурдного кадра: панели шлёт команды в десятки байт.
        if (length is < 0 or > 1024 * 1024) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, token).ConfigureAwait(false)) return null;

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, token).ConfigureAwait(false)) return null;

        if (masked)
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(payload[i] ^ mask[i & 3]);

        return new Frame(opcode, fin, payload);
    }

    /// <summary>Собирает один кадр. Длинные данные режутся на куски по 64 КиБ.</summary>
    public static byte[] Build(byte opcode, ReadOnlyMemory<byte> payload)
    {
        const int maxChunk = 64 * 1024;
        int chunks = Math.Max(1, (payload.Length + maxChunk - 1) / maxChunk);
        var result = new byte[BuildFrameSize(payload.Length, chunks)];

        int offset = 0, written = 0;
        for (int c = 0; c < chunks; c++)
        {
            int len = Math.Min(maxChunk, payload.Length - offset);
            bool last = c == chunks - 1;

            result[written++] = (byte)((last ? 0x80 : 0x00) | opcode);
            if (len < 126)
            {
                result[written++] = (byte)len;
            }
            else if (len <= ushort.MaxValue)
            {
                result[written++] = 126;
                result[written++] = (byte)(len >> 8);
                result[written++] = (byte)len;
            }
            else
            {
                result[written++] = 127;
                for (int s = 56; s >= 0; s -= 8) result[written++] = (byte)(len >> s);
            }

            payload.Span.Slice(offset, len).CopyTo(result.AsSpan(written));
            written += len;
            offset += len;
        }

        return result;
    }

    private static int BuildFrameSize(int payloadLength, int chunks)
    {
        int perChunk = payloadLength switch
        {
            < 126 => 2,
            <= ushort.MaxValue => 4,
            _ => 10
        };
        return perChunk * chunks + payloadLength;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read), token).ConfigureAwait(false);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Сериализация состояния в JSON без System.Text.Json: сообщение уходит 30 раз
/// в секунду, а формат зафиксирован. Ручная сборка даёт предсказуемый
/// (короткий) пакет и не тащит на каждый кадр ленты аллокаций.
/// </summary>
internal sealed class ObsDockJson
{
    private StringBuilder _sb = new(1024);
    private byte[] _bytes = new byte[4096];

    public byte[] Serialize(in ObsDockState state)
    {
        _sb.Clear();
        var sb = _sb;

        sb.Append("{\"eng\":").Append(state.EngineRunning ? "true" : "false")
            .Append(",\"min\":").Append(Num(state.MinDb))
            .Append(",\"max\":").Append(Num(state.MaxDb))
            .Append(",\"ch\":[");

        for (int i = 0; i < state.Channels.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var c = state.Channels[i];

            sb.Append("{\"id\":");
            AppendString(sb, c.Id);
            sb.Append(",\"k\":");
            AppendString(sb, c.Kind);
            sb.Append(",\"n\":");
            AppendString(sb, c.Name);
            sb.Append(",\"d\":");
            AppendString(sb, c.DeviceName);
            sb.Append(",\"dev\":");
            AppendString(sb, c.DeviceId);
            sb.Append(",\"db\":").Append(Num(c.VolumeDb))
                .Append(",\"p\":").Append(Peak(c.Peak))
                .Append(",\"m\":").Append(c.IsMuted ? "true" : "false")
                .Append(",\"s\":").Append(c.IsSolo ? "true" : "false")
                .Append(",\"o\":").Append(c.IsMono ? "true" : "false")
                .Append(",\"a\":").Append(c.IsAvailable ? "true" : "false")
                .Append('}');
        }

        sb.Append("]}");

        int max = Encoding.UTF8.GetMaxByteCount(sb.Length);
        if (_bytes.Length < max) _bytes = new byte[max];
        int count = Encoding.UTF8.GetBytes(sb.ToString(), 0, sb.Length, _bytes, 0);

        var result = new byte[count];
        Buffer.BlockCopy(_bytes, 0, result, 0, count);
        return result;
    }

    private static string Num(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Peak(float value) =>
        Math.Clamp(value, 0f, 1.5f).ToString("0.####", CultureInfo.InvariantCulture);

    private static void AppendString(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            sb.Append("\"\"");
            return;
        }

        sb.Append('"');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (ch < 0x20)
                        sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
    }
}
