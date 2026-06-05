using Microsoft.Extensions.Hosting;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Feed;

// Periodic sweep of the on-disk attachment image cache, mirroring HistoryRetentionService:
// once at startup, then hourly. Stale files age out using the same retention window as
// message history. Lives in the Feed feature (next to AttachmentImageService) so the
// History feature doesn't have to depend on Feed.
public sealed class AttachmentCacheSweepService(AttachmentImageService images, AppSettings settings) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        images.SweepStale(settings.HistoryRetentionDays);

        var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                images.SweepStale(settings.HistoryRetentionDays);
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }
}
