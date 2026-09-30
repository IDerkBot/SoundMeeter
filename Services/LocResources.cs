using System;
using System.Windows;
using System.Windows.Markup;

namespace SoundMeeter.Services;

/// <summary>
/// WPF-часть локализации (SM-C07): подключение строк к разметке.
///
/// <see cref="Loc"/> (в сборке <c>SoundMeeter.Audio</c>) умеет отдавать строку по
/// ключу и хранит язык, но не знает про WPF — это позволяет ядру обработки звука
/// не тянуть <c>PresentationFramework</c> ради строки в сообщении. Здесь словарь
/// строк подмешивается в <see cref="Application.Resources"/>, и разметка читает
/// его через <c>{DynamicResource Sm.Ключ}</c>.
///
/// Именно <c>DynamicResource</c>, а не <c>StaticResource</c>/<c>x:Static</c>:
/// смена языка перезаписывает словарь на месте, и все уже созданные окна и
/// попапы перечитывают значения сами, без пересоздания визуального дерева.
///
/// <c>FrameworkElement.Language</c> влияет не только на поиск ресурсов WPF, но и
/// на разделитель в <c>StringFormat</c> («0.0» против «0,0»), поэтому язык
/// проставляется и открытым окнам.
/// </summary>
public static class LocResources
{
    private static ResourceDictionary? _strings;
    private static bool _subscribed;

    /// <summary>Код языка для разметки: <c>FrameworkElement.Language</c>.</summary>
    public static XmlLanguage XmlLanguage => XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);

    /// <summary>
    /// Подключает словарь строк к приложению. Вызывается из
    /// <c>App.OnStartup</c> до создания первого окна.
    /// </summary>
    public static void Install(Application app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (_strings is null)
        {
            _strings = new ResourceDictionary();
            app.Resources.MergedDictionaries.Add(_strings);
        }

        if (!_subscribed)
        {
            Loc.LanguageChanged += OnLanguageChanged;
            _subscribed = true;
        }

        Reload();
    }

    /// <summary>
    /// Смена языка: перезаливаем словарь и обновляем открытые окна. Подписка в
    /// <see cref="Install"/> — иначе первая же смена языка молча ничего бы не
    /// сделала с интерфейсом.
    /// </summary>
    private static void OnLanguageChanged(object? sender, EventArgs e)
    {
        Reload();
        ApplyElementLanguage();
    }

    /// <summary>Перечитывает строки в словарь на месте.</summary>
    private static void Reload()
    {
        var target = _strings;
        if (target is null) return;

        var values = Loc.ReadAllValues();
        target.Clear();
        foreach (var pair in values)
            target[pair.Key] = pair.Value;
    }

    /// <summary>
    /// Проставляет язык открытым окнам; окна, созданные позже (MIDI, журнал,
    /// обновление), берут язык в своём конструкторе из <see cref="XmlLanguage"/>,
    /// а попапы наследуют его от родителя.
    /// </summary>
    public static void ApplyElementLanguage()
    {
        var app = Application.Current;
        if (app is null) return;

        var language = XmlLanguage;
        foreach (Window window in app.Windows)
            window.Language = language;
    }
}
