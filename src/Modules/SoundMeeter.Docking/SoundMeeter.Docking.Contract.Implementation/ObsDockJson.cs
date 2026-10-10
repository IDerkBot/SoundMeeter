using SoundMeeter.Docking.Entities;
using System.Globalization;
using System.Text;

namespace SoundMeeter.Docking.Contract.Implementation;

/// <summary>
/// Сериализация состояния в JSON без System.Text.Json: сообщение уходит 30 раз
/// в секунду, а формат зафиксирован. Ручная сборка даёт предсказуемый
/// (короткий) пакет и не тащит на каждый кадр ленты аллокаций.
///
/// Ручной сериализатор — не украшение: <see cref="System.Text.Json.Utf8JsonWriter"/>
/// на каждый кадр создаёт буфер, а <c>JsonSerializer.Serialize</c> ещё и пишет
/// имена свойств. Здесь ключи зашиты в коде короткими: пакет на 12 каналов
/// весит единицы килобайт вместо десятков.
///
/// Потокобезопасности нет намеренно: <see cref="Serialize"/> переиспользует
/// буферы, поэтому вызывать её может только один поток. Так и делает
/// <see cref="ObsDockServer"/> — под замком. Новый вызов без замка перепишет
/// буфер на середине чтения.
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

    /// <summary>
    /// Пик в 0…1. Приводится к этому диапазону: панель рисует столбик по
    /// фиксированной высоте, и значение выше единицы переполнило бы его.
    /// </summary>
    private static string Peak(float value) =>
        Math.Clamp(value, 0f, 1.5f).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Строка в JSON с экранированием. Здесь же и единственная причина, по
    /// которой вручную собранный пакет остаётся валидным: имя канала приходит
    /// из настроек Windows и может содержать что угодно, включая кавычки.
    /// </summary>
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