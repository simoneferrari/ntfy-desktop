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
public sealed class RuleEngine
{
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<RulePack>> _packsProvider;
    private readonly IIncidentStore _incidents;

    /// <summary>Production constructor: reads packs from the loaded PackStore.</summary>
    public RuleEngine(AppSettings settings, PackStore packs, IIncidentStore incidents)
        : this(settings, () => packs.Packs, incidents) { }

    /// <summary>Test constructor: packs supplied directly.</summary>
    public RuleEngine(AppSettings settings, Func<IReadOnlyList<RulePack>> packsProvider, IIncidentStore incidents)
    {
        _settings = settings;
        _packsProvider = packsProvider;
        _incidents = incidents;
    }

    public RuleVerdict Evaluate(NtfyMessage message)
    {
        if (!_settings.RulesEnabled) return RuleVerdict.PassThrough;

        var suppressToast = false;
        var hideFromFeed = false;
        var tags = new List<string>();
        IncidentOpen? openIncident = null;
        (string RuleId, string Key)? closeIncident = null;
        string? dismissMessageId = null;

        foreach (var pack in _packsProvider())
        {
            foreach (var rule in pack.MatchRules)
            {
                try
                {
                    if (!rule.When.Matches(message)) continue;
                    ApplyActions(rule.Actions, ref suppressToast, ref hideFromFeed, tags);
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
                        // A problem: record it open. It toasts and shows in the feed
                        // normally — a still-open problem is exactly what the feed surfaces.
                        var key = rule.Key.Extract(message);
                        if (key is not null)
                            openIncident = new IncidentOpen(rule.Id, key, message.Id, message.Time);
                    }
                    else if (rule.Close.Matches(message))
                    {
                        // A resolution: only folds when it pairs with an open problem.
                        // It still toasts (the "all good" signal), but both it and the
                        // original problem are hidden from the default feed.
                        var key = rule.Key.Extract(message);
                        if (key is not null && _incidents.FindOpen(rule.Id, key) is { } incident)
                        {
                            hideFromFeed = true;
                            closeIncident = (rule.Id, key);
                            dismissMessageId = incident.OpenMessageId;
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
            SuppressToast = suppressToast,
            HideFromFeed = hideFromFeed,
            Tags = tags,
            OpenIncident = openIncident,
            CloseIncident = closeIncident,
            DismissMessageId = dismissMessageId,
        };
    }

    private static void ApplyActions(IReadOnlyList<RuleAction> actions,
        ref bool suppressToast, ref bool hideFromFeed, List<string> tags)
    {
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case RuleActionKind.SuppressToast:
                    // A match suppress is "this is pure noise": no toast and no feed row.
                    suppressToast = true;
                    hideFromFeed = true;
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
            _incidents.Open(open.RuleId, open.Key, open.MessageId, open.OpenedAt);
        if (verdict.CloseIncident is { } close)
            _incidents.Resolve(close.RuleId, close.Key);
    }
}
