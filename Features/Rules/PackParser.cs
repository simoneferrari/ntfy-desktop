using System.Text.Json;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Parses a rule pack from JSON into the model. Unknown action strings and unknown
/// rule types are skipped (fail open) so a pack authored for a later phase doesn't
/// break loading. Throws only on JSON that isn't a valid pack object.
/// </summary>
public static class PackParser
{
    public static RulePack Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "pack" : "pack";

        var matchRules = new List<MatchRule>();
        var correlateRules = new List<CorrelateRule>();

        if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var rule in rules.EnumerateArray())
            {
                var type = rule.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "match":
                        matchRules.Add(new MatchRule(
                            ParseMatcher(rule, "when"),
                            ParseActions(rule, "do")));
                        break;
                    case "correlate":
                        correlateRules.Add(new CorrelateRule(
                            Id: $"{name}#{index}",
                            Open: ParseMatcher(rule, "open"),
                            Close: ParseMatcher(rule, "close"),
                            Key: ParseKey(rule),
                            OnClose: ParseActions(rule, "onClose")));
                        break;
                    // unknown type (e.g. "expect", phase 1b) → skip
                }
                index++;
            }
        }

        return new RulePack(name, matchRules, correlateRules);
    }

    private static Matcher ParseMatcher(JsonElement rule, string property)
    {
        if (!rule.TryGetProperty(property, out var m) || m.ValueKind != JsonValueKind.Object)
            return new Matcher();

        return new Matcher
        {
            Topic = Str(m, "topic"),
            MinPriority = ParsePriority(Str(m, "minPriority")),
            TitleRegex = Str(m, "titleRegex"),
            BodyRegex = Str(m, "bodyRegex"),
            Tag = Str(m, "tag"),
        };
    }

    private static KeySelector ParseKey(JsonElement rule)
    {
        if (!rule.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.Object)
            return new KeySelector();

        var from = string.Equals(Str(k, "from"), "title", StringComparison.OrdinalIgnoreCase)
            ? KeyField.Title : KeyField.Body;
        return new KeySelector { From = from, Regex = Str(k, "regex") ?? string.Empty };
    }

    private static IReadOnlyList<RuleAction> ParseActions(JsonElement rule, string property)
    {
        var actions = new List<RuleAction>();
        if (!rule.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return actions;

        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrEmpty(s)) continue;

            if (string.Equals(s, "suppressToast", StringComparison.OrdinalIgnoreCase))
                actions.Add(new RuleAction(RuleActionKind.SuppressToast));
            else if (s.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                actions.Add(new RuleAction(RuleActionKind.Tag, s["tag:".Length..]));
            // unknown action (digest, dismissOriginal, …) → skipped (phase 1a)
        }
        return actions;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Priority? ParsePriority(string? label) => label?.ToLowerInvariant() switch
    {
        "min" => Priority.Min,
        "low" => Priority.Low,
        "default" => Priority.Default,
        "high" => Priority.High,
        "urgent" or "max" => Priority.Urgent,
        _ => null,
    };
}
