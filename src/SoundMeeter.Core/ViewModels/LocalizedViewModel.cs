using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

/// <summary>
/// База для ViewModel'ов, у которых есть вычисляемые надписи (SM-C07).
///
/// Строки в разметке живут в словаре <see cref="Loc"/> и обновляются сами — там
/// стоит <c>{DynamicResource}</c>. А вот подпись, которую ViewModel считает сам
/// (<c>AppDropHint</c>, <c>Status</c>, <c>ServerToggleText</c>), перечитывается
/// движком привязки только при <c>PropertyChanged</c>. Поэтому на смену языка
/// класс поднимает уведомление с пустым именем — для WPF это сигнал «обнови всё
/// свойство», и обновляются в том числе уже открытые окна: словарь один на
/// приложение, а уведомление получают все живые ViewModel'ы.
///
/// Подписка снимается в <see cref="Dispose"/>. Для этого нужен и сам IDisposable:
/// стрипы пересоздаются на каждом <c>ChannelsChanged</c>, а без отписки они
/// накапливались бы в списке подписчиков <see cref="Loc.LanguageChanged"/>.
/// </summary>
/// <remarks>
/// <para>
/// Про поток выполнения: <see cref="Loc.LanguageChanged"/> поднимают только
/// <see cref="Loc.SetLanguage"/>, а его зовут два места — <c>App.OnStartup</c> и
/// <c>MainViewModel.SelectLanguage</c>, оба с UI-потока. Поэтому обновление
/// выполняется сразу, без диспетчера.
/// </para>
/// <para>
/// Раньше здесь стоял <c>Application.Current.Dispatcher</c> с проверкой
/// <c>CheckAccess()</c>: подстраховка на случай фонового потока. Она была
/// мёртвым кодом (ветка «с фонового потока» не срабатывала никогда), и именно
/// она держала WPF в ядре — из-за неё Core объявлял <c>UseWPF</c>. Подстраховку
/// перенесли в те ViewModel, которые получают события действительно из фоновых
/// потоков (MIDI, команды дока): там маршрутизация реальна и делается через
/// <see cref="IDispatcherService"/>.
/// </para>
/// </remarks>
public abstract class LocalizedViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    protected LocalizedViewModel() => Loc.LanguageChanged += OnLocLanguageChanged;

    /// <summary>
    /// Подсказка для наследников: что пересчитать при смене языка помимо
    /// уведомления по всем свойствам. Вызывается на UI-потоке.
    /// </summary>
    protected virtual void OnLanguageChangedCore()
    {
    }

    /// <summary>
    /// Выполнить действие в UI-потоке. По умолчанию — сразу: см. замечание про
    /// потоки в типе. Наследники, получающие события из фоновых потоков,
    /// переопределяют метод и маршрутизируют их через
    /// <see cref="IDispatcherService"/>.
    /// </summary>
    protected virtual void RunOnUiThread(Action action) => action();

    private void OnLocLanguageChanged(object? sender, EventArgs e)
    {
        RunOnUiThread(RefreshAll);
    }

    private void RefreshAll()
    {
        if (_disposed) return;
        OnLanguageChangedCore();
        // Пустое имя — соглашение WPF «перечитать все свойства».
        OnPropertyChanged(string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loc.LanguageChanged -= OnLocLanguageChanged;
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>Освобождение собственных подписок наследника.</summary>
    protected virtual void DisposeCore()
    {
    }
}