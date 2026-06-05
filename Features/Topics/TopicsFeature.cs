using Microsoft.Extensions.DependencyInjection;

namespace NtfyDesktop.Features.Topics;

public static class TopicsFeature
{
    extension(IServiceCollection services)
    {
        public void AddTopics()
        {
            services.AddSingleton<TopicArrangement>();
            services.AddSingleton<TopicManager>();
        }
        
    }
}