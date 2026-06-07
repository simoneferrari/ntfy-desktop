using System.Text.Json.Serialization;

namespace NtfyDesktop.Domain;

/// <summary>
/// A user action button attached to an ntfy message. Mirrors one element of the
/// top-level "actions" array in the subscribe JSON (a message may carry up to three).
/// The shape is a union over the action types: <c>view</c> opens <see cref="Url"/>,
/// <c>http</c> fires a request (<see cref="Url"/>/<see cref="Method"/>/<see cref="Headers"/>/
/// <see cref="Body"/>), <c>copy</c> copies <see cref="Value"/> to the clipboard, and
/// <c>broadcast</c> sends an Android Intent (not actionable on Windows).
/// </summary>
public sealed record NtfyAction
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Action type: <c>view</c>, <c>http</c>, <c>broadcast</c>, or <c>copy</c>.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>Button label shown to the user.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>Whether the originating notification should be cleared after the action.
    /// Only meaningful for a live toast — irrelevant in the feed, so unused here.</summary>
    [JsonPropertyName("clear")]
    public bool Clear { get; init; }

    /// <summary>Target URL for <c>view</c> and <c>http</c> actions.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }

    /// <summary>HTTP method for an <c>http</c> action (defaults to POST — see <see cref="EffectiveMethod"/>).</summary>
    [JsonPropertyName("method")]
    public string? Method { get; init; }

    /// <summary>Request headers for an <c>http</c> action.</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>Request body for an <c>http</c> action.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    /// <summary>Android Intent name for a <c>broadcast</c> action (not used on Windows).</summary>
    [JsonPropertyName("intent")]
    public string? Intent { get; init; }

    /// <summary>Android Intent extras for a <c>broadcast</c> action (not used on Windows).</summary>
    [JsonPropertyName("extras")]
    public Dictionary<string, string>? Extras { get; init; }

    /// <summary>Clipboard value for a <c>copy</c> action.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    private bool Is(string kind) => string.Equals(Action, kind, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore] public bool IsView => Is("view");
    [JsonIgnore] public bool IsHttp => Is("http");
    [JsonIgnore] public bool IsBroadcast => Is("broadcast");
    [JsonIgnore] public bool IsCopy => Is("copy");

    /// <summary>POST is ntfy's default when an <c>http</c> action omits the method.</summary>
    [JsonIgnore]
    public string EffectiveMethod =>
        string.IsNullOrWhiteSpace(Method) ? "POST" : Method.Trim().ToUpperInvariant();

    /// <summary>
    /// True when this app can actually perform the action: a <c>view</c>/<c>http</c> with an
    /// http(s) URL, or a <c>copy</c> with a value. <c>broadcast</c> (Android-only) and any
    /// unknown/unsafe action are not actionable — the button renders disabled.
    /// </summary>
    [JsonIgnore]
    public bool IsSupported =>
        ((IsView || IsHttp) && SafeUrl.IsAllowed(Url)) ||
        (IsCopy && !string.IsNullOrEmpty(Value));

    /// <summary>Tooltip explaining why a button is disabled, or null when it's actionable.</summary>
    [JsonIgnore]
    public string? DisabledReason
    {
        get
        {
            if (IsSupported) return null;
            if (IsBroadcast) return "Broadcast actions are Android-only.";
            if (IsView || IsHttp) return "This action has no valid http(s) URL.";
            if (IsCopy) return "This action has nothing to copy.";
            return "This action type isn't supported.";
        }
    }
}
