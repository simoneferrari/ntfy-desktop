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
        var packEnabled = !root.TryGetProperty("enabled", out var pe) || pe.ValueKind != JsonValueKind.False;

        var matchRules = new List<MatchRule>();
        var correlateRules = new List<CorrelateRule>();
        var expectRules = new List<ExpectRule>();

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
                            ParseActions(rule, "do"))
                            { Id = RuleIdentity(rule, name, index), Enabled = RuleEnabled(rule) });
                        break;
                    case "correlate":
                        // Folding behaviour is intrinsic; any "onClose" in the JSON is
                        // ignored (tolerated for forward/backward compatibility).
                        correlateRules.Add(new CorrelateRule(
                            Id: RuleIdentity(rule, name, index),
                            Open: ParseMatcher(rule, "open"),
                            Close: ParseMatcher(rule, "close"),
                            Key: ParseKey(rule))
                            { Enabled = RuleEnabled(rule) });
                        break;
                    case "expect":
                        if (TryParseExpect(rule, name, index) is { } expect)
                            expectRules.Add(expect);
                        break;
                    // unknown type → skip
                }
                index++;
            }
        }

        return new RulePack(name, matchRules, correlateRules, expectRules) { Enabled = packEnabled };
    }

    private static bool RuleEnabled(JsonElement rule) =>
        !rule.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;

    private static string RuleIdentity(JsonElement rule, string packName, int index) =>
        rule.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s
            ? s : $"{packName}#{index}";

    private static ExpectRule? TryParseExpect(JsonElement rule, string packName, int index)
    {
        // every is required and must be a valid duration; onAbsence is required.
        if (!Duration.TryParse(Str(rule, "every"), out var every)) return null;
        var onAbsence = ParseAlert(rule, "onAbsence");
        if (onAbsence is null) return null;

        Duration.TryParse(Str(rule, "grace"), out var grace); // absent/invalid → Zero

        return new ExpectRule(
            Id: RuleIdentity(rule, packName, index),
            When: ParseMatcher(rule, "when"),
            Every: every,
            Grace: grace,
            OnAbsence: onAbsence,
            OnRecovery: ParseAlert(rule, "onRecovery"))
            { Enabled = RuleEnabled(rule) };
    }

    private static AlertSpec? ParseAlert(JsonElement rule, string property)
    {
        if (!rule.TryGetProperty(property, out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        var title = Str(a, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        return new AlertSpec(ParsePriority(Str(a, "priority")) ?? Priority.High, title, Str(a, "message"));
    }

    private static Matcher ParseMatcher(JsonElement rule, string property)
    {
        // Prefer the named matcher object (when/open/close). If it's absent, fall back to the
        // rule object itself — weaker models often put matcher fields at the rule's top level
        // (e.g. "titleRegex" beside "type"); reading them there beats producing a match-all.
        var m = rule.TryGetProperty(property, out var obj) && obj.ValueKind == JsonValueKind.Object
            ? obj : rule;

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

        var regex = Str(k, "regex");
        var from = string.Equals(Str(k, "from"), "title", StringComparison.OrdinalIgnoreCase)
            ? KeyField.Title : KeyField.Body;

        // Tolerate the shorthand { "body": "<regex>" } / { "title": "<regex>" } that weaker
        // models emit instead of { "from": ..., "regex": ... }.
        if (string.IsNullOrEmpty(regex))
        {
            if (Str(k, "body") is { Length: > 0 } b) { regex = b; from = KeyField.Body; }
            else if (Str(k, "title") is { Length: > 0 } t) { regex = t; from = KeyField.Title; }
        }

        return new KeySelector { From = from, Regex = regex ?? string.Empty };
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
