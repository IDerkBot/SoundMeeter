using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace SoundMeeter.Services;

/// <summary>
/// Обёртка над COM-планировщиком Windows (<c>Schedule.Service</c>) для автозапуска
/// с полными правами (SM-D01).
///
/// Почему задача, а не ключ <c>HKCU\...\Run</c>: приложению нужны права
/// администратора (AppRouting — <c>PolicyConfigClient</c> меняет маршрутизацию
/// всего сеанса), а манифест объявлен как <c>requireAdministrator</c>. Запись в
/// ключе <c>Run</c> запускается планировщиком в обычном контексте пользователя,
/// поэтому при каждом входе Windows показывала бы запрос UAC — и если его не
/// подтвердить, микшер просто не стартовал бы. Задача, зарегистрированная
/// <c>TASK_LOGON_INTERACTIVE_TOKEN</c> + <c>TASK_RUNLEVEL_HIGHEST</c>, запускает
/// процесс с полным токеном администратора и без запроса: повышение происходит в
/// момент регистрации задачи, а она выполняется из уже повышенного процесса.
///
/// Именованные аргументы вместо позднего связывания <c>[ComImport]</c>
/// интерфейсов: у планировщика двенадцать интерфейсов, объявление их вручную —
/// это страница кода ради трёх вызовов. Все интерфейсы двусторонние (IDispatch),
/// поэтому вызовы идут через <c>dynamic</c>. Все методы ничего не бросают наружу:
/// автозапуск — не повод уронить микшер, и состояние возвращается вызывающей
/// стороне, а <c>IsEnabled</c> сообщает правду.
/// </summary>
internal static class TaskSchedulerClient
{
    /// <summary>Имя задачи в корневой папке планировщика.</summary>
    internal const string TaskName = "SoundMeeter";

    private const string RootFolder = @"\";
    private const string ServiceProgId = "Schedule.Service";
    private const string Description =
        "SoundMeeter: запуск при входе в Windows с правами администратора (AppRouting)";

    // Константы TASK_* из taskschd.h. Своими enum они не объявлены, а magic
    // numbers в коде, который работает только на Windows, читались бы хуже, чем
    // именованные константы с комментарием.
    private const int TaskCreate = 0;
    private const int ActionExec = 0;
    private const int TriggerLogon = 9;
    private const int LogonInteractiveToken = 3;
    private const int RunLevelHighest = 1;
    private const int CreateOrUpdate = 6;
    private const int InstancesIgnoreNew = 2;
    private const int DeleteNoHistory = 0;

    /// <summary>HRESULT отсутствующей задачи: HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND).</summary>
    private const int HResultTaskNotFound = unchecked((int)0x80070002);

    private static readonly ILogger _logger = AppLog.For(nameof(TaskSchedulerClient));

    /// <summary>
    /// Зарегистрирована ли задача. Отсутствие задачи — обычное «выключено», а не
    /// ошибка; недоступность самого планировщика попадает в журнал и тоже
    /// трактуется как «выключено», чтобы переключатель в интерфейсе не врал.
    /// </summary>
    internal static bool Exists()
    {
        try
        {
            dynamic folder = ConnectRootFolder();

            object? task = folder.GetTask(TaskName);
            return task is not null;
        }
        catch (Exception ex) when (IsTaskNotFound(ex))
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать состояние задачи автозапуска: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Создаёт задачу или перезаписывает существующую. <c>false</c> — записать не
    /// удалось (нет прав, политика, отключённая служба планировщика).
    /// </summary>
    internal static bool TryCreateOrUpdate(string executable)
    {
        try
        {
            dynamic service = CreateService();
            dynamic folder = service.GetFolder(RootFolder);
            dynamic definition = service.NewTask(TaskCreate);

            // Интерактивный токен + Highest: у процесса, запущенного задачей,
            // полный токен администратора. Логин и пароль не нужны и не
            // запрашиваются — иначе включение автозапуска подвисало бы на
            // невидимом окне ввода пароля.
            dynamic principal = definition.Principal;
            principal.LogonType = LogonInteractiveToken;
            principal.RunLevel = RunLevelHighest;

            dynamic trigger = definition.Triggers.Create(TriggerLogon);
            trigger.Enabled = true;
            trigger.UserId = CurrentUserName();

            dynamic action = definition.Actions.Create(ActionExec);
            action.Path = executable;
            action.WorkingDirectory = Path.GetDirectoryName(executable) ?? string.Empty;

            dynamic settings = definition.Settings;
            settings.Enabled = true;
            settings.AllowDemandStart = true;
            settings.StartWhenAvailable = true;

            // Батарея: с defaults задача не стартовала бы на ноутбуке от батареи
            // и останавливалась бы при переходе на неё.
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;

            // Простой микшера может длиться сутками: стандартные 72 часа
            // ограничения у задачи убили бы работающий микшер.
            settings.ExecutionTimeLimit = "PT0S";
            settings.RunOnlyIfIdle = false;

            // IgnoreNew вместо Parallel: второй экземпляр всё равно отсекает
            // SingleInstanceGuard, а лишний запуск задачи только шумит в журнале.
            settings.MultipleInstances = InstancesIgnoreNew;

            definition.RegistrationInfo.Description = Description;

            folder.RegisterTaskDefinition(
                TaskName, definition, CreateOrUpdate, null, null, LogonInteractiveToken, null);

            _logger.LogInformation("Задача автозапуска зарегистрирована: {Path}", executable);
            return true;
        }
        catch (Exception ex)
        {
            // Повышенные права — половина решения: без них именно этот вызов
            // отклоняется, поэтому в журнал идёт и признак повышения.
            _logger.LogWarning(
                ex,
                "Не удалось зарегистрировать задачу автозапуска (elevated={Elevated}): {Message}",
                IsElevated(),
                ex.Message);
            return false;
        }
    }

    /// <summary>Удаляет задачу. Отсутствующая задача считается удалённой.</summary>
    internal static void Delete()
    {
        try
        {
            dynamic folder = ConnectRootFolder();
            folder.DeleteTask(TaskName, DeleteNoHistory);
            _logger.LogInformation("Задача автозапуска удалена");
        }
        catch (Exception ex) when (IsTaskNotFound(ex))
        {
            // Уже нет задачи — выключено, а не ошибка.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось удалить задачу автозапуска: {Message}", ex.Message);
        }
    }

    private static dynamic CreateService()
    {
        Type? serviceType = Type.GetTypeFromProgID(ServiceProgId)
            ?? throw new InvalidOperationException("COM-класс планировщика задач не зарегистрирован");

        dynamic service = Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Не удалось создать COM-объект планировщика задач");

        service.Connect();
        return service;
    }

    private static dynamic ConnectRootFolder() => CreateService().GetFolder(RootFolder);

    /// <summary>
    /// Учётная запись для триггера «вход в систему» в виде
    /// <c>DOMAIN\user</c>: без неё задача сработала бы при входе любого
    /// пользователя, а потом ещё и искала бы пароль к интерактивному токену.
    /// </summary>
    private static string CurrentUserName()
    {
        string domain = Environment.UserDomainName;
        return string.IsNullOrWhiteSpace(domain)
            ? Environment.UserName
            : $@"{domain}\{Environment.UserName}";
    }

    /// <summary>
    /// Планировщик сообщает об отсутствующей задаче и HRESULT
    /// <c>ERROR_FILE_NOT_FOUND</c>, и COM-ошибкой, и
    /// <see cref="FileNotFoundException"/> — в зависимости от того, как именно
    /// пришёл вызов, поэтому проверяются оба признака.
    /// </summary>
    private static bool IsTaskNotFound(Exception ex) =>
        ex is FileNotFoundException || ex.HResult == HResultTaskNotFound;

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
