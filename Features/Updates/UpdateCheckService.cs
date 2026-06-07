using Microsoft.Extensions.Hosting;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Updates;

// Periodic background update check: once shortly after startup, then daily. Each
// tick is gated on the user's AutoUpdateCheckEnabled setting (so toggling it off
// stops checks, and toggling it on resumes them — Settings also kicks an immediate
// check on enable). Once an update is pending it stops re-checking; the banner and
// toast are already up. No-ops entirely in a non-installed (dev / portable) build.
public sealed class UpdateCheckService(UpdateService updates, AppSettings settings) : BackgroundService
{
    private static readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!updates.IsSupported) return; // dev / portable build — nothing to do

        try
        {
            // Let startup settle (connections, feed warm-up) before hitting the network.
            await Task.Delay(_startupDelay, stoppingToken);

            await MaybeCheckAsync();

            var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await MaybeCheckAsync();
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task MaybeCheckAsync()
    {
        if (!settings.AutoUpdateCheckEnabled) return; // user opted out
        if (updates.IsUpdatePending) return;          // already found — banner/toast up
        await updates.CheckAsync();
    }
}
