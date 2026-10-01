using System;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;

namespace SoundMeeter.Services;

/// <summary>
/// Локализация интерфейса (SM-C07), часть без UI.
///
/// Единый источник строк — <c>Resources/Strings.resx</c> (нейтральная,
/// английская) плюс спутник <c>Strings.ru.resx</c>. Отсюда обслуживаются оба
/// канала доступа:
///
/// * код — <see cref="Get(string, object?[])"/>. Строки, собираемые в рантайме
///   (статусы, сообщения об ошибках, подписи списков), идут только этим путём;
/// * разметка — через словарь, который держит <c>LocResources</c> в приложении.
///   Он же подписан на <see cref="LanguageChanged"/> и перечитывает значения при
///   смене языка, поэтому окна и попапы обновляются сами, без пересоздания
///   визуального дерева.
///
/// Почему класс разделён: всё выше — обычный .NET, а словарь
/// <c>ResourceDictionary</c>, <c>FrameworkElement.Language</c> и
/// <c>XmlLanguage</c> — это WPF. Ядро обработки звука не должно тянуть WPF
/// только ради строки в сообщении, поэтому установка словаря и язык элементов
/// живут в приложении (см. <c>Services\LocResources.cs</c>).
///
/// Переключение языка: <see cref="SetLanguage"/> ставит <see cref="CultureInfo"/>
/// текущего потока и поднимает <see cref="LanguageChanged"/>; ViewModel'ы на это
/// событие перевыкладывают свои уведомления, поэтому перечитываются и те строки,
/// что не в разметке, а вычисляются в свойствах VM.
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

    private static CultureInfo _culture = ResolveLanguage(FollowSystem);

    /// <summary>
    /// Язык приложения сменился. Подписчики — <c>ViewModels.LocalizedViewModel</c>
    /// (перечитывают свои строки) и <c>LocResources</c> (перезаливает словарь,
    /// из которого разметка берёт строки через <c>{DynamicResource}</c>).
    /// </summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>Текущая культура интерфейса.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Двухбуквенный код текущего языка ("ru"/"en").</summary>
    public static string Language => _culture.TwoLetterISOLanguageName;

    /// <summary>
    /// Выбранный пользователем значение: <see cref="FollowSystem"/>,
    /// <see cref="English"/> или <see cref="Russian"/>. Отличается от
    /// <see cref="Language"/>, когда выбран «язык системы» — их показывают в
    /// переключателе отмеченным.
    /// </summary>
    public static string RequestedLanguage { get; private set; } = FollowSystem;

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
    /// Все строки текущего языка для словаря разметки. Ключи из спутника
    /// накладываются на нейтральные, поэтому непереведённый ключ показывает
    /// английский текст, а не пустой элемент.
    /// </summary>
    public static Dictionary<string, string> ReadAllValues()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var culture in new[] { CultureInfo.InvariantCulture, _culture })
            foreach (var pair in ReadResourceSet(culture))
                result[pair.Key] = pair.Value;
        return result;
    }

    /// <summary>Переключает язык интерфейса.</summary>
    public static void SetLanguage(string? code)
    {
        var requested = Normalize(code);
        var culture = ResolveLanguage(requested);
        RequestedLanguage = requested;

        if (Equals(culture, _culture)) return;

        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Смена языка без записи в настройки — для тестов. Отличие от
    /// <see cref="SetLanguage"/> в том, что переключает и на тот же самый код
    /// (обычная смена игнорирует «ничего не поменялось»).
    /// </summary>
    internal static void ForceLanguage(string? code)
    {
        var culture = ResolveLanguage(Normalize(code));
        RequestedLanguage = Normalize(code);
        if (Equals(culture, _culture)) return;

        _culture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
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
    /// ниже дал бы ему null на старте. GetCultureInfo возвращает кэшированный
    /// неизменяемый экземпляр, так что новых объектов тут не будет.
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
