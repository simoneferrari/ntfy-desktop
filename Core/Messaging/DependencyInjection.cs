using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace NtfyDesktop.Core.Messaging;

public static class DependencyInjection
{
    extension(IServiceCollection services)
    {
        public void AddMessaging()
        {
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
            services.AddSingleton<IUIDispatcher>(_ => new WpfUIDispatcher(Application.Current.Dispatcher));
            services.AddSingleton<EventBus>();

            if (Assembly.GetEntryAssembly() is { } entryAssembly)
                services.AddEventHandlers(entryAssembly);
        }

        private void AddEventHandlers(params Assembly[] assemblies)
        {
            var registrations = 
                from a in assemblies
                from t in a.DefinedTypes
                where t is { IsAbstract: false, IsInterface: false }
                from i in t.GetInterfaces()
                where i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>)
                select (Service: i, Implementation: t);

            foreach (var (service, implementation) in registrations)
                services.AddTransient(service, implementation);
        }
    }

    public static void UseMessaging(this IServiceProvider serviceProvider)
    {
        var eventBus = serviceProvider.GetRequiredService<EventBus>();
        EventBusHost.Initialize(eventBus);
    }
}