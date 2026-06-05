using NtfyDesktop.Core.Messaging;

namespace NtfyDesktop.Features.Settings.Events;

// Raised when anything affecting how servers are shown changes: a server rename
// (DisplayLabel), the show-server-label toggle, or a server added/removed (which
// changes whether the server is worth showing at all).
public record ServerDisplayChanged : IEvent;
