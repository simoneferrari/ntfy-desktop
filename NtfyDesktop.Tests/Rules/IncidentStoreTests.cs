using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class IncidentStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public IncidentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfytests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "rules.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private IncidentStore NewStore() => new(_dbPath, "test-key-1234");

    [Fact]
    public void FindOpen_ReturnsNull_WhenEmpty()
    {
        Assert.Null(NewStore().FindOpen("r", "k"));
    }

    [Fact]
    public void Open_ThenFindOpen_ReturnsIncident()
    {
        var store = NewStore();
        store.Open("zabbix#0", "42", "p1", 1000);

        var found = store.FindOpen("zabbix#0", "42");
        Assert.NotNull(found);
        Assert.Equal("p1", found!.OpenMessageId);
        Assert.Equal(1000, found.OpenedAt);
    }

    [Fact]
    public void Resolve_RemovesIncident()
    {
        var store = NewStore();
        store.Open("zabbix#0", "42", "p1", 1000);
        store.Resolve("zabbix#0", "42");
        Assert.Null(store.FindOpen("zabbix#0", "42"));
    }

    [Fact]
    public void Open_IsScopedByRuleId()
    {
        var store = NewStore();
        store.Open("ruleA", "42", "p1", 1000);
        Assert.Null(store.FindOpen("ruleB", "42"));
    }

    [Fact]
    public void State_PersistsAcrossInstances()
    {
        NewStore().Open("zabbix#0", "42", "p1", 1000);
        // A second instance reopening the same encrypted file must see the row.
        Assert.NotNull(NewStore().FindOpen("zabbix#0", "42"));
    }
}
