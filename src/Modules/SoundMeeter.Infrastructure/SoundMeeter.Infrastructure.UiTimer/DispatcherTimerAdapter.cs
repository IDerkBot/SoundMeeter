using System.Windows.Threading;

namespace SoundMeeter.Services;

/// <summary>
/// Реализация <see cref="IUiTimer"/> поверх <see cref="DispatcherTimer"/> (SM-A10).
///
/// Таймер привязан к диспетчеру потока, на котором создан, поэтому его создаёт
/// Composition Root на UI-потоке (<c>App.OnStartup</c>). Тики приходят в UI-поток
/// без всякой маршрутизации — ровно то поведение, ради которого в
/// <c>MainViewModel</c> раньше стоял <c>DispatcherTimer</c> напрямую.
/// </summary>
public sealed class DispatcherTimerAdapter : IUiTimer
{
    private readonly DispatcherTimer _timer;

    public DispatcherTimerAdapter(TimeSpan interval)
    {
        _timer = new DispatcherTimer { Interval = interval };
        _timer.Tick += OnTick;
    }

    public TimeSpan Interval
    {
        get => _timer.Interval;
        set => _timer.Interval = value;
    }

    public event Action? Ticked;

    private void OnTick(object? sender, EventArgs e) => Ticked?.Invoke();

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
        Ticked = null;
    }
}