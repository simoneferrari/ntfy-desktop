using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class MatcherViewModel : ObservableObject
{
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private Priority? _minPriority;
    [ObservableProperty] private string _titleRegex = "";
    [ObservableProperty] private string _bodyRegex = "";
    [ObservableProperty] private string _tag = "";

    /// <summary>Selectable priorities for the min-priority combo (null = any).</summary>
    public static IReadOnlyList<Priority?> PriorityChoices { get; } =
        [null, Priority.Min, Priority.Low, Priority.Default, Priority.High, Priority.Urgent];

    public static MatcherViewModel FromModel(Matcher m) => new()
    {
        Topic = m.Topic ?? "", MinPriority = m.MinPriority,
        TitleRegex = m.TitleRegex ?? "", BodyRegex = m.BodyRegex ?? "", Tag = m.Tag ?? "",
    };

    public Matcher ToModel() => new()
    {
        Topic = Nullify(Topic), MinPriority = MinPriority,
        TitleRegex = Nullify(TitleRegex), BodyRegex = Nullify(BodyRegex), Tag = Nullify(Tag),
    };

    public bool TryValidate(out string error)
    {
        foreach (var (label, pattern) in new[] { ("Title", TitleRegex), ("Body", BodyRegex) })
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            try { _ = Regex.Match("", pattern); }
            catch (Exception ex) { error = $"{label} regex is invalid: {ex.Message}"; return false; }
        }
        error = ""; return true;
    }

    private static string? Nullify(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
