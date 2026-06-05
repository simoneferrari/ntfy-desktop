using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.History.Events;

// TopicId: null = broad/unscoped deletion (all, retention, single row); a value =
// that topic's messages were removed wholesale.
// Source: who triggered it, so consumers (the feed) can ignore their own deletes.
public record MessagesDeleted(Guid? TopicId, MessageDeletionSource Source) : IEvent;
