using Microsoft.Extensions.Logging;
using SoundMeeter.Core.Contract;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.StartUp.Contract;


namespace SoundMeeter.Core.UseCases;

/// <summary>
/// Поведение приложения: значок в системном лотке (SM-D02) и автозапуск вместе
/// с Windows (SM-D01).
///
/// Вынесено из <c>MainViewModel</c> потому, что здесь нет ничего от интерфейса:
/// обе настройки живут в системе и в settings.json, а окно лишь показывает их
/// и повторяет щелчок. Правило разрешения разногласий между файлом и системой —
/// единственное, что здесь есть по существу, и оно принадлежит сценарию, а не
/// представлению: иначе оно повторилось бы в каждом окне, которое когда-нибудь
/// дотянется до этих настроек.
/// </summary>
public sealed class AppBehaviourService
{
    private readonly IStartupService _startup;
    private readonly ISettingsService _settings;
    private readonly ILogger _logger = AppLog.For<AppBehaviourService>();

    public AppBehaviourService(IStartupService startup, ISettingsService settings)
    {
        _startup = startup;
        _settings = settings;
    }

    /// <summary>Значок в трее включён. По умолчанию включено.</summary>
    public bool TrayEnabled => _settings.Settings.TrayEnabled;

    /// <summary>
    /// Значение автозапуска, записанное в системе. Источник истины — система,
    /// а не файл настроек.
    /// </summary>
    public bool RunAtStartupInSystem => _startup.IsEnabled;

    /// <summary>
    /// Восстанавливает настройки поведения после загрузки пресета.
    /// Автозапуск приводится к состоянию системы: расхождение файла и системы
    /// разрешается в пользу системы, потому что включать автозапуск без спроса
    /// пользователя нельзя, а молчащее «в настройках включено» вводит в
    /// заблуждение.
    ///
    /// Возвращает фактическое состояние автозапуска — его и показывает окно.
    /// </summary>
    public bool Restore()
    {
        bool systemState = _startup.IsEnabled;

        if (_settings.Settings.RunAtStartup && !systemState)
        {
            _logger.LogInformation(
                "Автозапуск в настройках был включён, но в системе выключен — настройка сброшена");
            _settings.Settings.RunAtStartup = false;
        }

        return systemState;
    }

    /// <summary>
    /// Переключатель автозапуска: сначала пишем в реестр и только потом
    /// отмечаем флажок. Если запись не удалась, возвращается фактическое
    /// состояние — иначе флажок врал бы, и пользователь искал бы ошибку не там.
    /// </summary>
    public bool ApplyRunAtStartup(bool desired)
    {
        bool applied = _startup.SetEnabled(desired);
        _settings.Settings.RunAtStartup = applied;

        if (applied != desired)
        {
            _logger.LogWarning(
                "Автозапуск не удалось изменить; показываю фактическое состояние");
        }

        return applied;
    }
}