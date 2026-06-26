using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

public sealed record ApplyOutcome(int HiddenCount, int FoldedCount);

/// <summary>Previews a pack against stored history (read-only) and applies it as an
/// additive backfill (sets the Suppressed flag on already-stored rows). Apply never
/// clears suppression and never touches toasts; expect rules are skipped.</summary>
public sealed class RulePackHistoryService(HistoryRepository history, IIncidentStore incidents)
{
    public SimReport Preview(RulePack pack, Guid? topicId, int limit)
    {
        var msgs = Fetch(topicId, limit);
        return PackHistorySimulator.Simulate(pack, msgs);
    }

    public ApplyOutcome Apply(RulePack pack, Guid? topicId, int limit)
    {
        var msgs = Fetch(topicId, limit);
        var single = new[] { pack };
        int hidden = 0, folded = 0;

        foreach (var hm in msgs)
        {
            var v = RuleEngine.EvaluateAgainst(PackHistorySimulator.ToNtfyMessage(hm), single, incidents);
            // Persist incident pairing so future live messages correlate against it.
            if (v.OpenIncident is { } o) incidents.Open(o.RuleId, o.Key, o.MessageId, o.OpenedAt);
            if (v.CloseIncident is { } c) incidents.Resolve(c.RuleId, c.Key);

            if (v.HideFromFeed) { history.SuppressMessage(hm.MessageId); hidden++; }
            if (v.DismissMessageId is { } d) { history.SuppressMessage(d); folded++; }
        }
        return new ApplyOutcome(hidden, folded);
    }

    // Query returns newest-first; correlation/absence need oldest-first.
    private List<HistoryMessage> Fetch(Guid? topicId, int limit)
    {
        var msgs = history.Query(topicId: topicId, limit: limit, includeSuppressed: true);
        msgs.Reverse();
        return msgs;
    }
}
