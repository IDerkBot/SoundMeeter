using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Framework.Base.Modules;
using SoundMeeter.Twitch.Contract;
using SoundMeeter.Twitch.Contract.Implementation;
using SoundMeeter.Twitch.Presentation;
using SoundMeeter.Twitch.Presentation.ViewModels;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace SoundMeeter.Twitch
{
    public class TwitchModule : IModule
    {
        public string Name => nameof(TwitchModule);

        public void RegisterServices(IServiceCollection services)
        {
            // Интеграция с Twitch (SM-F01). Порядок важен: токены и клиент API живут
            // по одному экземпляру на процесс, потому что у них кэш и состояние входа,
            // а сервис поднимает подписку на чат и опрос трансляции — и это тоже
            // единственный в процессе.
            services.AddSingleton<ITwitchTokenStore, TwitchTokenFileStore>();
            services.AddSingleton(_ => new HttpClient());

            // Сервис просит ИНТЕРФЕЙСЫ, а не конкретные классы: так его можно проверить
            // с подменой без сети. Поэтому каждый интерфейс указывает на тот же
            // экземпляр, что и сам класс, — регистрация через GetRequiredService.
            // Регистрация класса без интерфейса дала бы «unable to resolve service».
            services.AddSingleton<TwitchAuthService>();
            services.AddSingleton<ITwitchAuth>(sp => sp.GetRequiredService<TwitchAuthService>());

            services.AddSingleton<TwitchApiClient>();
            services.AddSingleton<ITwitchApi>(sp => sp.GetRequiredService<TwitchApiClient>());

            services.AddSingleton<TwitchChatReader>();
            services.AddSingleton<ITwitchChatReader>(sp => sp.GetRequiredService<TwitchChatReader>());

            services.AddSingleton<TwitchService>();
            services.AddSingleton<TwitchViewModel>();

            // Окно настроек получает состояние интеграции, а не сам сервис: окно
            // показывает состояние и не должно получать ещё и право им управлять.
            services.AddSingleton<ITwitchState>(sp => sp.GetRequiredService<TwitchService>());

            services.AddSingleton<TwitchTtsMessageSource>();
        }
    }
}
