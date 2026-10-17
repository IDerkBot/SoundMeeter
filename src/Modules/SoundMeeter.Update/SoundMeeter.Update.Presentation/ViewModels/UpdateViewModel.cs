using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Core.Contract;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Framework.Contract.Implementation;
using SoundMeeter.Update.Contract;
using SoundMeeter.Update.Entities;

namespace SoundMeeter.Update.Presentation.ViewModels;

/// <summary>
/// VM модуля обновлений: тихая проверка после старта и ручная по кнопке.
/// Окно установки открывает код-behind — так же, как это сделано для MIDI-привязок.
/// </summary>
public partial class UpdateViewModel : LocalizedViewModel
{
    private readonly IUpdateService _updateService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger _logger = AppLog.For<UpdateViewModel>();

    public UpdateViewModel(IUpdateService updateService, IDispatcherService dispatcherService)
    {
        _updateService = updateService;
        _dispatcherService = dispatcherService;
    }

    /// <summary>Сервис обновлений (окно установки берёт план и отчёт).</summary>
    public IUpdateService UpdateService => _updateService;

    /// <summary>Релиз, найденный последней проверкой. null — обновлений нет.</summary>
    public UpdateInfo? PendingUpdate { get; private set; }

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateBanner = "";

    [ObservableProperty]
    private string _updateStatus = "";

    /// <summary>
    /// Тихая проверка после показа окна: только баннер, без модальных окон.
    /// </summary>
    public async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            UpdateStatus = Loc.Get("Sm.Update.Status.Checking", _updateService.CurrentVersion);
            var result = await _updateService.CheckAsync();
            await _dispatcherService.InvokeAsync(() => ApplyCheckResult(result));
        }
        catch (Exception ex)
        {
            // Задача запускается fire-and-forget: исключение here уйдёт в unobserved.
            _logger.LogError(ex, "Тихая проверка обновлений не удалась: {Message}", ex.Message);
        }
    }

    /// <summary>Ручная проверка по кнопке: сообщаем и про обновление, и про его отсутствие.</summary>
    public async Task CheckUpdatesAsync()
    {
        UpdateStatus = Loc.Get("Sm.Update.Status.Checking", _updateService.CurrentVersion);
        var result = await _updateService.CheckAsync();
        await _dispatcherService.InvokeAsync(() => ApplyCheckResult(result));
    }

    /// <summary>Скрыть баннер до следующей проверки.</summary>
    [RelayCommand]
    private void DismissUpdate() => IsUpdateAvailable = false;

    private void ApplyCheckResult(UpdateCheckResult result)
    {
        if (!result.Success)
        {
            PendingUpdate = null;
            IsUpdateAvailable = false;
            UpdateBanner = "";
            UpdateStatus = Loc.Get("Sm.Update.Status.CheckFailed", result.Message);
            return;
        }

        if (!result.UpdateAvailable || result.Update is not { } update)
        {
            PendingUpdate = null;
            IsUpdateAvailable = false;
            UpdateBanner = "";
            UpdateStatus = Loc.Get("Sm.Update.Status.UpToDate", _updateService.CurrentVersion);
            return;
        }

        PendingUpdate = update;
        IsUpdateAvailable = true;
        UpdateBanner = update.CanInstall
            ? Loc.Get("Sm.Update.Banner.Installable", update.Version, _updateService.CurrentVersion)
            : Loc.Get("Sm.Update.Banner.NoPortable", update.Version, _updateService.CurrentVersion);
        UpdateStatus = Loc.Get("Sm.Update.Status.Available");
    }

    protected override void OnLanguageChangedCore()
    {
        OnPropertyChanged(nameof(UpdateBanner));
        OnPropertyChanged(nameof(UpdateStatus));
    }
}
