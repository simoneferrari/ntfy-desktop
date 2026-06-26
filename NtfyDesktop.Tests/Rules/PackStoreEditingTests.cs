using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackStoreEditingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ntfyedit_" + Guid.NewGuid().ToString("N"));

    public PackStoreEditingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Match(string name, bool packEnabled, bool ruleEnabled) => $$"""
        { "name": "{{name}}", "enabled": {{(packEnabled ? "true" : "false")}}, "rules": [
          { "type": "match", "enabled": {{(ruleEnabled ? "true" : "false")}},
            "when": { "topic": "x" }, "do": ["suppressToast"] } ] }
        """;

    [Fact]
    public void Packs_ExcludesDisabledPack_ButGetEditablePacksIncludesIt()
    {
        File.WriteAllText(Path.Combine(_dir, "off.json"), Match("Off", packEnabled: false, ruleEnabled: true));
        var store = new PackStore(_dir);

        Assert.Empty(store.Packs);                       // engine view: hidden
        Assert.Single(store.GetEditablePacks());         // editor view: present
        Assert.False(store.GetEditablePacks()[0].Pack.Enabled);
    }

    [Fact]
    public void Packs_ExcludesDisabledRule_WithinEnabledPack()
    {
        File.WriteAllText(Path.Combine(_dir, "p.json"), Match("P", packEnabled: true, ruleEnabled: false));
        var store = new PackStore(_dir);

        Assert.Single(store.Packs);                      // pack visible
        Assert.Empty(store.Packs[0].MatchRules);         // its disabled rule filtered out
        Assert.Single(store.GetEditablePacks()[0].Pack.MatchRules); // still there for editing
    }

    [Fact]
    public void Overwrite_ReplacesFileContent_AndReloads()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, Match("Before", true, true));
        var store = new PackStore(_dir);

        store.Overwrite(path, Match("After", true, true));
        Assert.Equal("After", Assert.Single(store.Packs).Name);
    }

    [Fact]
    public void Delete_RemovesFile_AndReloads()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, Match("Doomed", true, true));
        var store = new PackStore(_dir);
        Assert.Single(store.Packs);

        store.Delete(path);
        Assert.Empty(store.Packs);
        Assert.False(File.Exists(path));
    }
}
