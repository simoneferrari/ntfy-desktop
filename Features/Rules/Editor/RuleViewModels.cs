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

/// <summary>How the correlation key is specified: a guided builder (label + id type) or a raw regex.</summary>
public enum KeyMode { Simple, Regex }

/// <summary>What the shared id looks like, for the guided key builder.</summary>
public enum KeyIdType { Number, Word, Anything }

public sealed partial class CorrelateRuleViewModel : RuleViewModel
{
    public override string Kind => "Correlate";
    public MatcherViewModel Open { get; init; } = new();
    public MatcherViewModel Close { get; init; } = new();
    [ObservableProperty] private KeyField _keyFrom = KeyField.Body;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSimpleKey))]
    [NotifyPropertyChangedFor(nameof(IsRegexKey))]
    private KeyMode _keyMode = KeyMode.Simple;

    // Guided (Simple) key: the text that precedes the id, and the id's shape.
    [ObservableProperty] private string _keyLabel = "";
    [ObservableProperty] private KeyIdType _keyIdType = KeyIdType.Number;

    // Raw (Regex) key.
    [ObservableProperty] private string _keyRegex = "";

    public bool IsSimpleKey => KeyMode == KeyMode.Simple;
    public bool IsRegexKey => KeyMode == KeyMode.Regex;

    public static IReadOnlyList<KeyField> KeyFields { get; } = [KeyField.Title, KeyField.Body];
    public static IReadOnlyList<KeyMode> KeyModes { get; } = [KeyMode.Simple, KeyMode.Regex];
    public static IReadOnlyList<KeyIdType> KeyIdTypes { get; } = [KeyIdType.Number, KeyIdType.Word, KeyIdType.Anything];

    public static CorrelateRuleViewModel FromModel(CorrelateRule c) => new()
    {
        Id = c.Id, Enabled = c.Enabled,
        Open = MatcherViewModel.FromModel(c.Open), Close = MatcherViewModel.FromModel(c.Close),
        KeyFrom = c.Key.From,
        // A loaded key is shown as raw regex (we can't reliably reverse the builder).
        KeyMode = KeyMode.Regex, KeyRegex = c.Key.Regex,
    };

    public CorrelateRule ToModel() => new(
        Id, Open.ToModel(), Close.ToModel(),
        new KeySelector { From = KeyFrom, Regex = EffectiveKeyRegex() }) { Enabled = Enabled };

    /// <summary>The regex actually used — built from the guided fields in Simple mode,
    /// or the raw pattern in Regex mode. Always contains a (?&lt;key&gt;…) capture in Simple mode.</summary>
    public string EffectiveKeyRegex()
    {
        if (KeyMode == KeyMode.Regex) return KeyRegex.Trim();
        var id = KeyIdType switch
        {
            KeyIdType.Word => @"\S+",
            KeyIdType.Anything => ".+",
            _ => @"\d+",
        };
        var label = KeyLabel.Trim();
        return string.IsNullOrEmpty(label)
            ? $"(?<key>{id})"
            : $@"{System.Text.RegularExpressions.Regex.Escape(label)}\s*(?<key>{id})";
    }

    public override bool TryValidate(out string error)
    {
        if (!Open.TryValidate(out error) || !Close.TryValidate(out error)) return false;
        var rx = EffectiveKeyRegex();
        if (string.IsNullOrWhiteSpace(rx))
        { error = "A correlate rule needs a correlation key (set the id text or a regex)."; return false; }
        try { _ = new System.Text.RegularExpressions.Regex(rx); }
        catch (Exception ex) { error = $"Key regex is invalid: {ex.Message}"; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [], [ToModel()], [])).FirstOrDefault() ?? "Correlate rule";
}

/// <summary>Unit for the expect-rule duration pickers — keeps "every"/"grace" valid by
/// construction (a number + unit instead of free text).</summary>
public enum DurationUnit { Minutes, Hours, Days }

public sealed partial class ExpectRuleViewModel : RuleViewModel
{
    public override string Kind => "Expect";
    public MatcherViewModel When { get; init; } = new();
    [ObservableProperty] private int _everyAmount = 24;
    [ObservableProperty] private DurationUnit _everyUnit = DurationUnit.Hours;
    [ObservableProperty] private int _graceAmount = 1;
    [ObservableProperty] private DurationUnit _graceUnit = DurationUnit.Hours;
    [ObservableProperty] private Priority _absencePriority = Priority.High;
    [ObservableProperty] private string _absenceTitle = "";
    [ObservableProperty] private string _absenceMessage = "";
    [ObservableProperty] private bool _hasRecovery;
    [ObservableProperty] private Priority _recoveryPriority = Priority.Default;
    [ObservableProperty] private string _recoveryTitle = "";

    public static IReadOnlyList<DurationUnit> DurationUnits { get; } =
        [DurationUnit.Minutes, DurationUnit.Hours, DurationUnit.Days];

    public static ExpectRuleViewModel FromModel(ExpectRule e)
    {
        var (ea, eu) = Decompose(e.Every);
        var (ga, gu) = Decompose(e.Grace);
        return new()
        {
            Id = e.Id, Enabled = e.Enabled, When = MatcherViewModel.FromModel(e.When),
            EveryAmount = ea, EveryUnit = eu, GraceAmount = ga, GraceUnit = gu,
            AbsencePriority = e.OnAbsence.Priority, AbsenceTitle = e.OnAbsence.Title, AbsenceMessage = e.OnAbsence.Message ?? "",
            HasRecovery = e.OnRecovery is not null,
            RecoveryPriority = e.OnRecovery?.Priority ?? Priority.Default, RecoveryTitle = e.OnRecovery?.Title ?? "",
        };
    }

    public ExpectRule ToModel()
    {
        var recovery = HasRecovery && !string.IsNullOrWhiteSpace(RecoveryTitle)
            ? new AlertSpec(RecoveryPriority, RecoveryTitle.Trim(), null) : null;
        return new ExpectRule(Id, When.ToModel(), ToSpan(EveryAmount, EveryUnit), ToSpan(GraceAmount, GraceUnit),
            new AlertSpec(AbsencePriority, AbsenceTitle.Trim(), string.IsNullOrWhiteSpace(AbsenceMessage) ? null : AbsenceMessage.Trim()),
            recovery) { Enabled = Enabled };
    }

    public override bool TryValidate(out string error)
    {
        if (!When.TryValidate(out error)) return false;
        if (EveryAmount <= 0) { error = "‘Every’ must be greater than zero."; return false; }
        if (GraceAmount < 0) { error = "‘Grace’ can’t be negative."; return false; }
        if (string.IsNullOrWhiteSpace(AbsenceTitle)) { error = "An expect rule needs an absence-alert title."; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [], [], [ToModel()])).FirstOrDefault() ?? "Expect rule";

    private static TimeSpan ToSpan(int amount, DurationUnit unit) => unit switch
    {
        DurationUnit.Minutes => TimeSpan.FromMinutes(amount),
        DurationUnit.Days => TimeSpan.FromDays(amount),
        _ => TimeSpan.FromHours(amount),
    };

    private static (int Amount, DurationUnit Unit) Decompose(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return (0, DurationUnit.Hours);
        if (t.TotalDays >= 1 && t.TotalDays == Math.Floor(t.TotalDays)) return ((int)t.TotalDays, DurationUnit.Days);
        if (t.TotalHours >= 1 && t.TotalHours == Math.Floor(t.TotalHours)) return ((int)t.TotalHours, DurationUnit.Hours);
        return ((int)t.TotalMinutes, DurationUnit.Minutes);
    }
}
