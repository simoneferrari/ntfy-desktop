using Microsoft.Extensions.DependencyInjection;

namespace NtfyDesktop.Features.Unread;

public static class UnreadFeature
{
    extension(IServiceCollection services)
    {
        public void AddUnread()
        {
            services.AddSingleton<UnreadTracker>();
        }
    }
}
