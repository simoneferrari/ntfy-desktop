using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Ai;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public abstract partial class RuleViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private bool _enabled = true;
    public abstract string Kind { get; }
    public abstract string Summary { get; }
    public abstract bool TryValidate(out string error);

    /// <summary>Selectable priorities for alert combos.</summary>
    public static IReadOnlyList<Priority> AlertPriorities { get; } =
        [Priority.Min, Priority.Low, Priority.Default, Priority.High, Priority.Urgent];
}

public sealed partial class MatchRuleViewModel : RuleViewModel
{
    public override string Kind => "Match";
    public MatcherViewModel When { get; init; } = new();
    [ObservableProperty] private bool _suppressToast = true;
    [ObservableProperty] private string _tagValue = "";

    public static MatchRuleViewModel FromModel(MatchRule m) => new()
    {
        Id = m.Id, Enabled = m.Enabled, When = MatcherViewModel.FromModel(m.When),
        SuppressToast = m.Actions.Any(a => a.Kind == RuleActionKind.SuppressToast),
        TagValue = m.Actions.FirstOrDefault(a => a.Kind == RuleActionKind.Tag)?.Value ?? "",
    };

    public MatchRule ToModel()
    {
        var actions = new List<RuleAction>();
        if (SuppressToast) actions.Add(new RuleAction(RuleActionKind.SuppressToast));
        if (!string.IsNullOrWhiteSpace(TagValue)) actions.Add(new RuleAction(RuleActionKind.Tag, TagValue.Trim()));
        return new MatchRule(When.ToModel(), actions) { Id = Id, Enabled = Enabled };
    }

    public override bool TryValidate(out string error)
    {
        if (!When.TryValidate(out error)) return false;
        if (!SuppressToast && string.IsNullOrWhiteSpace(TagValue))
        { error = "A match rule must suppress the toast or add a tag."; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [ToModelSafe()], [], [])).FirstOrDefault() ?? "Match rule";

    private MatchRule ToModelSafe() => new(When.ToModel(),
        SuppressToast ? [new RuleAction(RuleActionKind.SuppressToast)] : []) { Id = Id, Enabled = Enabled };
}

public sealed partial class CorrelateRuleViewModel : RuleViewModel
{
    public override string Kind => "Correlate";
    public MatcherViewModel Open { get; init; } = new();
    public MatcherViewModel Close { get; init; } = new();
    [ObservableProperty] private KeyField _keyFrom = KeyField.Body;
    [ObservableProperty] private string _keyRegex = "";

    public static CorrelateRuleViewModel FromModel(CorrelateRule c) => new()
    {
        Id = c.Id, Enabled = c.Enabled,
        Open = MatcherViewModel.FromModel(c.Open), Close = MatcherViewModel.FromModel(c.Close),
        KeyFrom = c.Key.From, KeyRegex = c.Key.Regex,
    };

    public CorrelateRule ToModel() => new(
        Id, Open.ToModel(), Close.ToModel(),
        new KeySelector { From = KeyFrom, Regex = KeyRegex.Trim() }) { Enabled = Enabled };

    public override bool TryValidate(out string error)
    {
        if (!Open.TryValidate(out error) || !Close.TryValidate(out error)) return false;
        if (string.IsNullOrWhiteSpace(KeyRegex))
        { error = @"A correlate rule needs a key regex (e.g. ID: (?<key>\d+))."; return false; }
        try { _ = new System.Text.RegularExpressions.Regex(KeyRegex); }
        catch (Exception ex) { error = $"Key regex is invalid: {ex.Message}"; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [], [ToModel()], [])).FirstOrDefault() ?? "Correlate rule";
}

public sealed partial class ExpectRuleViewModel : RuleViewModel
{
    public override string Kind => "Expect";
    public MatcherViewModel When { get; init; } = new();
    [ObservableProperty] private string _every = "24h";
    [ObservableProperty] private string _grace = "1h";
    [ObservableProperty] private Priority _absencePriority = Priority.High;
    [ObservableProperty] private string _absenceTitle = "";
    [ObservableProperty] private string _absenceMessage = "";
    [ObservableProperty] private bool _hasRecovery;
    [ObservableProperty] private Priority _recoveryPriority = Priority.Default;
    [ObservableProperty] private string _recoveryTitle = "";

    public static ExpectRuleViewModel FromModel(ExpectRule e) => new()
    {
        Id = e.Id, Enabled = e.Enabled, When = MatcherViewModel.FromModel(e.When),
        Every = Compact(e.Every), Grace = Compact(e.Grace),
        AbsencePriority = e.OnAbsence.Priority, AbsenceTitle = e.OnAbsence.Title, AbsenceMessage = e.OnAbsence.Message ?? "",
        HasRecovery = e.OnRecovery is not null,
        RecoveryPriority = e.OnRecovery?.Priority ?? Priority.Default, RecoveryTitle = e.OnRecovery?.Title ?? "",
    };

    public ExpectRule ToModel()
    {
        Duration.TryParse(Every, out var every);
        Duration.TryParse(Grace, out var grace);
        var recovery = HasRecovery && !string.IsNullOrWhiteSpace(RecoveryTitle)
            ? new AlertSpec(RecoveryPriority, RecoveryTitle.Trim(), null) : null;
        return new ExpectRule(Id, When.ToModel(), every, grace,
            new AlertSpec(AbsencePriority, AbsenceTitle.Trim(), string.IsNullOrWhiteSpace(AbsenceMessage) ? null : AbsenceMessage.Trim()),
            recovery) { Enabled = Enabled };
    }

    public override bool TryValidate(out string error)
    {
        if (!When.TryValidate(out error)) return false;
        if (!Duration.TryParse(Every, out _)) { error = "‘Every’ must be a duration like 26h, 90m, 2d."; return false; }
        if (!string.IsNullOrWhiteSpace(Grace) && !Duration.TryParse(Grace, out _)) { error = "‘Grace’ must be a duration like 1h."; return false; }
        if (string.IsNullOrWhiteSpace(AbsenceTitle)) { error = "An expect rule needs an absence-alert title."; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [], [], [ToModel()])).FirstOrDefault() ?? "Expect rule";

    private static string Compact(TimeSpan t) =>
        t.TotalDays == Math.Floor(t.TotalDays) && t.TotalDays >= 1 ? $"{(int)t.TotalDays}d"
        : t.TotalHours == Math.Floor(t.TotalHours) && t.TotalHours >= 1 ? $"{(int)t.TotalHours}h"
        : $"{(int)t.TotalMinutes}m";
}
