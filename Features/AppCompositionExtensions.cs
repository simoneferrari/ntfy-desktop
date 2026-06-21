using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Feed;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Shell;
using NtfyDesktop.Features.Topics;
using NtfyDesktop.Features.Unread;
using NtfyDesktop.Features.Updates;

namespace NtfyDesktop.Features;

public static class AppCompositionExtensions
{
    public static void AddNtfyDesktop(this IServiceCollection services)
    {
        services.AddMessaging();
        
        services.AddShell();
        services.AddNotifications();
        services.AddConnections();
        services.AddFeeds();
        services.AddHistory();
        services.AddRules();
        services.AddSettings();
        services.AddTopics();
        services.AddUnread();
        services.AddUpdates();
    }
}