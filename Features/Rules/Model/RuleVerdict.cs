namespace NtfyDesktop.Features.Rules.Model;

/// <summary>An incident to record as open (pending side-effect after the row is
/// confirmed new).</summary>
public sealed record IncidentOpen(string RuleId, string Key, string MessageId, long OpenedAt);

/// <summary>
/// The engine's decision for one message. Toast-suppression and feed-hiding are
/// separate axes: a <c>match</c> suppress rule sets both (the message is pure noise);
/// a correlated <i>resolution</i> sets only <see cref="HideFromFeed"/> (it still toasts
/// live, but folds out of the feed).
///
/// <see cref="OpenIncident"/> / <see cref="CloseIncident"/> are incident-store writes
/// the caller applies only once the message is confirmed new. <see cref="DismissMessageId"/>
/// is the id of an earlier message (the problem) to retroactively hide from the feed
/// because its resolution just arrived.
/// </summary>
public sealed record RuleVerdict
{
    public bool SuppressToast { get; init; }
    public bool HideFromFeed { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IncidentOpen? OpenIncident { get; init; }
    public (string RuleId, string Key)? CloseIncident { get; init; }
    public string? DismissMessageId { get; init; }

    public static readonly RuleVerdict PassThrough = new();
}
