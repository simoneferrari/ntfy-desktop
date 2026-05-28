using System.IO;
using System.IO.Pipes;

namespace NtfyDesktop.Features.Shell;

/// <summary>
/// Named-pipe channel used to forward a toast/protocol activation from a freshly-launched
/// second instance to the already-running instance. The pipe name is hashed from the
/// data folder, matching the single-instance mutex — different --data-path profiles get
/// independent pipes and never collide.
/// </summary>
public sealed class SingleInstanceServer(string pipeName) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on a background thread whenever a forwarded activation arrives.
    /// Handlers should marshal to the UI thread.</summary>
    public event EventHandler<string>? ActivationReceived;

    public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // PipeOptions.Asynchronous so WaitForConnectionAsync actually awaits
                // on the I/O port instead of pinning a thread.
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(pipe);
                var payload = await reader.ReadToEndAsync(ct);

                if (!string.IsNullOrWhiteSpace(payload))
                    ActivationReceived?.Invoke(this, payload);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Don't crash the listener loop on a malformed/aborted client.
                // Brief pause to avoid tight error spin if something pathological happens.
                try { await Task.Delay(200, ct); } catch { break; }
            }
        }
    }

    /// <summary>
    /// Called by a second instance to forward an activation URL to the running one.
    /// Returns false if no server was listening within the timeout — caller decides
    /// whether to retry or drop. Short timeout (1s) so the second exe exits quickly.
    /// </summary>
    public static bool TryForward(string pipeName, string payload)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(1000);

            using var writer = new StreamWriter(pipe);
            writer.Write(payload);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
