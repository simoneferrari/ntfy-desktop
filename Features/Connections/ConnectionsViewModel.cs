using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NtfyDesktop.Features.Connections;

// Backs the Connections page: per-topic connection state plus Reconnect /
// Disconnect-all / Reconnect-all hard resets. Notification pause is a
// separate concern — it lives on the title bar and per-topic context menu,
// not on this page.
public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;

    public ObservableCollection<TopicConnectionRow> Rows { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;

    public ConnectionsViewModel(ConnectionManager connections)
    {
        _connections = connections;
        _connections.ConnectionStatusChanged += OnChanged;
        _connections.TopicsChanged += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        Rows.Clear();
        var states = _connections.GetTopicStates();
        // Show the server only when there's more than one in play — otherwise it's noise.
        var showServer = states.Select(s => s.ServerName).Distinct().Count() > 1;
        foreach (var s in states)
            Rows.Add(new TopicConnectionRow(s, showServer));
        IsEmpty = Rows.Count == 0;
        DisconnectAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Reconnect(Guid topicId)
    {
        if (topicId == Guid.Empty) return;
        _connections.ReconnectTopic(topicId);
    }

    [RelayCommand]
    private async Task ReconnectAll() => await _connections.RestartAllAsync();

    [RelayCommand(CanExecute = nameof(CanDisconnectAll))]
    private async Task DisconnectAll() => await _connections.DisconnectAllAsync();

    private bool CanDisconnectAll() =>
        Rows.Any(r => r.ConnectionStatus != TopicConnectionStatus.Disconnected);
}

public sealed class TopicConnectionRow
{
    private static readonly Brush ConnectedBrush    = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush ConnectingBrush   = Frozen(Color.FromRgb(0xEA, 0x58, 0x0C));
    private static readonly Brush DisconnectedBrush = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public TopicConnectionRow(TopicConnectionState state, bool showServer)
    {
        TopicId = state.TopicId;
        TopicName = state.TopicName;
        DisplayName = state.DisplayName;
        ServerName = state.ServerName;
        ShowServer = showServer && !string.IsNullOrEmpty(state.ServerName);
        ConnectionStatus = state.Status;
        LastError = state.LastError;
    }

    public Guid TopicId { get; }
    public string TopicName { get; }
    public string DisplayName { get; }
    public string ServerName { get; }
    public bool ShowServer { get; }
    public TopicConnectionStatus ConnectionStatus { get; }
    public string? LastError { get; }

    public string StatusText => ConnectionStatus switch
    {
        TopicConnectionStatus.Connected    => "Connected",
        TopicConnectionStatus.Connecting   => "Connecting…",
        TopicConnectionStatus.Disconnected => "Disconnected",
        _ => "—",
    };

    public Brush StatusBrush => ConnectionStatus switch
    {
        TopicConnectionStatus.Connected    => ConnectedBrush,
        TopicConnectionStatus.Connecting   => ConnectingBrush,
        TopicConnectionStatus.Disconnected => DisconnectedBrush,
        _ => DisconnectedBrush,
    };

    public bool HasError => !string.IsNullOrEmpty(LastError);
}
