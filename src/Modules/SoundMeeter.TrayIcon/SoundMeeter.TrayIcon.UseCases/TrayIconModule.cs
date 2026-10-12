using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Framework.Base.Modules;
using SoundMeeter.TrayIcon.Contract;
using SoundMeeter.TrayIcon.Contract.Implementation;
using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.TrayIcon.UseCases
{
    public class TrayIconModule : IModule
    {
        public string Name => "TrayIconModule";

        public void RegisterServices(IServiceCollection services)
        {
            services.AddSingleton<ITrayIconService, TrayIconService>();
        }
    }
}
