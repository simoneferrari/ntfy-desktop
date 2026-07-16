using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

/// <summary>Starter rules pre-filled with sensible defaults + example text, so a user can
/// tweak rather than start from a blank form. Each gets a fresh stable id.</summary>
public static class RuleTemplates
{
    public sealed record Template(string Key, string Label, string Description);

    public static IReadOnlyList<Template> All { get; } =
    [
        new("suppress-success", "Suppress noisy successes",
            "Hide routine “success/OK” messages (no toast, hidden from feed)."),
        new("pair-problem-resolved", "Pair problem ↔ resolved",
            "Fold a PROBLEM and its matching RESOLVED out of the feed once paired."),
        new("heartbeat", "Heartbeat (alert if it stops)",
            "Alert when an expected recurring message stops arriving."),
    ];

    public static RuleViewModel Create(string key) => key switch
    {
        "pair-problem-resolved" => new CorrelateRuleViewModel
        {
            Id = RuleId.NewId(),
            Open = { TitleMode = MatchMode.Contains, TitleRegex = "PROBLEM" },
            Close = { TitleMode = MatchMode.Contains, TitleRegex = "RESOLVED" },
            KeyFrom = KeyField.Body,
            KeyMode = KeyMode.Simple, KeyLabel = "ID:", KeyIdType = KeyIdType.Number,
        },
        "heartbeat" => new ExpectRuleViewModel
        {
            Id = RuleId.NewId(),
            When = { TitleMode = MatchMode.Contains, TitleRegex = "success" },
            EveryAmount = 24, EveryUnit = DurationUnit.Hours,
            GraceAmount = 1, GraceUnit = DurationUnit.Hours,
            AbsencePriority = Priority.High, AbsenceTitle = "Heartbeat missed",
        },
        _ => new MatchRuleViewModel // "suppress-success"
        {
            Id = RuleId.NewId(),
            When = { TitleMode = MatchMode.Contains, TitleRegex = "success" },
            SuppressToast = true,
        },
    };
}
