using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Features.Connections;

namespace NtfyDesktop.Features.Feed;

public static class FeedFeature
{
    extension(IServiceCollection services)
    {
        public void AddFeeds()
        {
            services.AddSingleton<FeedViewModel>();
            services.AddTransient<FeedPage>();

            // Inline image attachments: shared download/cache service + its periodic sweep.
            services.AddSingleton<AttachmentImageService>();
            services.AddHostedService<AttachmentCacheSweepService>();

            // Executes message action buttons (view / http / copy) safely.
            services.AddSingleton<MessageActionInvoker>();
        }
        
    }
}