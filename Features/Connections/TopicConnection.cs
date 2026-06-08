using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Topics;

namespace NtfyDesktop.Features.Connections;

// getSince returns the ntfy `since=` value for this topic — the Unix timestamp of the last
// message we acknowledged from the server (or null when there's none). It comes from the
// durable topic_cursor table, not from message history, so deletes/retention don't rewind
// it. Re-read on every connect attempt so the socket resumes via ntfy's `since=` — closing
// the gap left by an app restart or any mid-session reconnect. A timestamp (not a message
// id) is used so a stale cursor still works: an id that's aged out of the server cache
// makes ntfy return nothing, whereas an old timestamp just returns whatever's still cached.
public sealed class TopicConnection(Guid topicId, string topicName, Guid serverId, Func<string> getServerUrl, Func<string?> getAuthHeader, Func<string?> getSince) : IAsyncDisposable
{
    private CancellationTokenSource _cts = new();
    private Task _runTask = Task.CompletedTask;
    private TopicConnectionStatus _status = TopicConnectionStatus.Disconnected;

    // Per-attempt catch-up state, set just before each connect. _resumedSince is the
    // `since=` cursor we subscribed with (null = none, fresh live subscription);
    // _connectStartedUnix bounds "live" — messages older than it are server-replayed
    // backlog, not live publishes. See ProcessRawMessage.
    private string? _resumedSince;
    private long _connectStartedUnix;

    public Guid TopicId => topicId;
    public string TopicName => topicName;
    public TopicConnectionStatus Status => _status;
    public string? LastError { get; private set; }

    public event EventHandler<IncomingMessage>? MessageReceived;
    public event EventHandler<TopicConnectionStatus>? StateChanged;

    private static readonly TimeSpan[] _backoffDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    public void Start()
    {
        _cts = new();
        _runTask = RunAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        await _cts.CancelAsync();
        try { await _runTask; } catch { /* cancelled */ }
    }

    public void ForceReconnect()
    {
        _cts.Cancel();
        _cts = new();
        _runTask = RunAsync(_cts.Token);
    }

    private void SetState(TopicConnectionStatus status)
    {
        if (_status == status) return;
        _status = status;
        StateChanged?.Invoke(this, status);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoffIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            SetState(TopicConnectionStatus.Connecting);

            try
            {
                using var ws = new ClientWebSocket();

                // Resume from our last acknowledged message (re-read each attempt).
                // Capture the attempt start just before connecting: any replayed message
                // timestamped before this instant is backlog, not a live publish.
                _resumedSince = getSince();
                _connectStartedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var uri = BuildWebSocketUri(_resumedSince);
                var isSecure = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
                var authHeader = getAuthHeader();

                // Refuse to attach credentials over cleartext (ws://) — covers both the
                // bearer token and HTTP Basic. The Settings page warns the user; the
                // connection itself still goes through unauthenticated, which is the safe
                // failure mode.
                if (!string.IsNullOrEmpty(authHeader) && isSecure)
                    ws.Options.SetRequestHeader("Authorization", authHeader);

                await ws.ConnectAsync(uri, ct);

                LastError = null;
                SetState(TopicConnectionStatus.Connected);

                backoffIndex = 0;

                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            if (ct.IsCancellationRequested) break;

            SetState(TopicConnectionStatus.Disconnected);

            var delay = _backoffDelays[Math.Min(backoffIndex, _backoffDelays.Length - 1)];
            backoffIndex++;

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }

        SetState(TopicConnectionStatus.Disconnected);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var messageBuffer = new StringBuilder();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            messageBuffer.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

            if (!result.EndOfMessage) continue;

            var json = messageBuffer.ToString();
            messageBuffer.Clear();
            ProcessRawMessage(json);
        }
    }

    private void ProcessRawMessage(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<NtfyMessage>(json);
            if (msg?.Event == "message")
            {
                // Backlog only exists when we resumed with `since`; then anything
                // published before this connect attempt is replayed history, not live.
                // ntfy sends no live/replay delimiter over the socket, so we bound it by
                // time — same approach as the ntfy web client.
                var isBackfill = _resumedSince is not null && msg.Time < _connectStartedUnix;
                MessageReceived?.Invoke(this, new IncomingMessage(msg, isBackfill));
            }
            // "keepalive" and "open" events are intentionally ignored
        }
        catch { /* malformed JSON — ignore */ }
    }

    private Uri BuildWebSocketUri(string? since)
    {
        var server = getServerUrl().TrimEnd('/');

        var wsServer = server
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);

        // With `since=<message-id>` ntfy replays cached messages after that id, then
        // streams live on the same socket. Omitted for a topic with no history so a
        // brand-new subscription doesn't pull the server's whole cache.
        var query = string.IsNullOrEmpty(since) ? "" : $"?since={Uri.EscapeDataString(since)}";
        return new Uri($"{wsServer}/{topicName}/ws{query}");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    
    public bool MatchesTopicSettings(TopicSettings topic)
        => topicName == topic.Name && serverId == topic.ServerId;
}

// A message received off a topic socket. IsBackfill = replayed from the server's
// cache via `since=` (a missed message), as opposed to a live publish.
public sealed record IncomingMessage(NtfyMessage Message, bool IsBackfill);
