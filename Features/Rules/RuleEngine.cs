using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Evaluates a received message against the loaded rule packs and returns a verdict.
/// Pure with respect to match rules; correlation reads the incident store. Incident
/// *writes* are deferred to <see cref="ApplyIncidentSideEffects"/> so the caller can
/// apply them only once the message is confirmed new (a since= catch-up re-delivers
/// the boundary message, which must not re-open/re-resolve an incident).
///
/// Fails open: a rule that throws is skipped, never silently dropping a message.
/// </summary>
public sealed class RuleEngine(
    AppSettings settings,
    Func<IReadOnlyList<RulePack>> packsProvider,
    IIncidentStore incidents)
{
    public RuleVerdict Evaluate(NtfyMessage message)
    {
        if (!settings.RulesEnabled) return RuleVerdict.PassThrough;

        var suppress = false;
        var tags = new List<string>();
        IncidentOpen? openIncident = null;
        (string RuleId, string Key)? closeIncident = null;

        foreach (var pack in packsProvider())
        {
            foreach (var rule in pack.MatchRules)
            {
                try
                {
                    if (!rule.When.Matches(message)) continue;
                    ApplyActions(rule.Actions, ref suppress, tags);
                }
                catch
                {
                    // Fail open: a malformed regex / rule never drops a message.
                }
            }

            foreach (var rule in pack.CorrelateRules)
            {
                try
                {
                    if (rule.Open.Matches(message))
                    {
                        var key = rule.Key.Extract(message);
                        if (key is not null)
                            openIncident = new IncidentOpen(rule.Id, key, message.Id, message.Time);
                    }
                    else if (rule.Close.Matches(message))
                    {
                        var key = rule.Key.Extract(message);
                        if (key is not null && incidents.FindOpen(rule.Id, key) is not null)
                        {
                            ApplyActions(rule.OnClose, ref suppress, tags);
                            closeIncident = (rule.Id, key);
                        }
                    }
                }
                catch
                {
                    // Fail open: a malformed regex / rule never drops a message.
                }
            }
        }

        return new RuleVerdict
        {
            Suppress = suppress,
            Tags = tags,
            OpenIncident = openIncident,
            CloseIncident = closeIncident,
        };
    }

    private static void ApplyActions(IReadOnlyList<RuleAction> actions, ref bool suppress, List<string> tags)
    {
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case RuleActionKind.SuppressToast:
                    suppress = true;
                    break;
                case RuleActionKind.Tag when !string.IsNullOrEmpty(action.Value):
                    if (!tags.Contains(action.Value)) tags.Add(action.Value);
                    break;
            }
        }
    }

    /// <summary>Applies the verdict's pending incident-store writes. Call only after
    /// the message is confirmed new.</summary>
    public void ApplyIncidentSideEffects(RuleVerdict verdict)
    {
        if (verdict.OpenIncident is { } open)
            incidents.Open(open.RuleId, open.Key, open.MessageId, open.OpenedAt);
        if (verdict.CloseIncident is { } close)
            incidents.Resolve(close.RuleId, close.Key);
    }
}
