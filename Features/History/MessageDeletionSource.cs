namespace NtfyDesktop.Features.History;

// Who triggered a message deletion. Lets consumers ignore deletes they originated
// themselves — notably the feed, which removes its own rows locally and only wants
// to react to deletions from elsewhere.
public enum MessageDeletionSource
{
    Feed,       // the feed UI's own Clear / delete-message
    Removal,    // a topic or server (with its history) was removed
    Retention,  // the automatic retention sweep
}
