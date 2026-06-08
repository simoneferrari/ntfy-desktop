using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Notifications.Events;
using NtfyDesktop.Features.Updates;
using NtfyDesktop.Features.Updates.Events;

namespace NtfyDesktop.Features.Shell;

// Owns the chrome state for MainWindow: connection-health pip + text in the title
// bar, a "paused" banner when notifications are paused, and an "update available"
// banner. The status/pause state are aggregate views re-read on coarse bus events.
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;
    private readonly UpdateService _updates;

    [ObservableProperty]
    private ConnectionStatus _connectionStatus;

    [ObservableProperty]
    private string _connectionStatusText = "Connecting…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseButtonLabel))]
    [NotifyPropertyChangedFor(nameof(ShowPauseButton))]
    private bool _isGloballyPaused;

    public string PauseButtonLabel => IsGloballyPaused ? "Resume notifications" : "Pause notifications";

    // When paused, the banner takes over as the resume entry point — the
    // title-bar button is hidden to avoid two redundant controls.
    public bool ShowPauseButton => !IsGloballyPaused;

    // ===== Update banner =====
    // Raised true once the background checker finds a newer release. Stays up until
    // the user applies it (which restarts the app) or quits.
    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
    private string _updateVersion = "";

    // Disables the button and switches its label while the download is in flight
    // (ApplyUpdate restarts the process on success, so this only resets on failure).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanApplyUpdate))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonLabel))]
    private bool _isApplyingUpdate;

    public string UpdateBannerText =>
        UpdateVersion.Length > 0 ? $"Version {UpdateVersion} is available" : "An update is available";

    public string UpdateButtonLabel => IsApplyingUpdate ? "Updating…" : "Restart & update";

    public bool CanApplyUpdate => !IsApplyingUpdate;

    public MainWindowViewModel(ConnectionManager connections, NotificationGate gate,
        UpdateService updates, EventBus bus)
    {
        _connections = connections;
        _gate = gate;
        _updates = updates;

        // Aggregate displays — re-read on the coarse events (handlers run on the UI thread).
        bus.Subscribe<ConnectionStatusChanged>(this, _ => Refresh(), ThreadOption.UIThread);
        bus.Subscribe<NotificationsStatusChanged>(this, _ => Refresh(), ThreadOption.UIThread);

        bus.Subscribe<UpdateStatusChanged>(this, e =>
        {
            UpdateVersion = e.Version;
            IsUpdateAvailable = e.Available;
        }, ThreadOption.UIThread);

        // The window (and this VM) is created lazily on first show, which may be
        // after the checker already found an update and fired its one-shot event.
        // Seed from the service so a waiting update still surfaces.
        if (_updates.IsUpdatePending)
        {
            UpdateVersion = _updates.PendingVersion ?? "";
            IsUpdateAvailable = true;
        }

        Refresh();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_gate.IsGloballyPaused) _gate.ResumeAll();
        else                        _gate.PauseAll();
    }

    // Download the pending update and restart into it. The process exits inside
    // DownloadAndApplyAsync on success; on failure we drop the busy state so the
    // user can retry (and the next daily check will surface it again anyway).
    [RelayCommand]
    private async Task ApplyUpdate()
    {
        if (IsApplyingUpdate) return;
        IsApplyingUpdate = true;
        try
        {
            await _updates.DownloadAndApplyAsync();
        }
        catch
        {
            IsApplyingUpdate = false;
        }
    }

    private void Refresh()
    {
        ConnectionStatus = _connections.GetConnectionStatus();
        ConnectionStatusText = ConnectionStatus switch
        {
            ConnectionStatus.Connected    => "Connected",
            ConnectionStatus.Degraded     => "Reconnecting…",
            ConnectionStatus.Disconnected => "Disconnected",
            _                             => "—",
        };
        IsGloballyPaused = _gate.IsGloballyPaused;
    }
}
