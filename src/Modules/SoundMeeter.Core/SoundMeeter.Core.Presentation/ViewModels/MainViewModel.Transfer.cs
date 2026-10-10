using SoundMeeter.Infrastructure.Settings;


namespace SoundMeeter.Core.Presentation.ViewModels;

// Импорт и экспорт всех настроек одним файлом (SM-C09).
//
// Сама работа с файлом и применение уехали в MixerCoordinator: импорт трогает и
// движок, и Loc с AppLog, и состояние системы, и всё это — не представление.
// Здесь остались вызовы, потому что показывать результат пользователю дело VM.
public partial class MainViewModel
{
    /// <summary>
    /// Пишет все настройки в <paramref name="path"/>: стрипы, роутинг, эффекты,
    /// MIDI, маршруты приложений, скрытые устройства, док OBS, язык, уровень
    /// журнала и поведение приложения. Возвращает результат с сообщением для
    /// пользователя — вызывающий показывает его диалогом.
    /// </summary>
    public SettingsExportResult ExportSettings(string path) => _coordinator.ExportSettings(path);

    /// <summary>
    /// Заменяет текущие настройки настройками из файла. Флаг трея и автозапуск
    /// показываются по системе, а не по файлу, поэтому после импорта читаем их
    /// заново — иначе переключатели мигали бы прежними значениями.
    /// </summary>
    public SettingsImportResult ImportSettings(string path)
    {
        var result = _coordinator.ImportSettings(path);
        SyncBehaviourFlags();
        return result;
    }
}