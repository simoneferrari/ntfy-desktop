using Microsoft.Extensions.DependencyInjection;

namespace NtfyDesktop.Features.Updates;

public static class UpdatesFeature
{
    extension(IServiceCollection services)
    {
        public void AddUpdates()
        {
            // UpdateService is a singleton — it holds the pending update between the
            // background check that finds it and the banner action that applies it.
            services.AddSingleton<UpdateService>();
            services.AddHostedService<UpdateCheckService>();
        }
    }
}
