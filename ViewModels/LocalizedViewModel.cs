using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.Services;
using System.Windows;

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

    private void OnLocLanguageChanged(object? sender, EventArgs e)
    {
        // Событие может прийти с фонового потока (перенос приложения, опрос
        // устройств), а уведомления свойств трогать оттуда нельзя.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) RefreshAll();
        else dispatcher.BeginInvoke(RefreshAll);
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