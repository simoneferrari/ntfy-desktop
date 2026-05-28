namespace NtfyDesktop.Features.Connections;

// A snapshot of one topic's live socket state. Pure connection concerns:
// pause is a separate axis owned by Features.Notifications.NotificationGate and
// composed at the call site (Shell read models, Connections page rows).
//
// TopicId is the stable key; TopicName is the ntfy subscription; DisplayName is
// what the UI shows (friendly name, falling back to the topic name).
public sealed record TopicConnectionState(
    Guid TopicId,
    string TopicName,
    string DisplayName,
    string ServerName,
    TopicConnectionStatus Status,
    string? LastError);
