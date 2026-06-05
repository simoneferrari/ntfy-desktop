using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Settings.Events;

// A server was removed, cascade-removing its topics. RemovedTopicIds is captured at
// removal time because consumers handle this after the topics are already gone from
// AppSettings, so they can't look them up. Consumers drop those topics' rail/list
// items and refresh server-label visibility (the server count changed).
public record ServerDeleted(Guid ServerId, IReadOnlyList<Guid> RemovedTopicIds) : IEvent;
