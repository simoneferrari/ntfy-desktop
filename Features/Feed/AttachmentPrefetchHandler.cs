using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.History.Events;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Feed;

/// <summary>
/// When auto-download is enabled, fetches a newly-arrived message's attachment to the local
/// cache immediately — before the ntfy server expires it — instead of waiting for the user to
/// open it (or, for images, to view the feed). Fires for every genuinely-new stored message
/// (<see cref="MessageInserted"/> is published only for new rows), independent of which feed is
/// open. Auto-discovered and registered via the messaging DI scan.
/// </summary>
public sealed class AttachmentPrefetchHandler(AttachmentImageService images, AppSettings settings)
    : IEventHandler<MessageInserted>
{
    public Task HandleAsync(MessageInserted ev, CancellationToken ct)
    {
        if (!settings.AutoDownloadAttachments) return Task.CompletedTask;

        var attachment = ev.Message.Attachment;
        if (attachment is null || !SafeUrl.IsAllowed(attachment.Url)) return Task.CompletedTask;

        var capBytes = (long)Math.Max(1, settings.AutoDownloadMaxFileMb) * 1024 * 1024;

        // Skip when the server-reported size already exceeds the cap; unknown sizes are still
        // bounded by the same cap passed into the download. Manual open ignores this cap.
        if (attachment.Size is { } size && size > capBytes) return Task.CompletedTask;

        // Fire-and-forget: the prefetch shouldn't hold up the publish pipeline. The service
        // caps concurrency and swallows failures, and CancellationToken.None decouples it from
        // the publish scope's lifetime.
        _ = images.EnsureFileAsync(attachment, ev.Message.TopicId, CancellationToken.None, capBytes);
        return Task.CompletedTask;
    }
}
