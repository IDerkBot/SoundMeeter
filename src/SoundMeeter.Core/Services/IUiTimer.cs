namespace SoundMeeter.Services
{
    /// <summary>
    /// Таймер, который тикает в UI-потоке (SM-A10). Ядро обновляет метры
    /// уровней и публикует снимок для дока OBS раз в 33 мс, и обновление метров
    /// обязано быть в UI-потоке: <c>InputChannelViewModel.UpdatePeak</c> и
    /// <c>PublishDockState</c> поднимают уведомления о свойствах и читают
    /// коллекции, которые в этот момент перестраивает UI.
    ///
    /// Раньше это был <c>System.Windows.Threading.DispatcherTimer</c>, объявленный
    /// прямо в <c>MainViewModel</c>, — то есть единственный таймер приложения
    /// жёстко тянул WPF в ядро. Теперь ядро знает только «нужен таймер в
    /// UI-потоке», а реализация (<c>DispatcherTimerAdapter</c>) лежит в
    /// приложении вместе с <see cref="IDispatcherService"/>.
    ///
    /// Экземпляр создаёт Composition Root: таймер привязан к диспетчеру того
    /// потока, на котором был создан, поэтому резолвить его нужно на UI-потоке
    /// (в приложении это <c>App.OnStartup</c>).
    /// </summary>
    public interface IUiTimer : IDisposable
    {
        /// <summary>Период тиков.</summary>
        TimeSpan Interval { get; set; }

        /// <summary>Тик. Подписанный обработчик выполняется в UI-потоке.</summary>
        event Action? Ticked;

        void Start();

        void Stop();
    }
}