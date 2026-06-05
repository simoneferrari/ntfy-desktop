using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Connections;

// TopicId identifies which configured topic (and therefore which server) the
// message arrived on — needed downstream because topic names are no longer unique
// across servers.
public record NtfyMessageReceived(NtfyMessage Message, Guid TopicId) : IEvent;
