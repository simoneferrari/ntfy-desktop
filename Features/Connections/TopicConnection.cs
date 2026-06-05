using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Topics;

namespace NtfyDesktop.Features.Connections;

public sealed class TopicConnection(Guid topicId, string topicName, Guid serverId, Func<string> getServerUrl, Func<string> getToken) : IAsyncDisposable
{
    private CancellationTokenSource _cts = new();
    private Task _runTask = Task.CompletedTask;
    private TopicConnectionStatus _status = TopicConnectionStatus.Disconnected;

    public Guid TopicId => topicId;
    public string TopicName => topicName;
    public TopicConnectionStatus Status => _status;
    public string? LastError { get; private set; }

    public event EventHandler<NtfyMessage>? MessageReceived;
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

                var uri = BuildWebSocketUri();
                var isSecure = uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
                var token = getToken();

                // Refuse to attach the bearer token over cleartext (ws://). The
                // Settings page warns the user; the connection itself still goes
                // through unauthenticated, which is the safe failure mode.
                if (!string.IsNullOrEmpty(token) && isSecure)
                    ws.Options.SetRequestHeader("Authorization", $"Bearer {token}");

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
                MessageReceived?.Invoke(this, msg);
            // "keepalive" and "open" events are intentionally ignored
        }
        catch { /* malformed JSON — ignore */ }
    }

    private Uri BuildWebSocketUri()
    {
        var server = getServerUrl().TrimEnd('/');

        var wsServer = server
            .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);

        return new Uri($"{wsServer}/{topicName}/ws");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    
    public bool MatchesTopicSettings(TopicSettings topic)
        => topicName == topic.Name && serverId == topic.ServerId;
}
