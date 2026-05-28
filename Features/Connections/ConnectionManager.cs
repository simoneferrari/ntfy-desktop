using FastEndpoints;
using Microsoft.Win32;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Topics;

namespace NtfyDesktop.Features.Connections;

// Owns live WebSocket subscriptions per configured topic. Pure connection
// concerns only — pause (whether toasts are delivered) lives in
// Features.Notifications.NotificationGate. Consumers that need both axes
// compose them at the call site.
//
// Keyed by TopicId (not topic name): topic names are no longer unique across
// servers, and each topic resolves its own server's URL + token.
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly HistoryRepository _history;
    private readonly Dictionary<Guid, TopicConnection> _connections = new();

    public event EventHandler? ConnectionStatusChanged;
    public event EventHandler? TopicsChanged;

    public ConnectionManager(AppSettings settings, HistoryRepository history)
    {
        _settings = settings;
        _history = history;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public ConnectionStatus GetConnectionStatus()
    {
        // No live sockets — either zero topics configured, or the user explicitly
        // tore everything down. Either way: nothing is connected.
        if (_connections.Count == 0)
            return ConnectionStatus.Disconnected;

        return _connections.Values.All(c => c.Status == TopicConnectionStatus.Connected)
            ? ConnectionStatus.Connected
            : ConnectionStatus.Degraded;
    }

    /// <summary>
    /// Per-topic connection snapshot. Sourced from configured topics so the UI
    /// can show topics that haven't connected yet; the live connection is looked
    /// up by TopicId where available. Pause is a separate axis — query
    /// NotificationGate at the call site.
    /// </summary>
    public IReadOnlyList<TopicConnectionState> GetTopicStates() =>
        _settings.Topics
            .Select(topic =>
            {
                _connections.TryGetValue(topic.Id, out var conn);
                return new TopicConnectionState(
                    topic.Id,
                    topic.Name,
                    topic.EffectiveDisplayName,
                    _settings.GetServer(topic.ServerId)?.DisplayLabel ?? string.Empty,
                    conn?.Status ?? TopicConnectionStatus.Disconnected,
                    conn?.LastError);
            })
            .ToList();

    /// <summary>
    /// Idempotent: brings the live connection set in line with the configured
    /// enabled topics. Removes connections for topics that disappeared / became
    /// disabled, adds connections for newly-enabled topics, leaves untouched
    /// connections alone. Use RestartAllAsync() when a server's URL or token
    /// changed and existing sockets must reauthenticate.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        var desired = _settings.Topics.Where(t => t.Enabled).ToDictionary(t => t.Id);

        // Stop and remove connections that are no longer wanted.
        foreach (var id in _connections.Keys.Except(desired.Keys).ToList())
            await RemoveConnectionAsync(id);

        // Add connections for newly-enabled topics; existing ones keep running.
        foreach (var id in desired.Keys.Except(_connections.Keys).ToList())
            AddConnection(desired[id]);

        ConnectionStatusChanged?.Invoke(this, EventArgs.Empty);
        TopicsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Full teardown + fresh start. Called when a server's URL or token changed
    // (existing sockets need to reauthenticate) and from the user-facing
    // "Reconnect all" action.
    public async Task RestartAllAsync()
    {
        foreach (var conn in _connections.Values)
            await conn.StopAsync();
        _connections.Clear();

        await ApplySettingsAsync();
    }

    public void ReconnectTopic(Guid topicId)
    {
        if (_connections.TryGetValue(topicId, out var conn))
            conn.ForceReconnect();
    }

    // Tears down and re-applies a single topic's connection. Use after an edit that
    // changes its subscription identity (topic name or server), so the connection
    // re-resolves instead of keeping the stale server/name it captured.
    public async Task RebuildTopicAsync(Guid topicId)
    {
        await RemoveConnectionAsync(topicId);
        await ApplySettingsAsync();
    }

    // Tears down and re-applies connections for every topic on a server. Use after
    // a server's URL or token changed, without disturbing other servers' sockets.
    public async Task RebuildServerAsync(Guid serverId)
    {
        var ids = _settings.Topics.Where(t => t.ServerId == serverId).Select(t => t.Id).ToList();
        foreach (var id in ids)
            await RemoveConnectionAsync(id);
        await ApplySettingsAsync();
    }

    public void ReconnectAll()
    {
        foreach (var conn in _connections.Values)
            conn.ForceReconnect();
    }

    // Hard-reset: tears down every WebSocket subscription. The connections stay
    // down until ApplySettingsAsync() (or "Reconnect all" in the UI) brings them
    // back. Settings are not touched, so an app restart resumes normal subscription.
    public async Task DisconnectAllAsync()
    {
        foreach (var conn in _connections.Values)
            await conn.StopAsync();

        _connections.Clear();

        ConnectionStatusChanged?.Invoke(this, EventArgs.Empty);
        TopicsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddConnection(TopicSettings topic)
    {
        var server = _settings.GetServer(topic.ServerId);
        if (server is null) return; // orphaned topic (server removed) — skip silently

        var conn = new TopicConnection(
            topic.Id,
            topic.Name,
            () => server.Url,
            () => server.GetAccessToken());

        conn.MessageReceived += OnMessageReceived;
        conn.StateChanged += OnTopicConnectionStatusChanged;

        _connections[topic.Id] = conn;

        conn.Start();
    }

    private async Task RemoveConnectionAsync(Guid topicId)
    {
        if (!_connections.TryGetValue(topicId, out var conn)) return;

        conn.MessageReceived -= OnMessageReceived;
        conn.StateChanged -= OnTopicConnectionStatusChanged;

        await conn.StopAsync();

        _connections.Remove(topicId);
    }

    private void OnTopicConnectionStatusChanged(object? sender, TopicConnectionStatus status)
    {
        ConnectionStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMessageReceived(object? sender, NtfyMessage message)
    {
        if (sender is not TopicConnection conn) return;

        var topic = _settings.GetTopicById(conn.TopicId);
        var serverId = topic?.ServerId ?? Guid.Empty;

        _history.Insert(message, conn.TopicId, serverId);

        new NtfyMessageReceived(message, conn.TopicId).PublishAsync(Mode.WaitForNone);
    }

    // Resume the connections regardless of notification-pause state — sockets
    // and pause are independent now. Toast suppression happens downstream in
    // ShowToastNotification via NotificationGate.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ReconnectAll();
    }

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        foreach (var conn in _connections.Values)
            await conn.DisposeAsync();
    }
}
