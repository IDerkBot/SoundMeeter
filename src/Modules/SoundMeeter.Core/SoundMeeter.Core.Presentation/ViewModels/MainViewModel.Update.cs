using SoundMeeter.Update.Entities;
using SoundMeeter.Update.UseCases;


namespace SoundMeeter.Framework.Presentation.ViewModels;

// Проверка обновлений SoundMeeter через GitHub Releases вынесена в
// UpdateViewModel (SoundMeeter.Update.UseCases). Этот partial оставлен для
// совместимости: все вызовы делегируются.
public partial class MainViewModel
{
    /// <summary>Доступ к VM модуля обновлений (для окна установки и баннера).</summary>
    public UpdateViewModel Update => _updateVm;

    /// <summary>Релиз, найденный последней проверкой. null — обновлений нет.</summary>
    public UpdateInfo? PendingUpdate => _updateVm.PendingUpdate;

    /// <summary>Обновление найдено — интерфейс показывает баннер.</summary>
    public bool IsUpdateAvailable
    {
        get => _updateVm.IsUpdateAvailable;
        set
        {
            if (_updateVm.IsUpdateAvailable != value)
                _updateVm.IsUpdateAvailable = value;
        }
    }

    /// <summary>Текст баннера обновления.</summary>
    public string UpdateBanner
    {
        get => _updateVm.UpdateBanner;
        set
        {
            if (_updateVm.UpdateBanner != value)
                _updateVm.UpdateBanner = value;
        }
    }

    /// <summary>Статусная строка проверки обновлений.</summary>
    public string UpdateStatus
    {
        get => _updateVm.UpdateStatus;
        set
        {
            if (_updateVm.UpdateStatus != value)
                _updateVm.UpdateStatus = value;
        }
    }

    /// <summary>Тихая проверка после показа окна: только баннер, без модальных окон.</summary>
    public Task CheckUpdatesOnStartupAsync() => _updateVm.CheckUpdatesOnStartupAsync();

    /// <summary>Ручная проверка по кнопке: сообщаем и про обновление, и про его отсутствие.</summary>
    public Task CheckUpdatesAsync() => _updateVm.CheckUpdatesAsync();

    /// <summary>Скрыть баннер до следующей проверки.</summary>
    public void DismissUpdate() => _updateVm.DismissUpdateCommand.Execute(null);
}
