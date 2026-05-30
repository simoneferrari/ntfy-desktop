using System.IO;
using System.Windows;
using FastEndpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NtfyDesktop.Features;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Feed;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Shell;
using NtfyDesktop.Features.Unread;
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
            // Another instance is already running for this data folder. Forward the
            // activation URL (if any) to it so it can bring the right feed forward,
            // then exit. If forwarding fails the running instance just won't react —
            // we still don't want two instances side-by-side.
            if (!string.IsNullOrEmpty(activationUrl))
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

        conn.ConnectionStatusChanged += (_, _) =>
            Dispatcher.Invoke(() => _trayIcon?.SetConnectionStatus(conn.GetConnectionStatus()));
        gate.GlobalStatusChanged += (_, _) =>
            Dispatcher.Invoke(() => _trayIcon?.SetNotificationStatus(gate.GlobalStatus));
        unread.Changed += (_, _) =>
            Dispatcher.Invoke(() => _trayIcon?.SetUnreadCount(unread.Total));

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
        if (!TryParseShowActivation(url, out var topicId)) return;

        ShowMainWindow();

        var window = _host!.Services.GetRequiredService<MainWindow>();
        window.NavigateToTopic(topicId);
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
