namespace SoundMeeter.Services
{
    /// <summary>
    /// Мост к UI-потоку (SM-A10). Единственное место в ядре, где нужен «поток
    /// интерфейса»: ViewModel'ы поднимают уведомления о свойствах, а трогать их
    /// из фонового потока нельзя — привязка WPF получит чужой поток и упадёт.
    ///
    /// Реализация лежит в приложении (<c>DispatcherService</c>, поверх
    /// <c>System.Windows.Threading.Dispatcher</c>), поэтому ядро знает про
    /// «переключись в UI-поток», но не знает, что это за поток и кто его
    /// обслуживает. Раньше то же самое было написано прямо в ViewModel через
    /// <c>Application.Current.Dispatcher</c> — и такими <c>Application.Current</c>
    /// ядро тянуло WPF в себя (см. UseWPF в SoundMeeter.Core.csproj).
    /// </summary>
    public interface IDispatcherService
    {
        /// <summary>
        /// Выполнить действие в UI-потоке и дождаться результата. Бросает
        /// исключение действия вызывающему (в отличие от <see cref="Post"/>).
        /// </summary>
        Task InvokeAsync(Action action);

        /// <summary>
        /// Поставить действие в очередь UI-потока и не ждать. Для обработчиков
        /// событий, которые пришли из фонового потока (MIDI, команды дока) и
        /// ничего не возвращают.
        /// </summary>
        void Post(Action action);

        /// <summary>
        /// Мы уже в UI-потоке? Нужен там, где действие безопасно выполнить
        /// немедленно, а постановка в очередь исказила бы порядок событий.
        /// </summary>
        bool HasThreadAccess { get; }
    }
}