using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules;
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
//
// Concurrency: every mutation of _connections runs under _gate. The lock is
// taken ONCE at the public-operation boundary; the *Internal helpers assume the
// gate is already held and never take it themselves (so composites don't
// deadlock on the non-reentrant semaphore). Internal helpers also raise no
// events — the public method publishes once, after releasing the gate, so bus
// handlers never run while we hold the lock.
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly HistoryRepository _history;
    private readonly RuleEngine _rules;
    private readonly ConcurrentDictionary<Guid, TopicConnection> _connections = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConnectionManager(AppSettings settings, HistoryRepository history, RuleEngine rules)
    {
        _settings = settings;
        _history = history;
        _rules = rules;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    // ===== Queries (lock-free reads off the ConcurrentDictionary) =====

    public ConnectionStatus GetConnectionStatus()
    {
        // No live sockets — either zero topics configured, or the user explicitly
        // tore everything down. Either way: nothing is connected.
        if (_connections.IsEmpty)
            return ConnectionStatus.Disconnected;

        return _connections.Values.All(c => c.Status == TopicConnectionStatus.Connected)
            ? ConnectionStatus.Connected
            : ConnectionStatus.Degraded;
    }

    public TopicConnectionStatus GetTopicConnectionStatus(Guid topicId)
        => GetTopicConnection(topicId, out var conn) ? conn.Status : TopicConnectionStatus.Disconnected;

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

    public bool GetTopicConnection(Guid topicId, [NotNullWhen(true)] out TopicConnection? conn)
        => _connections.TryGetValue(topicId, out conn);

    public bool TopicHasConnection(Guid topicId)
        => _connections.ContainsKey(topicId);

    // ===== Public mutations (gate once, then publish once) =====

    /// <summary>
    /// Idempotent: brings the live connection set in line with the configured
    /// enabled topics. Removes connections for topics that disappeared / became
    /// disabled, rebuilds ones whose subscription identity drifted, and adds
    /// newly-enabled topics; untouched connections keep running.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync();
        try { await ApplyInternalAsync(); }
        finally { _gate.Release(); }

        await PublishConnectionStatusChangedAsync();
    }

    // Full teardown + fresh start. Called when a server's URL or token changed
    // (existing sockets need to reauthenticate) and from the user-facing
    // "Reconnect all" action.
    public async Task RestartAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ClearAllInternalAsync();
            await ApplyInternalAsync();
        }
        finally { _gate.Release(); }

        await PublishConnectionStatusChangedAsync();
    }

    // Tears down and re-applies connections for every topic on a server. Use after
    // a server's URL or token changed, without disturbing other servers' sockets.
    public async Task RebuildServerAsync(Guid serverId)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var id in _settings.Topics.Where(t => t.ServerId == serverId).Select(t => t.Id).ToList())
                await RemoveConnectionInternalAsync(id);
            await ApplyInternalAsync();
        }
        finally { _gate.Release(); }

        await PublishConnectionStatusChangedAsync();
    }

    // Hard-reset: tears down every WebSocket subscription. The connections stay
    // down until ApplySettingsAsync() (or "Reconnect all" in the UI) brings them
    // back. Settings are not touched, so an app restart resumes normal subscription.
    public async Task DisconnectAllAsync()
    {
        await _gate.WaitAsync();
        try { await ClearAllInternalAsync(); }
        finally { _gate.Release(); }

        await PublishConnectionStatusChangedAsync();
    }

    public async Task AddTopicConnectionAsync(TopicSettings topic)
    {
        await _gate.WaitAsync();
        try { AddConnectionInternal(topic); }
        finally { _gate.Release(); }

        await PublishConnectionStatusChangedAsync();
    }

    public async Task RemoveTopicConnectionAsync(Guid topicId)
    {
        bool removed;
        await _gate.WaitAsync();
        try { removed = await RemoveConnectionInternalAsync(topicId); }
        finally { _gate.Release(); }

        if (removed) await PublishConnectionStatusChangedAsync();
    }

    // Tears down and re-applies a single topic's connection. Use after an edit that
    // changes its subscription identity (topic name or server), so the connection
    // re-resolves instead of keeping the stale server/name it captured.
    public async Task RebuildTopicConnectionAsync(Guid topicId)
    {
        await _gate.WaitAsync();
        try
        {
            await RemoveConnectionInternalAsync(topicId);
            var topic = _settings.GetTopicById(topicId);
            if (topic is not null) AddConnectionInternal(topic);
        }
        finally { _gate.Release(); }

        await PublishConnectionStatusChangedAsync();
    }

    // ReconnectTopic / ReconnectAll only flip the socket's CTS; they don't mutate
    // _connections, so they need no gate. Iterating .Values is a safe snapshot.
    public void ReconnectTopic(Guid topicId)
    {
        if (_connections.TryGetValue(topicId, out var conn))
            conn.ForceReconnect();
    }

    public void ReconnectAll()
    {
        foreach (var conn in _connections.Values)
            conn.ForceReconnect();
    }

    // ===== Internal primitives (gate held by caller; raise no events) =====

    // Reconcile the live set to the enabled topics. Caller holds _gate.
    private async Task ApplyInternalAsync()
    {
        var desired = _settings.Topics.Where(t => t.Enabled).ToDictionary(t => t.Id);

        // Existing connections whose subscription identity drifted → rebuild.
        foreach (var id in desired.Keys.Intersect(_connections.Keys).ToList())
            if (!_connections[id].MatchesTopicSettings(desired[id]))
                await RemoveConnectionInternalAsync(id); // re-added by the add loop below

        // Stop + remove connections that are no longer wanted.
        foreach (var id in _connections.Keys.Except(desired.Keys).ToList())
            await RemoveConnectionInternalAsync(id);

        // Add connections for newly-enabled topics; existing ones keep running.
        foreach (var id in desired.Keys.Except(_connections.Keys).ToList())
            AddConnectionInternal(desired[id]);
    }

    private async Task ClearAllInternalAsync()
    {
        foreach (var id in _connections.Keys.ToList())
            await RemoveConnectionInternalAsync(id);
    }

    private void AddConnectionInternal(TopicSettings topic)
    {
        var server = _settings.GetServer(topic.ServerId);
        if (server is null) return; // orphaned topic (server removed) — skip silently

        // Prime a baseline so this topic is never cursorless on a future reconnect (which
        // would skip catch-up). No-ops if it already has a cursor — never rewinds.
        _history.EnsureCursor(topic.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var conn = new TopicConnection(
            topic.Id,
            topic.Name,
            topic.ServerId,
            () => server.Url,
            server.GetAuthorizationHeader,
            () => _history.GetSinceValue(topic.Id));

        conn.MessageReceived += OnMessageReceived;
        conn.StateChanged += OnTopicConnectionStatusChanged;

        _connections[topic.Id] = conn;

        conn.Start(); // synchronously transitions to Connecting → fires the granular event
    }

    private async Task<bool> RemoveConnectionInternalAsync(Guid topicId)
    {
        if (!_connections.TryRemove(topicId, out var conn)) return false;

        // Detach MessageReceived first so a message in flight during shutdown isn't
        // inserted, but keep StateChanged attached across StopAsync so the socket's
        // final transition to Disconnected reaches consumers (resets the pip).
        conn.MessageReceived -= OnMessageReceived;
        await conn.StopAsync();
        conn.StateChanged -= OnTopicConnectionStatusChanged;

        return true;
    }

    // ===== Socket callbacks (off the gate) =====

    private static void OnTopicConnectionStatusChanged(object? sender, TopicConnectionStatus status)
    {
        if (sender is not TopicConnection conn) return;

        _ = new ConnectionStatusChanged().PublishAsync();
        _ = new TopicConnectionStatusChanged(conn.TopicId, conn.Status, conn.LastError).PublishAsync();
    }

    private void OnMessageReceived(object? sender, IncomingMessage incoming)
    {
        if (sender is not TopicConnection conn) return;

        var topic = _settings.GetTopicById(conn.TopicId);
        var serverId = topic?.ServerId ?? Guid.Empty;

        // Deterministic rule engine decides suppression + correlation. Pure read here;
        // incident-store writes are applied below only for genuinely-new messages.
        var verdict = _rules.Evaluate(incoming.Message);

        // Insert is INSERT-OR-IGNORE and reports novelty. A `since=<time>` catch-up is
        // inclusive of its boundary, so it re-delivers messages we already have — those
        // aren't new and must not reach the toast/summary path (the only consumer of
        // NtfyMessageReceived), or every reconnect would show a phantom "while you were
        // away" summary for the re-sent boundary message.
        var isNew = _history.Insert(incoming.Message, conn.TopicId, serverId, verdict.HideFromFeed);
        if (!isNew) return;

        // Apply incident side-effects (open/resolve) once, for the new message only.
        _rules.ApplyIncidentSideEffects(verdict);

        // A resolution folds its problem out of the feed: retroactively hide that row.
        if (verdict.DismissMessageId is { } dismissId)
            _history.SuppressMessage(dismissId);

        new NtfyMessageReceived(incoming.Message, conn.TopicId, incoming.IsBackfill, verdict.SuppressToast)
            .PublishAsync();
    }

    // Resume the connections regardless of notification-pause state — sockets
    // and pause are independent now. Toast suppression happens downstream in
    // ShowToastNotification via NotificationGate.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            ReconnectAll();
    }

    private static Task PublishConnectionStatusChangedAsync() => new ConnectionStatusChanged().PublishAsync();

    public async ValueTask DisposeAsync()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;

        foreach (var conn in _connections.Values)
            await conn.DisposeAsync();
    }
}
