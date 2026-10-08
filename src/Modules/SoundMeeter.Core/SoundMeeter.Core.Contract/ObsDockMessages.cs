namespace SoundMeeter.Models;

/// <summary>
/// Протокол локального сервера дока (ObsDockServer).
///
/// Состояние идёт от приложения к доку (push ~30 Гц) и содержит ровно то,
/// что нужно панели: метры, фейдеры и кнопки. Команды идут обратно от дока.
/// Формат — плоский JSON: его читает и страница в OBS, и наш харнесс.
///
/// Сверка состояния идёт одним объектом (не дельтами): панель маленькая,
/// пакет занимает единицы килобайт, зато док сам восстанавливается после
/// любого потерянного сообщения и не хранит предыдущее состояние.
/// </summary>
public sealed class ObsDockChannelState
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = ObsDockChannels.Input;
    public string Name { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public float VolumeDb { get; init; }
    public float Peak { get; init; }
    public bool IsMuted { get; init; }
    public bool IsSolo { get; init; }
    public bool IsMono { get; init; }
    public bool IsAvailable { get; init; } = true;

    public ObsDockChannelState() { }

    public ObsDockChannelState(string id, string kind, string name, string deviceId, string deviceName,
        float volumeDb, float peak, bool isMuted, bool isSolo, bool isMono, bool isAvailable)
    {
        Id = id;
        Kind = kind;
        Name = name;
        DeviceId = deviceId;
        DeviceName = deviceName;
        VolumeDb = volumeDb;
        Peak = peak;
        IsMuted = isMuted;
        IsSolo = isSolo;
        IsMono = isMono;
        IsAvailable = isAvailable;
    }
}

/// <summary>Снимок состояния микшера для дока: каналы + признак «движок запущен».</summary>
public sealed class ObsDockState
{
    public bool EngineRunning { get; init; }
    public IReadOnlyList<ObsDockChannelState> Channels { get; init; } = Array.Empty<ObsDockChannelState>();

    /// <summary>Нижняя граница фейдера в дБ (совпадает с VoiceFader в MixerTheme).</summary>
    public float MinDb { get; init; } = -60f;

    /// <summary>Верхняя граница фейдера в дБ.</summary>
    public float MaxDb { get; init; } = 12f;

    public ObsDockState() { }

    public ObsDockState(bool engineRunning, IReadOnlyList<ObsDockChannelState> channels)
    {
        EngineRunning = engineRunning;
        Channels = channels;
    }
}

/// <summary>Операции, которые док может прислать приложению.</summary>
public static class ObsDockOps
{
    /// <summary>Громкость стрипа, дБ (value).</summary>
    public const string Volume = "vol";

    /// <summary>Mute (value = true/false).</summary>
    public const string Mute = "mute";

    /// <summary>Solo (value = true/false).</summary>
    public const string Solo = "solo";

    /// <summary>Mono (value = true/false).</summary>
    public const string Mono = "mono";
}

/// <summary>Команда от дока к приложению: {op, id, kind, v}.</summary>
public sealed class ObsDockCommand
{
    public string Op { get; init; } = "";
    public string Id { get; init; } = "";
    public string Kind { get; init; } = ObsDockChannels.Input;

    /// <summary>Числовое значение: дБ для <see cref="ObsDockOps.Volume"/>, 1/0 — для флагов.</summary>
    public float Value { get; set; }

    /// <summary>Значение флага (mute/solo/mono) — извлекается из Value.</summary>
    public bool Flag { get; set; }

    /// <summary>Разбор команды из JSON. null — команда не распознана.</summary>
    public static ObsDockCommand? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            string op = Str(root, "op") ?? "";
            string id = Str(root, "id") ?? "";
            if (op.Length == 0 || id.Length == 0) return null;

            string kind = Str(root, "kind") ?? ObsDockChannels.Input;
            if (kind != ObsDockChannels.Input && kind != ObsDockChannels.Output)
                kind = ObsDockChannels.Input;

            var command = new ObsDockCommand { Op = op, Id = id, Kind = kind };
            if (root.TryGetProperty("v", out var v))
            {
                if (v.ValueKind == System.Text.Json.JsonValueKind.Number && v.TryGetSingle(out var num))
                {
                    command.Value = num;
                    command.Flag = Math.Abs(num) > float.Epsilon;
                }
                else if (v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                {
                    command.Flag = v.GetBoolean();
                    command.Value = command.Flag ? 1f : 0f;
                }
            }

            return command;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Str(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;
}
