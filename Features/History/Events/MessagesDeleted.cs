using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.History.Events;

// TopicId: null = broad/unscoped deletion (all, retention, single row); a value =
// that topic's messages were removed wholesale.
// Source: who triggered it, so consumers (the feed) can ignore their own deletes.
// AttachmentUrls: the attachment URLs of the deleted rows, so the attachment cache can
// drop their files (collected before the rows are gone).
public record MessagesDeleted(
    Guid? TopicId,
    MessageDeletionSource Source,
    IReadOnlyList<string>? AttachmentUrls = null) : IEvent;
