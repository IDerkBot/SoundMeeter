using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Состояние диалога обновления: показ changelog, скачивание с прогрессом,
/// распаковка и передача подмены файлов фоновому скрипту.
/// </summary>
public partial class UpdateViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly ILogger _logger = AppLog.For<UpdateViewModel>();
    private CancellationTokenSource? _cancellation;

    public UpdateViewModel(IUpdateService updates, UpdateInfo update)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        Update = update ?? throw new ArgumentNullException(nameof(update));
    }

    public UpdateInfo Update { get; }

    /// <summary>
    /// Показ плана обновления и запрос согласия. Задаётся окном (диалог — UI),
    /// чтобы логика применения не зависела от WPF. Возврат false отменяет установку.
    /// </summary>
    public Func<UpdatePlan, string, bool>? ConfirmPlan { get; set; }

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

            // Сверка целостности до подмены: повреждённый архив не должен
            // привести к удалению файлов установленной сборки (SM-A06).
            Status = "Проверка архива…";
            UpdatePlan plan;
            try
            {
                plan = _updates.PlanUpdate(payload, Update);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Архив обновления {Tag} не прошёл проверку целостности", Update.TagName);
                Status = "Обновление отклонено: " + ex.Message;
                IsBusy = false;
                return;
            }

            // Пользователь видит, что именно будет удалено, и может отказаться.
            var report = _updates.FormatPlanReport(plan);
            if (ConfirmPlan is { } confirm && !confirm(plan, report))
            {
                _logger.LogInformation("Установка обновления {Tag} отменена пользователем", Update.TagName);
                Status = "Установка отменена.";
                IsBusy = false;
                return;
            }

            Status = "Подмена файлов и перезапуск…";
            _updates.ApplyAndRestart(plan);

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
            _logger.LogError(ex, "Установка обновления не удалась: {Message}", ex.Message);
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
