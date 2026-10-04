using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;

namespace SoundMeeter.ViewModels;

// Поведение приложения: значок в трее (SM-D02) и автозапуск вместе с Windows (SM-D01).
public partial class MainViewModel
{
    private readonly IStartupService _startup;
    private readonly ILogger _startupLog = AppLog.For<StartupService>();

    /// <summary>
    /// Значок в системном лотке. По умолчанию включено: микшер уходит на сверху
    /// окна и обязан оставаться достижимым, а закрытие окна сворачивает его в
    /// трей вместо выхода.
    /// </summary>
    [ObservableProperty]
    private bool _trayEnabled = true;

    /// <summary>
    /// Автозапуск вместе с Windows.
    ///
    /// Источник истины — система, а не этот флажок: запись автозапуска переживает
    /// копирование settings.json на другой компьютер, и настройка «включено» без
    /// записи в системе — ровно тот случай, когда пользователь ждёт автозапуска,
    /// а его нет. Поэтому при старте значение берётся из системы, а поле в
    /// файле настроек хранит только пожелание для следующего запуска.
    /// </summary>
    [ObservableProperty]
    private bool _runAtStartup;

    /// <summary>
    /// Восстановить настройки поведения после загрузки пресета.
    /// Автозапуск приводится к состоянию системы — расхождение файла и системы
    /// разрешается в пользу системы, потому что включать автозапуск без спроса
    /// пользователя нельзя, а молчащее «в настройках включено» вводит в
    /// заблуждение.
    /// </summary>
    public void RestoreAppBehaviour()
    {
        TrayEnabled = _settingsService.Settings.TrayEnabled;

        bool systemState = _startup.IsEnabled;
        RunAtStartup = systemState;

        if (_settingsService.Settings.RunAtStartup && !systemState)
        {
            _startupLog.LogInformation(
                "Автозапуск в настройках был включён, но в системе выключен — настройка сброшена");
            _settingsService.Settings.RunAtStartup = false;
        }
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
        bool applied = _startup.SetEnabled(value);

        _settingsService.Settings.RunAtStartup = applied;

        if (applied == value) return;

        _startupLog.LogWarning(
            "Автозапуск не удалось {Action}; показываю фактическое состояние",
            value ? Loc.Get("Sm.Tray.Enable") : Loc.Get("Sm.Tray.Disable"));

        // Возврат внутри собственного обработчика: следующий проход увидит
        // applied == value и выйдет, то есть рекурсии не будет.
        RunAtStartup = applied;

        NotifyTray?.Invoke(
            Loc.Get("Sm.Tray.StartupFailedTitle"),
            Loc.Get("Sm.Tray.StartupFailedMessage",
                value ? Loc.Get("Sm.Tray.On") : Loc.Get("Sm.Tray.Off")));
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