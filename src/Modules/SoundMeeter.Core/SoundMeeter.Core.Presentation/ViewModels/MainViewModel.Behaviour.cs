using CommunityToolkit.Mvvm.ComponentModel;
using SoundMeeter.ChangeLanguage.UseCases;


namespace SoundMeeter.Framework.Presentation.ViewModels;

// Поведение приложения: значок в трее (SM-D02) и автозапуск вместе с Windows (SM-D01).
//
// Правило «источник истины — система, а не файл настроек» уехало в
// AppBehaviourService: это рассуждение о записи в реестре, а не о переключателе.
// Здесь осталось то, что правда про представление, — два наблюдаемых свойства и
// делегаты до окна.
public partial class MainViewModel
{
    /// <summary>
    /// Значок в системном лотке. По умолчанию включено: микшер уходит на сверху
    /// окна и обязан оставаться достижимым, а закрытие окна сворачивает его в
    /// трей вместо выхода.
    /// </summary>
    [ObservableProperty]
    private bool _trayEnabled = true;

    /// <summary>
    /// Автозапуск вместе с Windows. Показывает фактическое состояние системы,
    /// а не пожелание из файла: расхождение разрешается в пользу системы.
    /// </summary>
    [ObservableProperty]
    private bool _runAtStartup;

    /// <summary>
    /// Восстановить настройки поведения после загрузки пресета.
    /// Вызывается до показа окна, чтобы иконка в трее появилась сразу, а не
    /// мигала через секунду после старта.
    /// </summary>
    public void RestoreAppBehaviour()
    {
        _behaviour.Restore();
        SyncBehaviourFlags();
    }

    /// <summary>
    /// Переносит фактические значения из сценария в переключатели окна.
    /// Отдельный метод, потому что два места знают одно и то же: старт и импорт
    /// настроек меняют поведение приложения одинаково.
    /// </summary>
    private void SyncBehaviourFlags()
    {
        TrayEnabled = _behaviour.TrayEnabled;
        RunAtStartup = _behaviour.RunAtStartupInSystem;
    }

    partial void OnTrayEnabledChanged(bool value)
    {
        _settingsService.Settings.TrayEnabled = value;
        OnApplyTraySettings?.Invoke();
    }

    /// <summary>
    /// Переключатель автозапуска: сначала пишем в реестр и только потом
    /// отмечаем флажок. Если запись не удалась, показываем фактическое
    /// состояние — иначе флажок врал бы, и пользователь искал бы ошибку не там.
    /// </summary>
    partial void OnRunAtStartupChanged(bool value)
    {
        bool applied = _behaviour.ApplyRunAtStartup(value);

        if (applied == value) return;

        NotifyTray?.Invoke(
            Loc.Get("Sm.Tray.StartupFailedTitle"),
            Loc.Get("Sm.Tray.StartupFailedMessage",
                value ? Loc.Get("Sm.Tray.On") : Loc.Get("Sm.Tray.Off")));

        // Возврат внутри собственного обработчика: следующий проход увидит
        // applied == value и выйдет, то есть рекурсии не будет.
        RunAtStartup = applied;
    }

    /// <summary>
    /// Показать уведомление трея. Это делегат, а не зависимость от сервиса
    /// иконки: VM не должен знать про <c>NotifyIcon</c>, иначе тесты
    /// пришлось бы поднимать настоящий трей.
    /// </summary>
    public Action<string, string>? NotifyTray { get; set; }

    /// <summary>Хост окна подписывается сюда: он знает про окно, VM — нет.</summary>
    public Action? OnApplyTraySettings { get; set; }
}