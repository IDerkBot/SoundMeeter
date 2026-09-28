using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SoundMeeter.Services;

/// <summary>
/// Страница панели дока хранится во встроенных ресурсах сборки: у пользователя
/// нет ни папки с HTML, ни настроек — панель всегда соответствует версии
/// приложения (в том числе после автообновления portable-сборки).
/// </summary>
internal static class ObsDockAssets
{
    public const string IndexHtml = "obs-dock.index.html";
    public const string DockCss = "obs-dock.dock.css";
    public const string DockJs = "obs-dock.dock.js";

    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    private static readonly ILogger Logger = AppLog.For<ObsDockServer>();

    /// <summary>Ресурсы, о неудаче с чтением которых уже сообщили — иначе каждый
    /// запрос панели писал бы в журнал одно и то же.</summary>
    private static readonly ConcurrentDictionary<string, bool> Reported = new(StringComparer.Ordinal);

    public static byte[] Get(string name)
    {
        if (Cache.TryGetValue(name, out var cached)) return cached;

        // Аварийная заглушка собирается из ресурсов, то есть зависит от языка
        // интерфейса, и в кэш (общий на всё время работы приложения) не идёт.
        var bytes = Load(name);
        if (bytes.Length > 0) Cache[name] = bytes;
        return bytes;
    }

    private static byte[] Load(string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null)
            {
                if (Reported.TryAdd(name, true))
                    Logger.LogError("Встроенный ресурс {Resource} не найден в сборке", name);
                return Fallback(name);
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex)
        {
            if (Reported.TryAdd(name, true))
                Logger.LogError(ex, "Не удалось прочитать ресурс панели {Resource}", name);
            return Fallback(name);
        }
    }

    private static byte[] Fallback(string name) => name == IndexHtml
        ? Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=\"utf-8\"><body style=\"background:#1E1E1E;color:#EEE;" +
            "font:12px Segoe UI,sans-serif;padding:10px\">" + Loc.Get("Sm.Obs.AssetMissing", name) + "</body>")
        : Array.Empty<byte>();

    /// <summary>
    /// Тексты панели на языке приложения (SM-C07). Панель в OBS грузится один раз,
    /// поэтому словарь встраивается прямо в HTML: отдельный запрос означал бы
    /// мигание непереведённых подписей при открытии дока.
    /// </summary>
    public static string BuildScriptBlock()
    {
        var json = new StringBuilder("{");
        foreach (string key in TextKeys)
        {
            if (json.Length > 1) json.Append(',');
            json.Append('"').Append(key[KeyPrefix.Length..]).Append("\":");
            json.Append(JsonSerializer.Serialize(Loc.Get(key)));
        }
        json.Append('}');

        return "<script>window.SM_I18N=" + json + ";</script>";
    }

    /// <summary>Ключи ресурсов, которые нужны панели. Префикс <see cref="KeyPrefix"/>
    /// отбрасывается на стороне JS — там ключи короче.</summary>
    private const string KeyPrefix = "Sm.Dock.";

    private static readonly string[] TextKeys =
    {
        KeyPrefix + "Connection",
        KeyPrefix + "EngineStopped",
        KeyPrefix + "NoChannels",
        KeyPrefix + "VolumeTitle",
        KeyPrefix + "In",
        KeyPrefix + "Out",
        KeyPrefix + "Mute",
        KeyPrefix + "Solo",
    };
}