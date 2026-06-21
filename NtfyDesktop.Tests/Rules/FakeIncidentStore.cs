using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

/// <summary>In-memory IIncidentStore for engine tests.</summary>
public sealed class FakeIncidentStore : IIncidentStore
{
    private readonly Dictionary<(string, string), Incident> _open = new();

    public Incident? FindOpen(string ruleId, string key) =>
        _open.GetValueOrDefault((ruleId, key));

    public void Open(string ruleId, string key, string messageId, long openedAt) =>
        _open[(ruleId, key)] = new Incident(ruleId, key, messageId, openedAt);

    public void Resolve(string ruleId, string key) => _open.Remove((ruleId, key));

    public bool HasOpen(string ruleId, string key) => _open.ContainsKey((ruleId, key));
}
