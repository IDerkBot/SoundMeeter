using SoundMeeter.Models;
using SoundMeeter.Services;

namespace SoundMeeter.ViewModels;

// Восстановление пресета, отслеживание изменений и сохранение настроек микшера.
public partial class MainViewModel
{
    private readonly SettingsService _settings;
    private readonly System.Threading.Timer _saveTimer;
    private readonly object _dirtyLock = new();
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
        }
        else
        {
            _engine.EnsureDefaultStrips();
        }

        // Открываем MIDI-устройство, сохранённое в пресете.
        _midi.Open(_engine.Midi.DeviceName);
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"Save failed: {ex.Message}");
        }
    }
}
