using System.Windows;

namespace SoundMeeter.Services
{
    public class DispatcherService : IDispatcherService
    {
        public Task InvokeAsync(Action action)
        {
            if (Application.Current == null)
            {
                throw new InvalidOperationException("Application.Current is null. Cannot invoke on UI thread.");
            }

            return Application.Current.Dispatcher.InvokeAsync(action).Task;
        }
    }
}
