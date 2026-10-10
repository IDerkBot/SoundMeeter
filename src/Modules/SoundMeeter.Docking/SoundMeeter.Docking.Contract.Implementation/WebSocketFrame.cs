namespace SoundMeeter.Docking.Contract.Implementation;

/// <summary>
/// Фреймы WebSocket: чтение (маскированные) и сборка (без маски).
///
/// Вынесено из сервера отдельно: это самостоятельный кодек, о доке не знающий
/// ничего. Его можно проверить, не поднимая сокет, и он меняется по своим
/// правилам — когда появятся, скажем, расширения сообщений.
///
/// Только то, что нужно панели OBS: она шлёт короткие команды и читает снимки
/// состояния. Серверные фреймы не склеиваются (обычно это не требуется, а
/// склейка с лимитом — отдельная история), зато фрагменты команды собираются:
/// порция в 64 КиБ — это граница TCP-сегмента на практике.
/// </summary>
internal static class WebSocketFrame
{
    public const byte Continuation = 0x0;
    public const byte Text = 0x1;
    public const byte Binary = 0x2;
    public const byte Close = 0x8;
    public const byte Ping = 0x9;
    public const byte Pong = 0xA;

    /// <summary>Панель шлёт команды в десятки байт; мегабайтный кадр — это мусор
    /// или попытка положить сервер.</summary>
    private const long MaxPayloadBytes = 1024 * 1024;

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

        // Проверка после разбора длины, а не по заголовку: длину в 64-битном виде
        // присылает только клиент, и до этого момента она ещё не тронула память.
        if (length is < 0 or > MaxPayloadBytes) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, token).ConfigureAwait(false)) return null;

        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, token).ConfigureAwait(false)) return null;

        if (masked)
            for (int i = 0; i < payload.Length; i++)
                payload[i] = (byte)(payload[i] ^ mask[i & 3]);

        return new Frame(opcode, fin, payload);
    }

    /// <summary>Собирает один фрейм. Длинные данные режутся на куски по 64 КиБ.</summary>
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