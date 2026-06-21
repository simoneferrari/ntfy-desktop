using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Turns a parsed pack into human-readable lines for the review step.</summary>
public static class PackSummarizer
{
    public static IReadOnlyList<string> Summarize(RulePack pack)
    {
        var lines = new List<string>();

        foreach (var r in pack.MatchRules)
            lines.Add(IsEmpty(r.When)
                ? "⚠ Suppress EVERY message — this rule has no conditions and will hide everything (likely a mistake)."
                : $"Suppress messages where {Describe(r.When)} (no toast, hidden from feed).");

        foreach (var r in pack.CorrelateRules)
        {
            var warn = SameMatcher(r.Open, r.Close)
                ? "⚠ Open and close match the same messages — this can never pair. "
                : string.Empty;
            lines.Add($"{warn}Pair: open when {Describe(r.Open)}, close when {Describe(r.Close)}, " +
                      $"matched by the key from the {r.Key.From.ToString().ToLowerInvariant()}; " +
                      "both toast, then fold out of the feed.");
        }

        foreach (var r in pack.ExpectRules)
            lines.Add($"Alert ({r.OnAbsence.Title}) if no message where {Describe(r.When)} " +
                      $"arrives within {(int)r.Every.TotalHours}h (+{(int)r.Grace.TotalMinutes}m grace)" +
                      (r.OnRecovery is null ? "." : "; notify on recovery."));

        return lines;
    }

    private static bool IsEmpty(Matcher m) =>
        m.Topic is null && m.MinPriority is null && m.TitleRegex is null &&
        m.BodyRegex is null && m.Tag is null;

    // Field-wise comparison (avoids relying on record equality, since Matcher caches
    // compiled regexes in mutable fields).
    private static bool SameMatcher(Matcher a, Matcher b) =>
        a.Topic == b.Topic && a.MinPriority == b.MinPriority && a.TitleRegex == b.TitleRegex &&
        a.BodyRegex == b.BodyRegex && a.Tag == b.Tag;

    private static string Describe(Matcher m)
    {
        var parts = new List<string>();
        if (m.Topic is not null) parts.Add($"topic = {m.Topic}");
        if (m.MinPriority is { } p) parts.Add($"priority ≥ {p}");
        if (m.TitleRegex is not null) parts.Add($"title ~ /{m.TitleRegex}/");
        if (m.BodyRegex is not null) parts.Add($"body ~ /{m.BodyRegex}/");
        if (m.Tag is not null) parts.Add($"tagged '{m.Tag}'");
        return parts.Count == 0 ? "any message" : string.Join(" and ", parts);
    }
}
