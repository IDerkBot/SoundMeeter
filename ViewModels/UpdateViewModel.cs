using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Состояние диалога обновления: показ changelog, скачивание с прогрессом,
/// распаковка и передача подмены файлов фоновому скрипту.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private CancellationTokenSource? _cancellation;

    public UpdateViewModel(IUpdateService updates, UpdateInfo update)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        Update = update ?? throw new ArgumentNullException(nameof(update));
    }

    public UpdateInfo Update { get; }

    public string CurrentVersion => _updates.CurrentVersion.ToString();

    public string NewVersion => Update.Version.ToString();

    public string Title => Update.Title;

    public string PublishedAt => Update.PublishedAt is { } date
        ? date.ToLocalTime().ToString("d MMMM yyyy")
        : "";

    public string ReleaseNotes => string.IsNullOrWhiteSpace(Update.ReleaseNotes)
        ? "Релиз без описания."
        : Update.ReleaseNotes;

    public string AssetName => Update.Asset is { } asset ? $"{asset.Name} ({asset.HumanSize})" : "";

    /// <summary>Нет zip-ассета — автоустановка невозможна, только ссылка на релиз.</summary>
    public bool CanInstall => Update.CanInstall;

    /// <summary>Событие «обновление передано скрипту» — окно закрывается, приложение выключается.</summary>
    public event EventHandler? InstallCompleted;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (Update.Asset is not { } asset || IsBusy) return;

        IsBusy = true;
        Progress = 0;
        Status = $"Загрузка {asset.Name}…";
        _cancellation = new CancellationTokenSource();

        try
        {
            // Progress<double> сконструирован на UI-потоке, поэтому отчёт автоматически
            // маршалится обратно в UI через SynchronizationContext.
            var progress = new Progress<double>(p => Progress = p * 100);
            var zip = await _updates.DownloadAsync(asset, progress, _cancellation.Token);

            Status = "Распаковка…";
            var payload = _updates.Extract(zip, Update.TagName);

            Status = "Подмена файлов и перезапуск…";
            _updates.ApplyAndRestart(payload, Update);

            Progress = 100;
            Status = "Обновление устанавливается. Приложение будет закрыто.";
            InstallCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Загрузка отменена.";
            IsBusy = false;
        }
        catch (Exception ex)
        {
            Status = "Не удалось установить обновление: " + ex.Message;
            IsBusy = false;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private void OpenReleasePage() => _updates.OpenReleasePage(Update);
}
