using System.Text.Json;
using System.Text.Json.Nodes;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

/// <summary>Serialises a pack back to canonical JSON (the inverse of <see cref="PackParser"/>).
/// Emits only engine-supported fields; behaviourally-inert constructs are not preserved.</summary>
public static class PackWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Write(RulePack pack)
    {
        var rules = new JsonArray();

        foreach (var r in pack.MatchRules)
        {
            var o = new JsonObject { ["type"] = "match", ["id"] = r.Id, ["enabled"] = r.Enabled };
            o["when"] = Matcher(r.When);
            var actions = new JsonArray();
            foreach (var a in r.Actions)
            {
                if (a.Kind == RuleActionKind.SuppressToast) actions.Add("suppressToast");
                else if (a.Kind == RuleActionKind.Tag && !string.IsNullOrEmpty(a.Value)) actions.Add($"tag:{a.Value}");
            }
            o["do"] = actions;
            rules.Add(o);
        }

        foreach (var r in pack.CorrelateRules)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "correlate", ["id"] = r.Id, ["enabled"] = r.Enabled,
                ["open"] = Matcher(r.Open), ["close"] = Matcher(r.Close),
                ["key"] = new JsonObject
                {
                    ["from"] = r.Key.From == KeyField.Title ? "title" : "body",
                    ["regex"] = r.Key.Regex,
                },
            });
        }

        foreach (var r in pack.ExpectRules)
        {
            var o = new JsonObject
            {
                ["type"] = "expect", ["id"] = r.Id, ["enabled"] = r.Enabled,
                ["when"] = Matcher(r.When),
                ["every"] = WriteDuration(r.Every),
                ["grace"] = WriteDuration(r.Grace),
                ["onAbsence"] = Alert(r.OnAbsence),
            };
            if (r.OnRecovery is { } rec) o["onRecovery"] = Alert(rec);
            rules.Add(o);
        }

        var root = new JsonObject { ["name"] = pack.Name, ["enabled"] = pack.Enabled, ["rules"] = rules };
        return root.ToJsonString(Indented);
    }

    private static JsonObject Matcher(Matcher m)
    {
        var o = new JsonObject();
        if (m.Topic is not null) o["topic"] = m.Topic;
        if (m.MinPriority is { } p) o["minPriority"] = p.ToString().ToLowerInvariant();
        if (m.TitleRegex is not null) o["titleRegex"] = m.TitleRegex;
        if (m.BodyRegex is not null) o["bodyRegex"] = m.BodyRegex;
        if (m.Tag is not null) o["tag"] = m.Tag;
        return o;
    }

    private static JsonObject Alert(AlertSpec a)
    {
        var o = new JsonObject { ["priority"] = a.Priority.ToString().ToLowerInvariant(), ["title"] = a.Title };
        if (a.Message is not null) o["message"] = a.Message;
        return o;
    }

    // Whole units where possible (26h, 90m, 2d, 45s), else fall back to seconds.
    private static string WriteDuration(TimeSpan t)
    {
        if (t.TotalSeconds <= 0) return "0s";
        if (t.TotalDays == Math.Floor(t.TotalDays)) return $"{(int)t.TotalDays}d";
        if (t.TotalHours == Math.Floor(t.TotalHours)) return $"{(int)t.TotalHours}h";
        if (t.TotalMinutes == Math.Floor(t.TotalMinutes)) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalSeconds}s";
    }
}
