using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.History.Events;

// A message that was already visible has been retroactively hidden from the feed
// (its suppressed flag was set after the fact — e.g. a problem folded by its
// resolution). Consumers drop the row from the default feed view and re-seed the
// unread count. Distinct from MessagesDeleted: the row still exists, just hidden.
public record MessageSuppressed(Guid TopicId, string MessageId) : IEvent;
