using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackStoreTests : IDisposable
{
    private readonly string _dir;

    public PackStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfypacks_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MissingDirectory_YieldsEmpty()
    {
        Assert.Empty(new PackStore(_dir).Packs);
    }

    [Fact]
    public void LoadsValidPacks_AndSkipsInvalidFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "good.json"),
            """{ "name": "Good", "rules": [ { "type": "match", "when": { "topic": "x" }, "do": ["suppressToast"] } ] }""");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json");

        var store = new PackStore(_dir);
        var pack = Assert.Single(store.Packs);
        Assert.Equal("Good", pack.Name);
    }

    [Fact]
    public void Reload_PicksUpNewFiles()
    {
        Directory.CreateDirectory(_dir);
        var store = new PackStore(_dir);
        Assert.Empty(store.Packs);

        File.WriteAllText(Path.Combine(_dir, "new.json"),
            """{ "name": "New", "rules": [] }""");
        store.Reload();
        Assert.Single(store.Packs);
    }
}
