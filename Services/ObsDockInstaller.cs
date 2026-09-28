using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SoundMeeter.Services;

/// <summary>Итог попытки добавить/убрать док в конфигурации OBS.</summary>
public sealed record ObsDockInstallResult(bool Success, string Message, string? Path = null)
{
    public static ObsDockInstallResult Ok(string message, string? path = null) => new(true, message, path);
    public static ObsDockInstallResult Fail(string message) => new(false, message);
}

/// <summary>
/// Обёртка над конфигом OBS: знает, где лежит user.ini, и НЕ ДАЁТ править его,
/// пока запущен OBS. Работа с самим файлом — в <see cref="ObsDockConfig"/>.
///
/// Проверка «OBS закрыт» живёт именно здесь, а не в коде правки, чтобы её нельзя
/// было случайно обойти и затереть файл: работающий OBS переписывает user.ini
/// целиком при выходе, и наша запись пропала бы вместе с чужими настройками.
/// </summary>
public sealed class ObsDockInstaller
{
    private const string UserIniName = "user.ini";

    /// <summary>Каталог конфигурации OBS. По умолчанию %APPDATA%\obs-studio.</summary>
    public static string DefaultConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    private readonly string _configDirectory;

    public ObsDockInstaller() : this(DefaultConfigDirectory) { }

    /// <summary>Явный каталог — нужен проверкам, чтобы не трогать конфиг пользователя.</summary>
    public ObsDockInstaller(string configDirectory) => _configDirectory = configDirectory;

    /// <summary>Путь к user.ini, который правим (может не существовать — OBS ещё не запускался).</summary>
    public string UserIniPath => Path.Combine(_configDirectory, UserIniName);

    /// <summary>Запущен ли сейчас OBS.</summary>
    public static bool IsObsRunning()
    {
        foreach (var name in new[] { "obs64", "obs32" })
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>Есть ли уже наш док в списке (нужно окну настроек для статуса).</summary>
    public bool IsDockInstalled() => ObsDockConfig.IsDockInstalled(UserIniPath);

    /// <summary>Добавляет док (или обновляет его URL). Требует закрытого OBS.</summary>
    public ObsDockInstallResult Install(string url) =>
        Guarded(() => ObsDockConfig.Install(UserIniPath, url));

    /// <summary>Убирает наш док из списка. Требует закрытого OBS.</summary>
    public ObsDockInstallResult Uninstall() =>
        Guarded(() => ObsDockConfig.Uninstall(UserIniPath));

    private static ObsDockInstallResult Guarded(Func<ObsDockInstallResult> action)
    {
        if (IsObsRunning())
        {
            return ObsDockInstallResult.Fail(
                "OBS сейчас запущен и перезапишет user.ini при выходе. Закройте OBS и повторите.");
        }
        return action();
    }
}

/// <summary>
/// Правка списка доков OBS в user.ini — без знания о процессах, поэтому
/// проверяется отдельно (на копиях файла).
///
/// OBS хранит «лишние» браузерные доки (добавленные пользователем, а не
/// штатным макетом) в секции <c>[BasicWindow]</c> файла user.ini массивом
/// <c>ExtraBrowserDocks=[{"title": "...", "url": "...", "uuid": "..."}]</c>.
///
/// Именно этот список и читает OBS при старте: док оттуда появляется в меню
/// View → Docks и сам размещается в свободной области. Положение и размер окна
/// лежат в QMainWindow-блобе DockState — он намеренно не трогается (OBS
/// перезапишет его сам, а вручную такой blob не собирается).
/// </summary>
public static class ObsDockConfig
{
    private const string SectionName = "BasicWindow";
    private const string DocksKey = "ExtraBrowserDocks";
    private const string DockTitle = "SoundMeeter";
    private const string BackupSuffix = ".soundmeeter.bak";

    /// <summary>
    /// Идентификатор дока SoundMeeter. Постоянный: повторная установка обновляет
    /// URL (например, после смены порта), а не плодит копии.
    /// </summary>
    public const string DockUuid = "5c0f3a91b7d24e6aa1c0b8e2d4f61a37";

    private static readonly ILogger Logger = AppLog.For<ObsDockInstaller>();

    public static bool IsDockInstalled(string userIniPath)
    {
        var docks = ReadDocks(userIniPath);
        return docks is not null && docks.Any(IsSoundMeeter);
    }

    /// <summary>Добавляет запись дока или обновляет URL существующей.</summary>
    public static ObsDockInstallResult Install(string userIniPath, string url)
    {
        try
        {
            var docks = ReadDocks(userIniPath);
            if (docks is null)
                return ObsDockInstallResult.Fail($"Не удалось разобрать {DocksKey} в {userIniPath}. Файл не тронут.");

            var existing = docks.FirstOrDefault(IsSoundMeeter);
            if (existing is not null)
            {
                existing["url"] = url;
                existing["title"] = DockTitle;
            }
            else
            {
                docks.Add(new Dictionary<string, string>
                {
                    ["title"] = DockTitle,
                    ["url"] = url,
                    ["uuid"] = DockUuid
                });
            }

            return WriteDocks(userIniPath, docks);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Не удалось добавить док в user.ini: {Message}", ex.Message);
            return ObsDockInstallResult.Fail($"Не удалось изменить {userIniPath}: {ex.Message}");
        }
    }

    /// <summary>Убирает запись дока. Если её нет — сообщаем, что удалять нечего.</summary>
    public static ObsDockInstallResult Uninstall(string userIniPath)
    {
        try
        {
            var docks = ReadDocks(userIniPath);
            if (docks is null)
                return ObsDockInstallResult.Fail($"Не удалось разобрать {DocksKey} в {userIniPath}. Файл не тронут.");

            int removed = docks.RemoveAll(IsSoundMeeter);
            if (removed == 0)
                return ObsDockInstallResult.Ok("Док SoundMeeter в конфигурации OBS не найден — удалять нечего.", userIniPath);

            return WriteDocks(userIniPath, docks);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Не удалось убрать док из user.ini: {Message}", ex.Message);
            return ObsDockInstallResult.Fail($"Не удалось изменить {userIniPath}: {ex.Message}");
        }
    }

    private static bool IsSoundMeeter(Dictionary<string, string> dock) =>
        string.Equals(dock.GetValueOrDefault("uuid"), DockUuid, StringComparison.OrdinalIgnoreCase);

    /// <summary>Читает список доков. null — файла/ключа нет либо JSON не разбирается.</summary>
    private static List<Dictionary<string, string>>? ReadDocks(string userIniPath)
    {
        if (!File.Exists(userIniPath)) return null;

        string text = File.ReadAllText(userIniPath, Encoding.UTF8);
        string? raw = IniValueReader.ReadValue(text, SectionName, DocksKey);
        if (string.IsNullOrWhiteSpace(raw)) return new List<Dictionary<string, string>>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var result = new List<Dictionary<string, string>>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) return null;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var prop in item.EnumerateObject())
                    map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();

                result.Add(map);
            }
            return result;
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "{Key} в {Path} не разбирается как JSON", DocksKey, userIniPath);
            return null;
        }
    }

    private static ObsDockInstallResult WriteDocks(string userIniPath, List<Dictionary<string, string>> docks)
    {
        if (!File.Exists(userIniPath))
            return ObsDockInstallResult.Fail($"Не найден {userIniPath}. Запустите OBS хотя бы раз, чтобы он создал конфигурацию.");

        BackupOnce(userIniPath);

        string text = File.ReadAllText(userIniPath, Encoding.UTF8);
        string updated = IniValueReader.WriteValue(text, SectionName, DocksKey, SerializeDocks(docks));
        File.WriteAllText(userIniPath, updated, new UTF8Encoding(false));

        Logger.LogInformation("Док OBS: записано {Count} записей в {Key}", docks.Count, DocksKey);
        return ObsDockInstallResult.Ok(
            "Док добавлен в OBS. Перезапустите OBS — панель появится в меню View → Docks → SoundMeeter.",
            userIniPath);
    }

    /// <summary>Резервная копия делается один раз: до первой правки этого сеанса OBS.</summary>
    private static void BackupOnce(string userIniPath)
    {
        var backup = userIniPath + BackupSuffix;
        if (File.Exists(backup)) return;

        try
        {
            File.Copy(userIniPath, backup, overwrite: false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Не удалось сделать резервную копию {Backup}", backup);
        }
    }

    /// <summary>Пишет список в формате OBS: одна строка, ключи в порядке title/url/uuid.</summary>
    private static string SerializeDocks(List<Dictionary<string, string>> docks)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < docks.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('{');
            bool first = true;
            foreach (var key in new[] { "title", "url", "uuid" })
            {
                if (!docks[i].TryGetValue(key, out var v)) continue;
                if (!first) sb.Append(", ");
                first = false;
                sb.Append('"').Append(key).Append("\": \"").Append(Escape(v)).Append('"');
            }
            sb.Append('}');
        }
        return sb.Append(']').ToString();
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"")
        .Replace("\r", "")
        .Replace("\n", "");
}

/// <summary>
/// Минимальная правка INI: найти значение по секции и заменить строку целиком.
/// Секция и ключ ищутся без учёта регистра, порядок строк и кодировка файла
/// сохраняются — лишние изменения в конфиге OBS недопустимы.
/// </summary>
internal static class IniValueReader
{
    public static string? ReadValue(string text, string section, string key)
    {
        bool inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                inSection = line.TrimEnd().Trim('[', ']')
                    .Equals(section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (!line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            return line[(eq + 1)..].Trim();
        }
        return null;
    }

    /// <summary>Заменяет значение ключа в секции; если ключа или секции нет — добавляет.</summary>
    public static string WriteValue(string text, string section, string key, string value)
    {
        var newLine = $"{key}={value}";
        var lines = text.Split('\n').ToList();
        int sectionStart = -1, keyIndex = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                if (sectionStart >= 0)
                {
                    break;
                }

                if (line.TrimEnd().Trim('[', ']').Equals(section, StringComparison.OrdinalIgnoreCase))
                    sectionStart = i;
                continue;
            }

            if (sectionStart < 0) continue;
            int eq = line.IndexOf('=');
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                keyIndex = i;
                break;
            }
        }

        if (keyIndex >= 0)
        {
            // split('\n') уносит перевод строки, но не CR — возвращаем его,
            // иначе OBS увидит в файле смесь окончаний строк.
            bool hadCarriageReturn = lines[keyIndex].EndsWith('\r');
            lines[keyIndex] = hadCarriageReturn ? newLine + "\r" : newLine;
            return string.Join('\n', lines);
        }

        if (sectionStart < 0)
        {
            var result = text.EndsWith('\n') ? text : text + "\r\n";
            return result + $"[{section}]\r\n{newLine}\r\n";
        }

        // Ключ вставляем в начало секции — так он заметен и не теряется между
        // сотнями строк состояния окна.
        lines.Insert(sectionStart + 1, newLine);
        return string.Join('\n', lines);
    }
}
