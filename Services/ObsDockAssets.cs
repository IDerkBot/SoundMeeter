using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;

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

    public static byte[] Get(string name) => Cache.GetOrAdd(name, Load);

    private static byte[] Load(string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null)
            {
                Logger.LogError("Встроенный ресурс {Resource} не найден в сборке", name);
                return Fallback(name);
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Не удалось прочитать ресурс панели {Resource}", name);
            return Fallback(name);
        }
    }

    private static byte[] Fallback(string name) => name == IndexHtml
        ? Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=\"utf-8\"><body style=\"background:#1E1E1E;color:#EEE;" +
            "font:12px Segoe UI,sans-serif;padding:10px\">SoundMeeter: панель дока повреждена " +
            "(нет встроенного ресурса " + name + ").</body>")
        : Array.Empty<byte>();
}
