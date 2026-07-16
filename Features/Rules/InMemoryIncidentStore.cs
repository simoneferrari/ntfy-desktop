namespace NtfyDesktop.Features.Rules;

/// <summary>A non-persistent incident store for previewing/simulating correlation
/// without touching the real (SQLite) incident state.</summary>
public sealed class InMemoryIncidentStore : IIncidentStore
{
    private readonly Dictionary<(string, string), Incident> _open = new();

    public Incident? FindOpen(string ruleId, string key) => _open.GetValueOrDefault((ruleId, key));
    public void Open(string ruleId, string key, string messageId, long openedAt) =>
        _open[(ruleId, key)] = new Incident(ruleId, key, messageId, openedAt);
    public void Resolve(string ruleId, string key) => _open.Remove((ruleId, key));
}
