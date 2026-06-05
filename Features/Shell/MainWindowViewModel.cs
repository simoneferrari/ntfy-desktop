using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Notifications.Events;

namespace NtfyDesktop.Features.Shell;

// Owns the chrome state for MainWindow: connection-health pip + text in the title
// bar, and a separate "paused" chip when notifications are paused. Both are
// aggregate views, so they re-read on the coarse bus events.
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;

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

    public MainWindowViewModel(ConnectionManager connections, NotificationGate gate, EventBus bus)
    {
        _connections = connections;
        _gate = gate;

        // Aggregate displays — re-read on the coarse events (handlers run on the UI thread).
        bus.Subscribe<ConnectionStatusChanged>(this, _ => Refresh(), ThreadOption.UIThread);
        bus.Subscribe<NotificationsStatusChanged>(this, _ => Refresh(), ThreadOption.UIThread);

        Refresh();
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_gate.IsGloballyPaused) _gate.ResumeAll();
        else                        _gate.PauseAll();
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
