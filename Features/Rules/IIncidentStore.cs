namespace NtfyDesktop.Features.Rules;

/// <summary>An open (unresolved) correlated incident.</summary>
public sealed record Incident(string RuleId, string Key, string OpenMessageId, long OpenedAt);

/// <summary>
/// Tracks open correlated incidents so a "resolved" message can be paired with the
/// "problem" that opened it. Keyed by (rule id, extracted key).
/// </summary>
public interface IIncidentStore
{
    Incident? FindOpen(string ruleId, string key);
    void Open(string ruleId, string key, string messageId, long openedAt);
    void Resolve(string ruleId, string key);
}
