using System.Windows;
using System.Windows.Threading;

namespace SoundMeeter.Services
{
    /// <summary>
    /// Реализация <see cref="IDispatcherService"/> поверх WPF (SM-A10).
    ///
    /// Диспетчер берётся один раз в конструкторе, а не по <c>Application.Current</c>
    /// на каждый вызов: пока нет <see cref="Application"/>, падать надо сразу и
    /// понятно, а не из середины MIDI-потока. Создаётся Composition Root'ом на
    /// UI-потоке, поэтому диспетчер закреплён именно за ним.
    /// </summary>
    public class DispatcherService : IDispatcherService
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherService()
        {
            _dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException(
                    "Application.Current is null: переключиться на UI-поток не на чем. " +
                    "DispatcherService создаётся только из App.OnStartup.");
        }

        public bool HasThreadAccess => _dispatcher.CheckAccess();

        public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

        public void Post(Action action) => _dispatcher.BeginInvoke(action);
    }
}