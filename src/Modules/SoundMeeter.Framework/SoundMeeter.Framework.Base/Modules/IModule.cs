using Microsoft.Extensions.DependencyInjection;

namespace SoundMeeter.Framework.Base.Modules
{
    /// <summary>
    /// Контракт для всех модулей приложения.
    /// Каждый модуль реализует этот интерфейс, чтобы зарегистрировать свои сервисы в DI.
    /// </summary>
    public interface IModule
    {
        /// <summary>
        /// Уникальное имя модуля (для логирования и отладки).
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Регистрация сервисов модуля в DI-контейнере.
        /// Вызывается ДО сборки контейнера.
        /// </summary>
        void RegisterServices(IServiceCollection services);
    }
}
