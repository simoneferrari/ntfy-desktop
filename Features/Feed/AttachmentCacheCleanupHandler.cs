using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.History.Events;

namespace NtfyDesktop.Features.Feed;

/// <summary>
/// Purges cached attachment files when their messages are deleted (feed delete/clear, topic or
/// server removal with history, retention sweep), so a deleted message doesn't leave its files
/// behind. The deleted rows' attachment URLs ride along on <see cref="MessagesDeleted"/> — they
/// can't be looked up afterwards since the rows are already gone. Auto-discovered via the
/// messaging DI scan.
/// </summary>
public sealed class AttachmentCacheCleanupHandler(AttachmentImageService images)
    : IEventHandler<MessagesDeleted>
{
    public Task HandleAsync(MessagesDeleted ev, CancellationToken ct)
    {
        if (ev.AttachmentUrls is { Count: > 0 } urls)
            images.RemoveFromCache(urls);
        return Task.CompletedTask;
    }
}
