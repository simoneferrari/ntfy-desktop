using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class ExpectationStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ExpectationStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfyexp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "rules.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private ExpectationStore New() => new(_dbPath, "k");

    [Fact]
    public void Get_Null_WhenEmpty() => Assert.Null(New().Get("r"));

    [Fact]
    public void Seed_ThenGet()
    {
        var s = New();
        s.Seed("r", 1000);
        var state = s.Get("r");
        Assert.NotNull(state);
        Assert.Equal(1000, state!.LastSeenAt);
        Assert.False(state.Alerted);
    }

    [Fact]
    public void Seed_DoesNotOverwriteExisting()
    {
        var s = New();
        s.Seed("r", 1000);
        s.Seed("r", 5000);
        Assert.Equal(1000, s.Get("r")!.LastSeenAt);
    }

    [Fact]
    public void RecordSeen_AdvancesAndClearsAlerted_ReturnsPrevAlerted()
    {
        var s = New();
        s.Seed("r", 1000);
        s.MarkAlerted("r");
        Assert.True(s.Get("r")!.Alerted);

        var wasAlerted = s.RecordSeen("r", 2000, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.True(wasAlerted);
        var state = s.Get("r")!;
        Assert.Equal(2000, state.LastSeenAt);
        Assert.False(state.Alerted);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), state.TopicId);
    }

    [Fact]
    public void RecordSeen_ForwardOnly()
    {
        var s = New();
        s.RecordSeen("r", 5000, Guid.Empty);
        s.RecordSeen("r", 2000, Guid.Empty);
        Assert.Equal(5000, s.Get("r")!.LastSeenAt);
    }
}
