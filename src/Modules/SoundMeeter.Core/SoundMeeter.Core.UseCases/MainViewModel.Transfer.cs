using Microsoft.Extensions.Logging;
using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.ViewModels;

// Импорт и экспорт всех настроек одним файлом (SM-C09). Файл пишет и читает
// SettingsTransferService, а применяет его здесь: применение затрагивает и
// движок, и Loc с AppLog, и состояние системы, и всё это знает только VM.
public partial class MainViewModel
{
    private readonly SettingsTransferService _transfer;
    private readonly ILogger _transferLogger = AppLog.For<MainViewModel>();

    /// <summary>
    /// Пишет все настройки в <paramref name="path"/>: стрипы, роутинг, эффекты,
    /// MIDI, маршруты приложений, скрытые устройства, док OBS, язык, уровень
    /// журнала и поведение приложения. Возвращает результат с сообщением для
    /// пользователя — вызывающий показывает его диалогом.
    /// </summary>
    public SettingsExportResult ExportSettings(string path) => _transfer.Export(path);

    /// <summary>
    /// Заменяет текущие настройки настройками из файла.
    ///
    /// Импорт — не «применить пресет»: он трогает всё, включая то, что лежит
    /// вне движка (док, язык, трей, автозапуск), поэтому порядок важен. Сначала
    /// движок, потом живые поля, и только потом всё, у чего есть побочный эффект.
    /// Возвращает <c>false</c>, если файл не прошёл проверку, и ничего не меняет.
    /// </summary>
    public SettingsImportResult ImportSettings(string path)
    {
        var read = _transfer.Read(path);
        if (!read.Success || read.Settings is not { } imported) return read;

        // Копия того, что сейчас настроено. Импорт переписывает всё разом, а
        // откатить его без копии нечем, а settings.json в этот момент ещё цел —
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
        ApplyDockSettings(_settings.Settings.ObsDock, saveNow: false);

        // Модуль синтеза речи (SM-E01): настройки уже в живом снимке, но модуль
        // надо перевключить под них — как сервер дока. Отдельно от ApplyDockSettings
        // ещё и потому, что ApplyPreset пересоздал стрипы: если файл пришёл с
        // другой машины, целевой канал мог смениться, и без этого модуль продолжил
        // бы говорить в старый.
        RestoreTts();

        // MIDI-устройство из файла — как при старте, в Restore.
        _midi.Open(_engine.Midi.DeviceName);

        // Поведение приложения. Источник истины по автозапуску — система: файл
        // из чужой машины не должен молча регистрировать автозапуск здесь, поэтому
        // значение из файла остаётся пожеланием, а показывает его RestoreAppBehaviour.
        RestoreAppBehaviour();

        // Состояние движка приводим к файлу: импорт означает «сделать всё как там».
        if (imported.EngineWasRunning && !_engine.IsRunning) _engine.Start();
        else if (!imported.EngineWasRunning && _engine.IsRunning) _engine.Stop();

        SaveNow();

        _transferLogger.LogInformation(
            "Settings imported from {Path}: {Inputs} inputs, {Buses} buses, was running: {WasRunning}",
            path, imported.Inputs.Count, imported.Outputs.Count, imported.EngineWasRunning);

        return read with { BackupPath = backup ?? string.Empty };
    }

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
}