using NtfyDesktop.Domain;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

public sealed record SimResult(
    HistoryMessage Message, bool Hidden, IReadOnlyList<string> Tags,
    string? DismissMessageId, bool OpensIncident);

public sealed record AbsenceWindow(string RuleTitle, DateTimeOffset Start, DateTimeOffset End, TimeSpan Gap);

public sealed record SimReport(IReadOnlyList<SimResult> Results, IReadOnlyList<AbsenceWindow> Absences);

/// <summary>Runs one pack over an ordered slice of stored history to show what it WOULD do.
/// Pure and read-only; uses its own <see cref="InMemoryIncidentStore"/>.</summary>
public static class PackHistorySimulator
{
    public static NtfyMessage ToNtfyMessage(HistoryMessage m) => new()
    {
        Id = m.MessageId,
        Time = m.Timestamp.ToUnixTimeSeconds(),
        Topic = m.Topic,
        Title = m.Title,
        Message = m.Body,
        Priority = m.Priority,
        Tags = string.IsNullOrEmpty(m.Tags) ? null : m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
    };

    public static SimReport Simulate(RulePack pack, IReadOnlyList<HistoryMessage> oldestFirst)
    {
        var incidents = new InMemoryIncidentStore();
        var single = new[] { pack };
        var results = new List<SimResult>();

        foreach (var hm in oldestFirst)
        {
            var v = RuleEngine.EvaluateAgainst(ToNtfyMessage(hm), single, incidents);
            // Apply pending incident writes to our in-memory store so later closes can pair.
            if (v.OpenIncident is { } o) incidents.Open(o.RuleId, o.Key, o.MessageId, o.OpenedAt);
            if (v.CloseIncident is { } c) incidents.Resolve(c.RuleId, c.Key);

            results.Add(new SimResult(hm, v.HideFromFeed, v.Tags, v.DismissMessageId, v.OpenIncident is not null));
        }

        return new SimReport(results, DetectAbsences(pack, oldestFirst));
    }

    private static List<AbsenceWindow> DetectAbsences(RulePack pack, IReadOnlyList<HistoryMessage> oldestFirst)
    {
        var windows = new List<AbsenceWindow>();
        foreach (var rule in pack.ExpectRules)
        {
            var threshold = rule.Every + rule.Grace;
            DateTimeOffset? prev = null;
            foreach (var hm in oldestFirst)
            {
                if (!rule.When.Matches(hm.Topic, hm.Title, hm.Body, hm.Priority,
                        string.IsNullOrEmpty(hm.Tags) ? null : hm.Tags.Split(','))) continue;

                if (prev is { } p && hm.Timestamp - p > threshold)
                    windows.Add(new AbsenceWindow(rule.OnAbsence.Title, p, hm.Timestamp, hm.Timestamp - p));
                prev = hm.Timestamp;
            }
        }
        return windows;
    }
}
