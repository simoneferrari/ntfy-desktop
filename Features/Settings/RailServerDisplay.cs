namespace NtfyDesktop.Features.Settings;

/// <summary>
/// How the nav rail shows which server a topic belongs to. Only has a visible
/// effect when more than one server is configured.
/// </summary>
public enum RailServerDisplay
{
    /// <summary>Topics grouped under a server header.</summary>
    Grouped,

    /// <summary>Server name shown as small secondary text under each topic.</summary>
    Subtitle,

    /// <summary>No server context in the rail.</summary>
    None,
}
