namespace NtfyDesktop.Features.Rules.Model;

/// <summary>A straight pattern → actions rule.</summary>
public sealed record MatchRule(Matcher When, IReadOnlyList<RuleAction> Actions);

/// <summary>
/// Pairs an opening message with its resolving message via an extracted key.
/// When a close matches an open incident, the folding behaviour is intrinsic
/// (resolution toasts but is hidden from the feed; the original problem is hidden
/// too) — there are no per-rule close actions. <see cref="Id"/> namespaces incidents
/// in the store (pack name + index).
/// </summary>
public sealed record CorrelateRule(
    string Id,
    Matcher Open,
    Matcher Close,
    KeySelector Key);

public sealed record RulePack(
    string Name,
    IReadOnlyList<MatchRule> MatchRules,
    IReadOnlyList<CorrelateRule> CorrelateRules,
    IReadOnlyList<ExpectRule> ExpectRules);
