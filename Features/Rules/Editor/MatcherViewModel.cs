using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

/// <summary>How a title/body text field is interpreted when building the regex the engine
/// uses. Contains/Equals let non-technical users type plain text (escaped behind the scenes);
/// Regex is the raw pattern for power users (and how loaded packs are shown).</summary>
public enum MatchMode { Contains, Equals, Regex }

public sealed partial class MatcherViewModel : ObservableObject
{
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private Priority? _minPriority;

    // TitleRegex/BodyRegex hold the user-entered *text*; the mode decides how it's compiled
    // into the stored regex (see Build). Kept under these names so existing tests/bindings hold.
    [ObservableProperty] private MatchMode _titleMode = MatchMode.Contains;
    [ObservableProperty] private string _titleRegex = "";
    [ObservableProperty] private MatchMode _bodyMode = MatchMode.Contains;
    [ObservableProperty] private string _bodyRegex = "";

    [ObservableProperty] private string _tag = "";

    /// <summary>Selectable priorities for the min-priority combo (null = any).</summary>
    public static IReadOnlyList<Priority?> PriorityChoices { get; } =
        [null, Priority.Min, Priority.Low, Priority.Default, Priority.High, Priority.Urgent];

    public static IReadOnlyList<MatchMode> MatchModes { get; } =
        [MatchMode.Contains, MatchMode.Equals, MatchMode.Regex];

    public static MatcherViewModel FromModel(Matcher m) => new()
    {
        Topic = m.Topic ?? "", MinPriority = m.MinPriority,
        // A loaded pattern is shown as raw regex — we can't reliably reverse an escape.
        TitleMode = MatchMode.Regex, TitleRegex = m.TitleRegex ?? "",
        BodyMode = MatchMode.Regex, BodyRegex = m.BodyRegex ?? "",
        Tag = m.Tag ?? "",
    };

    public Matcher ToModel() => new()
    {
        Topic = Nullify(Topic), MinPriority = MinPriority,
        TitleRegex = Build(TitleMode, TitleRegex), BodyRegex = Build(BodyMode, BodyRegex),
        Tag = Nullify(Tag),
    };

    public bool TryValidate(out string error)
    {
        foreach (var (label, mode, text) in new[] { ("Title", TitleMode, TitleRegex), ("Body", BodyMode, BodyRegex) })
        {
            if (mode != MatchMode.Regex || string.IsNullOrEmpty(text)) continue;
            try { _ = Regex.Match("", text); }
            catch (Exception ex) { error = $"{label} regex is invalid: {ex.Message}"; return false; }
        }
        error = ""; return true;
    }

    /// <summary>Compiles the user's text + mode into the stored regex pattern (null when empty).</summary>
    private static string? Build(MatchMode mode, string text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return mode switch
        {
            MatchMode.Contains => Regex.Escape(t),
            MatchMode.Equals => $"^{Regex.Escape(t)}$",
            _ => t, // Regex: raw
        };
    }

    private static string? Nullify(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
