using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.ViewModels;

// Восстановление пресета, отслеживание изменений и сохранение настроек микшера.
public partial class MainViewModel
{
    private readonly SettingsService _settings;
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _dirtyLock = new();
    private readonly ILogger _logger = AppLog.For<MainViewModel>();
    private bool _dirty;

    /// <summary>
    /// Применяет сохранённый пресет на старте приложения и автозапускает движок.
    /// </summary>
    public void Restore(AppSettings? saved)
    {
        if (saved != null)
        {
            _engine.ApplyPreset(saved);
            if (saved.EngineWasRunning) _engine.Start();

            _logger.LogInformation(
                "Preset restored: {Inputs} inputs, {Buses} buses, engine was running: {WasRunning}, MIDI: {Midi}",
                saved.Inputs.Count, saved.Outputs.Count, saved.EngineWasRunning,
                string.IsNullOrWhiteSpace(saved.Midi?.DeviceName) ? "not set" : saved.Midi.DeviceName);
        }
        else
        {
            // Каналов нет: микшер начинается пустым, и это осознанное решение,
            // а не недостаток настроек. Набор каналов задаёт пользователь —
            // см. WasapiAudioEngine: осмотр каталога не добавляет стрипов.
            if (_settings.HasUnsupportedNewerSchema)
                _logger.LogWarning("Настройки не применены: {Report}", _settings.LoadReport);
            else
                _logger.LogInformation("{Report}", Loc.Get("Sm.Settings.NoPreset"));
        }

        // Открываем MIDI-устройство, сохранённое в пресете.
        _midi.Open(_engine.Midi.DeviceName);

        // Сервер док-панели поднимаем после восстановления стрипов: к моменту
        // первого подключения панели набор каналов уже настоящий.
        if (DockSettings.Enabled) StartDock();
    }

    /// <summary>
    /// Немедленно сохраняет настройки (при закрытии окна).
    /// Синхронная запись: блокирующий async из UI-потока на месте события
    /// Closed вызывает deadlock и процесс не завершается.
    /// </summary>
    public void SaveNow()
    {
        try
        {
            _settings.SaveSync(_engine.CreateSnapshot());
            _logger.LogDebug("Settings saved on exit");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить настройки при выходе: {Message}", ex.Message);
        }
    }

    private void MarkDirty()
    {
        lock (_dirtyLock) _dirty = true;
    }

    private void SavePool()
    {
        bool shouldSave;
        lock (_dirtyLock)
        {
            shouldSave = _dirty;
            _dirty = false;
        }
        if (!shouldSave) return;

        var snapshot = _engine.CreateSnapshot();
        _ = SaveAsync(snapshot);
    }

    private async Task SaveAsync(AppSettings snapshot)
    {
        try
        {
            await _settings.SaveAsync(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Фоновое сохранение настроек не удалось: {Message}", ex.Message);
        }
    }
}