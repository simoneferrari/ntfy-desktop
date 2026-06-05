using System.Windows.Media;

namespace NtfyDesktop.Features.Connections;

// Immutable per-topic row for the Connections page. WithStatus produces a copy with
// a new connection status/error so the page can replace a single row on
// TopicConnectionStatusChanged without rebuilding the whole list.
public sealed class TopicConnectionRow
{
    private static readonly Brush ConnectedBrush    = Frozen(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush ConnectingBrush   = Frozen(Color.FromRgb(0xEA, 0x58, 0x0C));
    private static readonly Brush DisconnectedBrush = Frozen(Color.FromRgb(0xDC, 0x26, 0x26));

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public TopicConnectionRow(TopicConnectionState state, bool showServer)
        : this(state.TopicId, state.TopicName, state.DisplayName, state.ServerName,
               showServer && !string.IsNullOrEmpty(state.ServerName), state.Status, state.LastError)
    {
    }

    private TopicConnectionRow(
        Guid topicId, string topicName, string displayName, string serverName,
        bool showServer, TopicConnectionStatus status, string? lastError)
    {
        TopicId = topicId;
        TopicName = topicName;
        DisplayName = displayName;
        ServerName = serverName;
        ShowServer = showServer;
        ConnectionStatus = status;
        LastError = lastError;
    }

    // Copy with the same display fields but a new connection status/error.
    public TopicConnectionRow WithStatus(TopicConnectionStatus status, string? lastError) =>
        new(TopicId, TopicName, DisplayName, ServerName, ShowServer, status, lastError);

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
