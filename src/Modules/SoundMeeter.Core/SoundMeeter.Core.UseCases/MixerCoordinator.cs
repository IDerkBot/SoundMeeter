using Microsoft.Extensions.Logging;
using SoundMeeter.Audio.Contract;
using SoundMeeter.ChangeLanguage.UseCases;
using SoundMeeter.Docking.UseCases;
using SoundMeeter.Framework.Contract;
using SoundMeeter.Framework.Contract.Implementation;
using SoundMeeter.Infrastructure.Contract.Implementation;
using SoundMeeter.Infrastructure.Entities;
using SoundMeeter.Midi.UseCases;
using SoundMeeter.TextToSpeech.UseCases;
using SoundMeeter.Twitch.UseCases;

namespace SoundMeeter.Core.UseCases;

/// <summary>
/// Оркестрация микшера: подписки на движок, таймер метров, таймер сохранения,
/// восстановление пресета и импорт/экспорт настроек.
///
/// Почему это отдельный объект, а не методы главной VM. Главная VM делала всё
/// сразу: держала коллекции стрипов для разметки, создавала их элементы,
/// подписывалась на движок, мерила метры и писала settings.json. Из-за этого
/// сценарии и представление лежали в одной сборке: сценарий не мог обойтись без
/// ViewModel'ов полос, а ViewModel не мог быть собран без сценариев. Здесь
/// осталось ровно то, что о представлении не знает: события движка, таймеры и
/// файлы. Наружу выходят три уведомления — <see cref="MeterTick"/>,
/// <see cref="ChannelsChanged"/>, <see cref="StateChanged"/> — и методы, которые
/// вызывает представление.
///
/// Границы здесь не переименованы, а прорезаны по-настоящему: представление
/// владеет ViewModel'ами полос и разметкой, этот класс — всем, что живёт и
/// меняется без участия пользователя.
/// </summary>
public sealed class MixerCoordinator : IDisposable
{
    private readonly IAudioEngine _engine;
    private readonly SettingsService _settings;
    private readonly IUiTimer _meterTimer;
    private readonly StripProvider _strips;
    private readonly DockController _dock;
    private readonly TtsViewModel _ttsVm;
    private readonly TwitchViewModel _twitchVm;
    private readonly MidiController _midi;
    private readonly SelectMidiDeviceUseCase _selectMidiDevice;
    private readonly SettingsTransferService _transfer;
    private readonly AppBehaviourService _behaviour;
    private readonly ILogger _logger = AppLog.For<MixerCoordinator>();

    private readonly System.Threading.Timer _saveTimer;
    private readonly object _dirtyLock = new();
    private bool _dirty;
    private bool _disposed;

    /// <summary>
    /// Период обновления метров и снимка для дока. 33 мс — около 30 Гц: глаз
    /// не отличает, а снимок дока не должен упираться в частоту разметки.
    /// </summary>
    private static readonly TimeSpan MeterInterval = TimeSpan.FromMilliseconds(33);

    /// <summary>Тик таймера метров: представлению пора обновить столбики.</summary>
    public event Action? MeterTick;

    /// <summary>Состав стрипов изменился — представлению надо пересобрать коллекции.</summary>
    public event Action? ChannelsChanged;

    /// <summary>Движок запустился или остановился.</summary>
    public event Action<bool>? StateChanged;

    /// <summary>Модуль MIDI (для окна привязок).</summary>
    public MidiController Midi => _midi;

    /// <summary>Модуль док-панели OBS (для окна настроек).</summary>
    public DockController Dock => _dock;

    /// <summary>Модуль синтеза речи (для окна настроек).</summary>
    public TtsViewModel Tts => _ttsVm;

    /// <summary>Интеграция с Twitch (для окна настроек).</summary>
    public TwitchViewModel Twitch => _twitchVm;

    public MixerCoordinator(
        IAudioEngine engine,
        SettingsService settings,
        StripProvider strips,
        DockController dock,
        TtsViewModel ttsVm,
        TwitchViewModel twitchVm,
        MidiController midi,
        SelectMidiDeviceUseCase selectMidiDevice,
        IUiTimer meterTimer,
        AppBehaviourService behaviour)
    {
        _engine = engine;
        _settings = settings;
        _strips = strips;
        _dock = dock;
        _ttsVm = ttsVm;
        _twitchVm = twitchVm;
        _midi = midi;
        _selectMidiDevice = selectMidiDevice;
        _behaviour = behaviour;
        // Импорт/экспорт настроек: нужен только SettingsService, поэтому собирается
        // здесь, а не через DI — отдельный сервис в контейнере был бы лишней
        // зависимостью ради двух методов над файлом.
        _transfer = new SettingsTransferService(settings);

        _engine.ChannelsChanged += OnChannelsChanged;
        _engine.StateChanged += OnStateChanged;
        _twitchVm.SubscribeTwitch();

        _meterTimer = meterTimer;
        _meterTimer.Interval = MeterInterval;
        _meterTimer.Ticked += OnMeterTick;
        _meterTimer.Start();

        _saveTimer = new System.Threading.Timer(_ => SavePool(), null, 5000, 2000);
    }

    /// <summary>
    /// Отдаёт наружу состав стрипов. Отдельный метод, а не доступ к
    /// <see cref="StripProvider"/> снаружи: наполняет список создатель
    /// ViewModel'ов, то есть представление, и реестр стрипов — единственное
    /// место, где слои встречаются.
    /// </summary>
    public void PublishStrips(IEnumerable<IInputStrip> inputs, IEnumerable<IOutputStrip> buses) =>
        _strips.Reset(inputs, buses);

    /// <summary>
    /// Настройки изменились — пора сохранить.
    ///
    /// Вызывается из полос при каждом движении регулятора, поэтому здесь только
    /// взведение флага: запись делает фоновый таймер, и синхронная запись на
    /// каждом кадре дёргала бы диск.
    /// </summary>
    public void MarkDirty()
    {
        lock (_dirtyLock) _dirty = true;
    }

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

        // Открываем MIDI-устройство, сохранённое в пресете. Через сценарий, а не
        // напрямую в порт: он проверяет, что устройство вообще есть, и откатывает
        // зажатые PTT-кнопки. На старте их быть не может, но при смене пресета
        // на живом движке — может, и без отката микрофон остался бы открытым.
        var midi = _selectMidiDevice.Execute(_engine.Midi.DeviceName);
        if (!midi.IsOpen && !string.IsNullOrWhiteSpace(_engine.Midi.DeviceName))
            _logger.LogWarning("{Reason}", Loc.Get(midi.MessageKey));

        // Сервер док-панели поднимаем после восстановления стрипов: к моменту
        // первого подключения панели набор каналов уже настоящий.
        if (_dock.DockSettings.Enabled) _dock.StartDock();

        // Модуль синтеза речи (SM-E01) — по той же причине и в том же порядке:
        // «куда говорить» можно решить только после того, как стрипы на месте.
        RestoreTts();
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

    /// <summary>
    /// Пишет все настройки в файл: стрипы, роутинг, эффекты, MIDI, маршруты
    /// приложений, скрытые устройства, док OBS, язык, уровень журнала и
    /// поведение приложения.
    /// </summary>
    public SettingsExportResult ExportSettings(string path) => _transfer.Export(path);

    /// <summary>
    /// Заменяет текущие настройки настройками из файла.
    ///
    /// Импорт — не «применить пресет»: он трогает всё, включая то, что лежит
    /// вне движка (док, язык, автозапуск), поэтому порядок важен. Сначала
    /// движок, потом живые поля, и только потом всё, у чего есть побочный эффект.
    /// Возвращает <c>false</c>, если файл не прошёл проверку, и ничего не меняет.
    /// </summary>
    public SettingsImportResult ImportSettings(string path)
    {
        var read = _transfer.Read(path);
        if (!read.Success || read.Settings is not { } imported) return read;

        // Копия того, что сейчас настроено. Импорт переписывает всё разом, а
        // откатить его без копии нечем, и settings.json в этот момент ещё цел —
        // записать его как .bak дешевле, чем объяснять пользователю потерю микшера.
        var backup = _settings.BackupCurrent();

        // Движок: стрипы, роутинг, MIDI, скрытые устройства. ApplyPreset поднимает
        // ChannelsChanged, и полосы пересоздаются уже из импортированных данных.
        _engine.ApplyPreset(imported);

        // Поля, которых снимок движка не знает, — копиями, а не ссылками на
        // разобранный файл: он больше никто не использует, а список правил
        // приложений потом ещё правит AudioService.
        _settings.AdoptAppLevelFields(imported);

        // Язык и уровень журнала живут не в Settings, а в Loc и AppLog: значение
        // из файла кладём в снимок и сразу применяем, иначе следующее же сохранение
        // записало бы обратно прежнее.
        ApplyImportedLanguage(imported.Language);
        AppLog.SetLevel(AppLog.ParseLevel(imported.LogLevel));

        // Док OBS: сервер перезапускается, если изменился порт, и глушится при
        // выключении. Само значение порта уже в снимке.
        _dock.ApplyDockSettings(_settings.Settings.ObsDock, saveNow: false);

        // Модуль синтеза речи (SM-E01): настройки уже в живом снимке, но модуль
        // надо перевключить под них — как сервер дока. Отдельно от ApplyDockSettings
        // ещё и потому, что ApplyPreset пересоздал стрипы: если файл пришёл с
        // другой машины, целевой канал мог смениться, и без этого модуль продолжил
        // бы говорить в старый.
        RestoreTts();

        // MIDI-устройство из файла — как при старте, в Restore.
        _selectMidiDevice.Execute(_engine.Midi.DeviceName);

        // Поведение приложения. Источник истины по автозапуску — система: файл
        // из чужой машины не должен молча регистрировать автозапуск здесь, поэтому
        // значение из файла остаётся пожеланием, а показывает его вызывающая VM.
        _behaviour.Restore();

        // Состояние движка приводим к файлу: импорт означает «сделать всё как там».
        if (imported.EngineWasRunning && !_engine.IsRunning) _engine.Start();
        else if (!imported.EngineWasRunning && _engine.IsRunning) _engine.Stop();

        SaveNow();

        _logger.LogInformation(
            "Settings imported from {Path}: {Inputs} inputs, {Buses} buses, was running: {WasRunning}",
            path, imported.Inputs.Count, imported.Outputs.Count, imported.EngineWasRunning);

        return read with { BackupPath = backup ?? string.Empty };
    }

    /// <summary>
    /// Поднять модуль синтеза речи после восстановления пресета или импорта.
    /// Порядок тот же, что у дока: стрипы уже на месте, поэтому «куда говорить»
    /// разрешается сразу.
    /// </summary>
    public void RestoreTts() => _ttsVm.RestoreTts();

    /// <summary>
    /// Полное завершение оркестрации: таймеры, модули, движок.
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;

        _saveTimer.Dispose();
        _meterTimer.Stop();
        _meterTimer.Ticked -= OnMeterTick;
        _engine.ChannelsChanged -= OnChannelsChanged;
        _engine.StateChanged -= OnStateChanged;

        _dock.Dispose();
        _ttsVm.Dispose();
        _twitchVm.Dispose();
        _midi.Dispose();

        _engine.Stop();
        _disposed = true;
    }

    public void Dispose() => Shutdown();

    /// <summary>
    /// Язык из файла. Неизвестный код равносилен «язык системы» — ровно как при
    /// старте, иначе чужой файл оставил бы интерфейс без строк.
    /// </summary>
    private void ApplyImportedLanguage(string? code)
    {
        var normalized = Loc.SupportedLanguages.Contains(code) ? code! : Loc.FollowSystem;
        _settings.Settings.Language = normalized;

        if (!string.Equals(normalized, Loc.RequestedLanguage, StringComparison.Ordinal))
            Loc.SetLanguage(normalized);
    }

    private void OnChannelsChanged() => ChannelsChanged?.Invoke();

    private void OnStateChanged() => StateChanged?.Invoke(_engine.IsRunning);

    private void OnMeterTick()
    {
        // Метры на полосах — представление, снимок дока — сценарий. Оба читают
        // один и тот же тик: второй таймер означал бы два источника правды о том,
        // что сейчас слышно.
        MeterTick?.Invoke();

        // Тот же тик отдаёт панели дока свежие метры: снимок собирается на UI-потоке,
        // поэтому гонок с командами дока не возникает.
        _dock.PublishDockState();
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