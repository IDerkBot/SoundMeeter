namespace SoundMeeter.Services
{
    public interface IDispatcherService
    {
        Task InvokeAsync(Action action);
    }
}
