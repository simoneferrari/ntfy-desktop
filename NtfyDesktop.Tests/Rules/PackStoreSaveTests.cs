using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackStoreSaveTests : IDisposable
{
    private readonly string _dir;

    public PackStoreSaveTests() =>
        _dir = Path.Combine(Path.GetTempPath(), "ntfysave_" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void Save_WritesFile_AndReloads()
    {
        var store = new PackStore(_dir);
        var path = store.Save("AI Backups",
            """{ "name":"AI Backups","rules":[{"type":"match","when":{"topic":"x"},"do":["suppressToast"]}] }""");

        Assert.True(File.Exists(path));
        Assert.Single(store.Packs);
        Assert.Equal("AI Backups", store.Packs[0].Name);
    }

    [Fact]
    public void Save_Twice_DoesNotClobber()
    {
        var store = new PackStore(_dir);
        store.Save("dup", """{ "name":"dup","rules":[] }""");
        store.Save("dup", """{ "name":"dup","rules":[] }""");
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }
}
