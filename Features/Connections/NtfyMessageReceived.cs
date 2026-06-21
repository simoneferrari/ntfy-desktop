using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Connections;

// TopicId identifies which configured topic (and therefore which server) the
// message arrived on — needed downstream because topic names are no longer unique
// across servers.
//
// IsBackfill marks a message that was replayed by the server (via the `since=`
// catch-up subscription) rather than published live while we were connected.
// History/feed/unread still process it; toast delivery is suppressed so a
// reconnect doesn't re-toast messages the user already missed-and-moved-on-from.
public record NtfyMessageReceived(NtfyMessage Message, Guid TopicId, bool IsBackfill = false, bool SuppressToast = false) : IEvent;
