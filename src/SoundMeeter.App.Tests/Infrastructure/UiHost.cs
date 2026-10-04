using SoundMeeter.Services;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SoundMeeter.Tests.Infrastructure;

/// <summary>
/// Единственный STA-поток с <see cref="Application"/> и установленным
/// <see cref="Loc"/>. UI-тесты обязаны работать на нём: у объектов WPF есть
/// потоковая привязка, а тест-раннер xunit выполняет тесты в пуле потоков
/// (MTA). Отдельного STA-атрибута в xunit v2 нет, поэтому тело UI-теста
/// запускается через <see cref="Run"/> — метод блокирует вызывающий поток и
/// возвращает результат (или исключение) назад.
/// </summary>
/// <remarks>
/// Поток один на весь набор тестов намеренно: <see cref="Application.Current"/>
/// в процессе может быть только один, а словарь строк локализации — общий
/// статический ресурс. Заводить по потоку на тест означало бы гонку за обоими.
/// Параллельные тестовые классы при этом не мешают друг другу: всё, что идёт
/// через <see cref="Run"/>, встаёт в очередь диспетчера и выполняется по
/// очереди, а тесты без UI (DSP, миграции) в очереди не стоят вовсе.
/// </remarks>
public static class UiHost
{
    private static readonly Lazy<Thread> LazyThread = new(CreateThread,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static volatile bool _ready;
    private static ExceptionDispatchInfo? _startupFailure;

    private static Thread CreateThread()
    {
        // STA обязателен: WPF не создаёт Application в MTA-потоке, а тест-раннер
        // xunit запускает тесты в пуле (MTA). Умолчание .NET — MTA, поэтому
        // apartment выставляется явно — и до Start, иначе поздно.
        var thread = new Thread(Start) { IsBackground = true, Name = "SoundMeeter.Tests.UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread;
    }

    private static void Start()
    {
        try
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            InstallTheme(app);

            // LocResources, а не Loc: словарь строк — это WPF. Сам Loc лежит в
            // SoundMeeter.Audio и поднимается без графики, но подключить его к
            // Application.Resources может только сторона с UI.
            LocResources.Install(app);
            _ready = true;
        }
        catch (Exception ex)
        {
            // Ошибка старта обязана долететь до теста, а не прятаться в мёртвом
            // потоке: иначе каждый UI-тест «падает» по таймауту ожидания.
            _startupFailure = ExceptionDispatchInfo.Capture(ex);
            _ready = true;
            return;
        }

        Dispatcher.Run();
    }

    /// <summary>
    /// Тема приложения в <see cref="Application.Resources"/> — ровно как в
    /// <c>App.xaml</c>.
    ///
    /// Тест создаёт <see cref="Application"/> программно и не проходит через
    /// App.xaml, поэтому без этого словаря UI-тесты шли бы не в том окружении,
    /// в каком работает приложение: любой <c>{StaticResource …}</c> из темы в
    /// шаблоне падал бы при загрузке разметки, и тест рушился бы на XAML вместо
    /// проверяемого поведения.
    /// </summary>
    private static void InstallTheme(Application app)
    {
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/StreamerTools.Style;component/Themes/DarkTheme.xaml", UriKind.Relative)
        });

        // Неявный стиль Button из App.xaml: без него кнопки в тестах выглядели бы
        // иначе, чем у пользователя.
        app.Resources[typeof(Button)] = new Style(typeof(Button), (Style)app.Resources["BtnFilled"]);
    }

    /// <summary>Выполнить действие в единственном STA-потоке приложения.</summary>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        _ = LazyThread.Value;
        SpinWait.SpinUntil(() => _ready, TimeSpan.FromSeconds(30));
        _startupFailure?.Throw();

        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();

        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                done.Set();
            }
        }));

        if (!done.Wait(TimeSpan.FromMinutes(2)))
            throw new TimeoutException("UI-тест не отработал за 2 минуты");

        failure?.Throw();
    }

    /// <summary>Выполнить действие в STA-потоке и вернуть его результат.</summary>
    public static T Run<T>(Func<T> function)
    {
        ArgumentNullException.ThrowIfNull(function);

        T result = default!;

        // Каст до Action обязателен: присваивание само по себе — выражение типа
        // T, и без каста лямбда снова выбрала бы Run<T> и ушла в рекурсию.
        Run((Action)(() => result = function()));
        return result;
    }
}
