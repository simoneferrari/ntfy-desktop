using Microsoft.Extensions.DependencyInjection;

namespace NtfyDesktop.Features.Notifications;

public static class NotificationsFeature
{
    extension(IServiceCollection services)
    {
        public void AddNotifications()
        {
            services.AddSingleton<NotificationGate>();

            services.AddSingleton<ToastNotifier>(_ =>
            {
                var notifier = new ToastNotifier();
                notifier.Register();
                return notifier;
            });

            // Singleton: holds the debounced catch-up accumulator across messages
            // (the IEventHandler that feeds it is transient).
            services.AddSingleton<BackfillSummaryNotifier>();
        }
        
    }
}