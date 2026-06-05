using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using H.NotifyIcon;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Notifications;

namespace NtfyDesktop.Features.Shell;

// Owns the system-tray icon: the app's bell glyph on a background coloured by
// connection status (green / amber / red), so health reads at a glance without
// opening the window. Pause is independent — it doesn't change the icon (sockets
// aren't unhealthy when paused); it only flips the menu item label and tooltip.
internal sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;

    // The icon currently handed to the tray. H.NotifyIcon disposes the icon it's
    // given when a new one replaces it, so we hand it a *fresh* icon each render
    // and dispose the previous one ourselves — never reuse an instance.
    private System.Drawing.Icon? _activeIcon;

    private ConnectionStatus _lastConnection = ConnectionStatus.Disconnected;
    private NotificationStatus _lastNotifications = NotificationStatus.Active;
    private int _lastUnread;

    public TrayIconHost(App app)
    {
        _pauseItem = new MenuItem
        {
            Header = "Pause notifications",
            Command = new RelayCommand(app.TogglePause),
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = App.NAME,
            ContextMenu = BuildContextMenu(app, _pauseItem),
            LeftClickCommand = new RelayCommand(app.ShowMainWindow),
            NoLeftClickDelay = true,
        };

        Render();
        _icon.ForceCreate();
    }

    public void SetConnectionStatus(ConnectionStatus status)
    {
        if (status == _lastConnection) return; // avoid reloading the icon on no-op updates
        _lastConnection = status;
        Render();
    }

    public void SetNotificationStatus(NotificationStatus status)
    {
        if (status == _lastNotifications) return; // avoid reloading the icon on no-op updates
        _lastNotifications = status;
        Render();
    }

    public void SetUnreadCount(int count)
    {
        if (count == _lastUnread) return; // avoid rebuilding the icon on no-op updates
        _lastUnread = count;
        Render();
    }

    private void Render()
    {
        var (file, connectionWord) = _lastConnection switch
        {
            ConnectionStatus.Connected    => ("tray-connected.ico",    "connected"),
            ConnectionStatus.Degraded     => ("tray-degraded.ico",     "reconnecting"),
            ConnectionStatus.Disconnected => ("tray-disconnected.ico", "disconnected"),
            _                             => ("tray-disconnected.ico", "—"),
        };

        // Fresh icon each time; dispose the one we previously handed off.
        var fresh = LoadIcon(file);
        _icon.Icon = fresh;
        _activeIcon?.Dispose();
        _activeIcon = fresh;

        var unreadWord = _lastUnread > 0 ? $", {_lastUnread} unread" : string.Empty;
        _icon.ToolTipText = _lastNotifications == NotificationStatus.Paused
            ? $"{App.NAME} — {connectionWord}, notifications paused{unreadWord}"
            : $"{App.NAME} — {connectionWord}{unreadWord}";

        _pauseItem.Header = _lastNotifications == NotificationStatus.Paused
            ? "Resume notifications"
            : "Pause notifications";
    }

    private static System.Drawing.Icon LoadIcon(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/assets/{fileName}", UriKind.Absolute);
        using var stream = Application.GetResourceStream(uri)!.Stream;
        // Pick the 32px frame — crisp at the DPI scales the tray typically uses.
        return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
    }

    private static ContextMenu BuildContextMenu(App app, MenuItem pauseItem) =>
        new()
        {
            Items =
            {
                new MenuItem { Header = "Show", Command = new RelayCommand(app.ShowMainWindow) },
                new Separator(),
                pauseItem,
                new Separator(),
                new MenuItem { Header = "Disconnect all", Command = new RelayCommand(app.DisconnectAllConnections) },
                new MenuItem { Header = "Reconnect all",  Command = new RelayCommand(app.ReconnectAllConnections) },
                new Separator(),
                new MenuItem { Header = "Quit", Command = new RelayCommand(app.QuitApp) },
            },
        };

    public void Dispose()
    {
        _icon.Dispose();
        _activeIcon?.Dispose();
    }

    private sealed class RelayCommand(Action execute) : ICommand
    {
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
        public event EventHandler? CanExecuteChanged
        {
            add { /* always executable */ }
            remove { /* always executable */ }
        }
    }
}
