using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Core.Messaging;
using Microsoft.Extensions.Hosting;
using NtfyDesktop.Domain;
using NtfyDesktop.Features;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.Feed;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Notifications.Events;
using NtfyDesktop.Features.Shell;
using NtfyDesktop.Features.Unread;
using NtfyDesktop.Features.Unread.Events;
using Wpf.Ui.Appearance;
using FeedViewModel = NtfyDesktop.Features.Feed.FeedViewModel;

namespace NtfyDesktop;

public partial class App : Application
{
    public const string NAME = "Ntfy Desktop";

    // Default data folder. Can be overridden at launch with --data-path <dir>.
    public static string DataPath { get; private set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NtfyDesktop");

    // Stable per-profile hash of the data folder. Drives both the single-instance
    // mutex and the activation named-pipe so different --data-path profiles run
    // fully independently.
    private static string DataPathHash()
    {
        var normalized = Path.GetFullPath(DataPath).ToUpperInvariant();
        var hash = normalized.Aggregate(2166136261u, (current, c) => (current ^ (byte) c) * 16777619u); // FNV-1a 32-bit
        return hash.ToString("X8");
    }

    private static string SingleInstanceMutexName() => $"NtfyDesktop_{DataPathHash()}_SingleInstance";
    private static string ActivationPipeName()      => $"NtfyDesktop_{DataPathHash()}_Activation";

    private IHost? _host;
    private Mutex? _mutex;
    private TrayIconHost? _trayIcon;
    private SingleInstanceServer? _activationServer;

    // Stash a cold-start activation URL until the host is ready to handle it.
    private string? _pendingActivationUrl;

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
            ?? throw new InvalidOperationException("Host not started yet.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --data-path <dir> or --data-path=<dir>: override the default data folder.
        // Must be set before anything else so AppSettings + HistoryRepository pick it up.
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--data-path" && i + 1 < e.Args.Length)
            { DataPath = Path.GetFullPath(e.Args[i + 1]); break; }

            if (!e.Args[i].StartsWith("--data-path=", StringComparison.Ordinal)) continue;

            DataPath = Path.GetFullPath(e.Args[i]["--data-path=".Length..]); break;
        }

        // Pull any ntfy-desktop:// URL out of the args (toast click activation).
        var activationUrl = ExtractActivationUrl(e.Args);

        // Surface anything fatal during startup instead of silently dying.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), $"{NAME} — unhandled exception",
                MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            System.Windows.MessageBox.Show(args.ExceptionObject?.ToString() ?? "Unknown", $"{NAME} — fatal",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);

        _mutex = new Mutex(true, SingleInstanceMutexName(), out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Another instance is already running for this data folder.
            //
            // A "view" toast button is self-contained (just a URL), so open it HERE rather
            // than forwarding: this freshly-launched, toast-activated process holds the
            // foreground grant, so ShellExecute brings the browser to the foreground. The
            // long-running instance does not have that grant and the page would only flash
            // in the taskbar. Everything else (show / http / copy) needs the running
            // instance's window + state, so forward it there and exit.
            if (TryParseViewActivation(activationUrl, out var viewUrl))
                SafeUrl.Open(viewUrl);
            else if (!string.IsNullOrEmpty(activationUrl))
                SingleInstanceServer.TryForward(ActivationPipeName(), activationUrl);
            Shutdown(0);
            return;
        }

        // We're the first/only instance. Spin up the pipe listener so future
        // launches (e.g. another toast click) can forward their URLs to us.
        _activationServer = new SingleInstanceServer(ActivationPipeName());
        _activationServer.ActivationReceived += OnActivationReceived;
        _activationServer.Start();

        // Register the ntfy-desktop:// scheme so Windows knows how to dispatch
        // toast clicks back to this exe. Safe to re-apply on every launch.
        ProtocolRegistration.Apply();

        // Stash cold-start activation; routed below once the host is built and the
        // main window is reachable.
        _pendingActivationUrl = activationUrl;

        var builder = Host.CreateApplicationBuilder(e.Args);

        builder.Services.AddNtfyDesktop();

        _host = builder.Build();

        _host.Services.UseMessaging();

        await _host.StartAsync();

        ApplicationThemeManager.ApplySystemTheme();

        // Pre-warm the feed VM so the SQLite backfill happens before the user
        // opens the window. By the time they click Show, Messages is populated.
        _ = _host.Services.GetRequiredService<FeedViewModel>();

        _trayIcon = new(this);

        // Tray reflects two independent axes — wire each.
        var conn = _host.Services.GetRequiredService<ConnectionManager>();
        var gate = _host.Services.GetRequiredService<NotificationGate>();

        var unread = _host.Services.GetRequiredService<UnreadTracker>();

        _trayIcon.SetConnectionStatus(conn.GetConnectionStatus());
        _trayIcon.SetNotificationStatus(gate.GlobalStatus);
        _trayIcon.SetUnreadCount(unread.Total);

        // Bus subscriptions marshaled to the UI thread, so the tray updates directly.
        var bus = _host.Services.GetRequiredService<EventBus>();
        bus.Subscribe<ConnectionStatusChanged>(this,
            _ => _trayIcon?.SetConnectionStatus(conn.GetConnectionStatus()), ThreadOption.UIThread);
        bus.Subscribe<NotificationsStatusChanged>(this,
            _ => _trayIcon?.SetNotificationStatus(gate.GlobalStatus), ThreadOption.UIThread);
        bus.Subscribe<UnreadCountChanged>(this,
            _ => _trayIcon?.SetUnreadCount(unread.Total), ThreadOption.UIThread);

        // Cold-start activation: if launched directly by a toast click (no prior
        // instance), now that the host is up we can dispatch the URL.
        if (string.IsNullOrEmpty(_pendingActivationUrl)) return;
        var url = _pendingActivationUrl;
        _pendingActivationUrl = null;
        DispatchActivation(url);
    }

    private void OnActivationReceived(object? sender, string url) =>
        Dispatcher.Invoke(() => DispatchActivation(url));

    private void DispatchActivation(string url)
    {
        if (TryParseShowActivation(url, out var topicId))
        {
            ShowMainWindow();
            _host!.Services.GetRequiredService<MainWindow>().NavigateToTopic(topicId);
            return;
        }

        // Cold start via a "view" toast button: this launched process holds the foreground
        // grant, so opening the URL brings the browser forward. (Warm starts open it in the
        // second instance and never reach here.)
        if (TryParseViewActivation(url, out var viewUrl))
        {
            SafeUrl.Open(viewUrl);
            return;
        }

        if (TryParseActionActivation(url, out var msgId, out var actionIndex))
            _ = HandleActionActivationAsync(msgId, actionIndex);
    }

    // Runs a message action triggered from a toast button. Looks the message up in
    // history by id (it was stored before the toast fired), takes the indexed action,
    // and runs it through the same MessageActionInvoker the feed uses — so http is
    // confirmed before firing and the logic isn't duplicated. http brings the window
    // forward so its confirm dialog has focus; copy stays silent (no window pop).
    private async Task HandleActionActivationAsync(string messageId, int index)
    {
        var history = _host!.Services.GetRequiredService<HistoryRepository>();
        var message = history.GetByMessageId(messageId);

        if (message?.Actions is not { } actions || index < 0 || index >= actions.Count) return;

        var action = actions[index];
        if (!action.IsSupported) return;

        if (action.IsHttp) ShowMainWindow();

        await _host.Services.GetRequiredService<MessageActionInvoker>().InvokeAsync(action);
    }

    // ntfy-desktop://show?topic=<TopicId>
    private static bool TryParseShowActivation(string url, out Guid topicId)
    {
        topicId = Guid.Empty;

        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, ProtocolRegistration.SCHEME, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "show", StringComparison.OrdinalIgnoreCase)) return false;

        // Minimal query parsing — no System.Web dependency. Format: ?topic=<guid>
        var q = uri.Query;
        if (string.IsNullOrEmpty(q)) return false;
        if (q.StartsWith('?')) q = q[1..];

        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (string.Equals(key, "topic", StringComparison.OrdinalIgnoreCase))
                Guid.TryParse(value, out topicId);
        }

        return topicId != Guid.Empty;
    }

    // ntfy-desktop://view?url=<http(s) URL>. Only http/https pass (SafeUrl), so a malicious
    // publisher can't smuggle a file:// or custom-scheme target through a toast button.
    private static bool TryParseViewActivation(string? url, out string viewUrl)
    {
        viewUrl = string.Empty;

        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, ProtocolRegistration.SCHEME, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "view", StringComparison.OrdinalIgnoreCase)) return false;

        var q = uri.Query;
        if (string.IsNullOrEmpty(q)) return false;
        if (q.StartsWith('?')) q = q[1..];

        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (string.Equals(key, "url", StringComparison.OrdinalIgnoreCase)) viewUrl = value;
        }

        return SafeUrl.IsAllowed(viewUrl);
    }

    // ntfy-desktop://action?msg=<MessageId>&i=<ActionIndex>
    private static bool TryParseActionActivation(string url, out string messageId, out int index)
    {
        messageId = string.Empty;
        index = -1;

        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!string.Equals(uri.Scheme, ProtocolRegistration.SCHEME, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(uri.Host, "action", StringComparison.OrdinalIgnoreCase)) return false;

        // Minimal query parsing — no System.Web dependency. Format: ?msg=<id>&i=<index>
        var q = uri.Query;
        if (string.IsNullOrEmpty(q)) return false;
        if (q.StartsWith('?')) q = q[1..];

        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq]);
            var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (string.Equals(key, "msg", StringComparison.OrdinalIgnoreCase)) messageId = value;
            else if (string.Equals(key, "i", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out index);
        }

        return messageId.Length > 0 && index >= 0;
    }

    private static string? ExtractActivationUrl(string[] args) =>
        args.FirstOrDefault(a =>
            a.StartsWith(ProtocolRegistration.SCHEME + "://", StringComparison.OrdinalIgnoreCase));

    public void ShowMainWindow()
    {
        var window = _host!.Services.GetRequiredService<MainWindow>();

        if (!window.IsVisible)
            window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void TogglePause()
    {
        var gate = _host!.Services.GetRequiredService<NotificationGate>();
        if (gate.IsGloballyPaused) gate.ResumeAll();
        else                       gate.PauseAll();
    }

    public async void DisconnectAllConnections()
    {
        var conn = _host!.Services.GetRequiredService<ConnectionManager>();
        await conn.DisconnectAllAsync();
    }

    public async void ReconnectAllConnections()
    {
        // Hard reset: tears down all sockets and brings them back up. The plain
        // ApplySettingsAsync would no-op now that it's idempotent.
        var conn = _host!.Services.GetRequiredService<ConnectionManager>();
        await conn.RestartAllAsync();
    }

    public void QuitApp() => Shutdown(0);

    protected override async void OnExit(ExitEventArgs e)
    {
        _activationServer?.Dispose();
        _trayIcon?.Dispose();

        if (_host != null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(3));
            }
            catch { /* shutdown best-effort */ }
            _host.Dispose();
        }

        _mutex?.ReleaseMutex();
        _mutex?.Dispose();

        base.OnExit(e);
    }
}
