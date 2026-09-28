using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Markup;

namespace SoundMeeter.Services;

/// <summary>
/// Локализация интерфейса (SM-C07).
///
/// Единый источник строк — <c>Resources/Strings.resx</c> (нейтральная, английская)
/// плюс спутники <c>Strings.ru.resx</c>. Из этого одного набора обслуживаются
/// оба канала доступа:
///
/// * разметка — словарь <see cref="ResourceDictionary"/>, который подмешивается в
///   <c>Application.Resources</c> и читается через <c>{DynamicResource Sm.Ключ}</c>.
///   Именно DynamicResource, а не StaticResource/x:Static: смена языка перезаписывает
///   словарь на месте, и все уже созданные окна и попапы перечитывают значения сами,
///   без пересоздания визуального дерева (SM-C07: «без перезапуска»);
/// * код — <see cref="Get(string, object?[])"/>. Строки, собираемые в рантайме
///   (статусы, сообщения об ошибках, подписи списков), идут только этим путём.
///
/// Переключение языка: <see cref="SetLanguage"/> ставит <see cref="CultureInfo"/>
/// текущего потока, перезаливает словарь и поднимает <see cref="LanguageChanged"/>.
/// ViewModel'ы на это событие перевыкладывают свои уведомления
/// (<see cref="ViewModels.LocalizedViewModel"/>), поэтому перечитываются и те
/// строки, что не в разметке, а вычисляются в свойствах VM.
/// </summary>
public static class Loc
{
    /// <summary>Хранимое значение «язык системы» (в settings.json — пустая строка).</summary>
    public const string FollowSystem = "";

    public const string English = "en";
    public const string Russian = "ru";

    /// <summary>Языки, которые можно выбрать в интерфейсе (плюс «как в системе»).</summary>
    public static readonly IReadOnlyList<string> SupportedLanguages =
        new[] { FollowSystem, English, Russian };

    private static readonly ResourceManager Manager =
        new("SoundMeeter.Resources.Strings", typeof(Loc).Assembly);

    private static readonly object Gate = new();

    private static ResourceDictionary? _strings;
    private static CultureInfo _culture = ResolveLanguage(FollowSystem);

    /// <summary>
    /// Язык приложения сменился. Подписчики — <see cref="ViewModels.LocalizedViewModel"/>
    /// и док-панель OBS (её HTML отдаётся с текстом текущего языка).
    /// </summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Текущая культура интерфейса.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Двухбуквенный код текущего языка ("ru"/"en").</summary>
    public static string Language => _culture.TwoLetterISOLanguageName;

    /// <summary>
    /// Выбранное пользователем значение: <see cref="FollowSystem"/>, <see cref="English"/>
    /// или <see cref="Russian"/>. Отличается от <see cref="Language"/>, когда выбран
    /// «язык системы» — его показывают в переключателе отмеченным.
    /// </summary>
    public static string RequestedLanguage { get; private set; } = FollowSystem;

    /// <summary>Код языка для разметки: <c>FrameworkElement.Language</c>.</summary>
    public static XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(_culture.IetfLanguageTag);

    /// <summary>
    /// Строка из ресурсов текущего языка. <paramref name="args"/> подставляются
    /// в плейсхолдеры {0}, {1}… обычным <c>string.Format</c>.
    /// </summary>
    public static string Get(string key, params object?[] args)
    {
        string? value = null;
        try
        {
            value = Manager.GetString(key, _culture);
        }
        catch (MissingManifestResourceException)
        {
            // Сборка собрана без Resources/Strings.resx — покажем ключ, разбираться
            // с этим всё равно придётся по строке в коде.
        }

        if (value is null) return Missing(key);
        if (args is null || args.Length == 0) return value;

        try
        {
            return string.Format(_culture, value, args);
        }
        catch (FormatException)
        {
            // Кривые плейсхолдеры в переводе не должны ронять интерфейс: показываем
            // строку как есть, чинить перевод — при следующей правке ресурсов.
            return value;
        }
    }

    /// <summary>
    /// Подключает словарь строк к приложению. Вызывается один раз из
    /// <c>App.OnStartup</c> до создания первого окна.
    /// </summary>
    public static void Install(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);

        lock (Gate)
        {
            if (_strings is null)
            {
                _strings = new ResourceDictionary();
                app.Resources.MergedDictionaries.Add(_strings);
            }
        }

        ReloadResources();
    }

    /// <summary>
    /// Переключает язык интерфейса. <paramref name="code"/> — <see cref="FollowSystem"/>,
    /// <see cref="English"/> или <see cref="Russian"/>; неизвестное значение и пустая
    /// строка трактуются как «язык системы».
    /// </summary>
    public static void SetLanguage(string? code)
    {
        var requested = Normalize(code);
        var culture = ResolveLanguage(requested);
        RequestedLanguage = requested;

        if (Equals(culture, _culture)) return;

        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        ReloadResources();
        ApplyElementLanguage();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Смена языка без записи в настройки — для тестов и харнессов.
    /// </summary>
    internal static void ForceLanguage(string? code)
    {
        var culture = ResolveLanguage(Normalize(code));
        RequestedLanguage = Normalize(code);
        if (Equals(culture, _culture)) return;

        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        ReloadResources();
        ApplyElementLanguage();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// <c>FrameworkElement.Language</c> влияет не только на поиск ресурсов WPF, но и
    /// на разделитель в <c>StringFormat</c> («0.0» против «0,0»). Проставляем открытым
    /// окнам; окна, созданные позже (MIDI, журнал, обновление), берут язык в своём
    /// конструкторе из <see cref="XmlLanguage"/>, а попапы наследуют его от родителя.
    /// </summary>
    public static void ApplyElementLanguage()
    {
        var app = Application.Current;
        if (app is null) return;

        var language = XmlLanguage;
        foreach (Window window in app.Windows)
            window.Language = language;
    }

    /// <summary>
    /// Перечитывает словарь строк. Ключи из спутника накладываются на нейтральные,
    /// поэтому непереведённый ключ показывает английский текст, а не пустой элемент.
    /// </summary>
    private static void ReloadResources()
    {
        var target = _strings;
        if (target is null) return;

        var values = ReadAllValues();
        target.Clear();
        foreach (var pair in values)
            target[pair.Key] = pair.Value;
    }

    private static Dictionary<string, string> ReadAllValues()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var culture in new[] { CultureInfo.InvariantCulture, _culture })
            foreach (var pair in ReadResourceSet(culture))
                result[pair.Key] = pair.Value;
        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> ReadResourceSet(CultureInfo culture)
    {
        ResourceSet? set;
        try
        {
            set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: true);
        }
        catch (MissingManifestResourceException)
        {
            yield break;
        }

        if (set is null) yield break;

        var enumerator = set.GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
            {
                if (enumerator.Key is string key && enumerator.Value is string value)
                    yield return new KeyValuePair<string, string>(key, value);
            }
        }
        finally
        {
            (enumerator as IDisposable)?.Dispose();
        }
    }

    /// <summary>Код языка, приведённый к одному из поддерживаемых.</summary>
    private static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return FollowSystem;

        var trimmed = code.Trim();
        if (trimmed.StartsWith(Russian, StringComparison.OrdinalIgnoreCase)) return Russian;
        if (trimmed.StartsWith(English, StringComparison.OrdinalIgnoreCase)) return English;
        return FollowSystem;
    }

    /// <summary>
    /// Культура по коду языка. «Язык системы» смотрит на язык интерфейса Windows;
    /// любой не-русский код даёт английский — это и есть нейтральный ресурс.
    ///
    /// Здесь принципиально нет кэшированных CultureInfo в статических полях: поля
    /// инициализируются в порядке объявления, а <see cref="_culture"/> объявлен
    /// выше и зовёт этот метод из своего инициализатора. Кэш в статическом поле
    /// ниже дал бы ему null на старте, и первый же Install() упал бы с
    /// ArgumentNullException в GetResourceSet. GetCultureInfo возвращает
    /// кэшированный неизменяемый экземпляр, так что новых объектов тут не будет.
    /// </summary>
    private static CultureInfo ResolveLanguage(string code)
    {
        var effective = code == FollowSystem ? SystemLanguage() : code;
        return effective == Russian
            ? CultureInfo.GetCultureInfo("ru-RU")
            : CultureInfo.GetCultureInfo("en-US");
    }

    private static string SystemLanguage()
    {
        try
        {
            var ui = CultureInfo.InstalledUICulture;
            if (ui.TwoLetterISOLanguageName.StartsWith(Russian, StringComparison.OrdinalIgnoreCase))
                return Russian;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // У cultures без установленного языка в системе свойства могут кидать —
            // молча откатываемся на английский.
        }

        return English;
    }

    /// <summary>
    /// Незакрытая строка в интерфейсе — это баг, а не пустой элемент: показываем
    /// сам ключ, чтобы его было видно на экране и в скриншоте багрепорта.
    /// </summary>
    private static string Missing(string key) => $"⟨{key}⟩";
}