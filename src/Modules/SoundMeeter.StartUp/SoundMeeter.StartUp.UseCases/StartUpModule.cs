using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Framework.Base.Modules;
using SoundMeeter.StartUp.Contract;
using SoundMeeter.StartUp.Contract.Implementation;

namespace SoundMeeter.StartUp.UseCases;

public class StartUpModule : IModule
{
    public string Name => nameof(StartUpModule);

    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton<IStartupService, StartupService>();
    }
}
