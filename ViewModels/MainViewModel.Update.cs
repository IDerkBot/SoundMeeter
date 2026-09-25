using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

// Проверка обновлений SoundMeeter через GitHub Releases. Окно установки открывает
// код- behind (Views/UpdateWindow.xaml) — так же, как это сделано для MIDI-привязок.
public partial class MainViewModel
{
    private readonly IUpdateService _updateService;

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
            UpdateStatus = $"SoundMeeter {_updateService.CurrentVersion}: проверка обновлений…";
            var result = await _updateService.CheckAsync();
            await _dispatcherService.InvokeAsync(() => ApplyCheckResult(result));
        }
        catch (Exception ex)
        {
            // Задача запускается fire-and-forget: исключение here уйдёт в unobserved.
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>Ручная проверка по кнопке: сообщаем и про обновление, и про его отсутствие.</summary>
    public async Task CheckUpdatesAsync()
    {
        UpdateStatus = $"SoundMeeter {_updateService.CurrentVersion}: проверка обновлений…";
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
            UpdateStatus = "Проверка обновлений не удалась: " + result.Message;
            return;
        }

        if (!result.UpdateAvailable || result.Update is not { } update)
        {
            PendingUpdate = null;
            IsUpdateAvailable = false;
            UpdateBanner = "";
            UpdateStatus = $"SoundMeeter {_updateService.CurrentVersion}: обновлений нет";
            return;
        }

        PendingUpdate = update;
        IsUpdateAvailable = true;
        UpdateBanner = update.CanInstall
            ? $"SoundMeeter {update.Version} доступен (у вас {_updateService.CurrentVersion})."
            : $"SoundMeeter {update.Version} доступен (у вас {_updateService.CurrentVersion}), но portable-архива в релизе нет.";
        UpdateStatus = "Доступно обновление";
    }
}
