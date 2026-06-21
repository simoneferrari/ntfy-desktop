using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class ProviderPresetsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public ProviderPresetsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfyprov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "providers.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void EnsureSeeded_WritesBundled_WhenAbsent()
    {
        const string bundled = """[{"name":"OpenAI","baseUrl":"https://api.openai.com/v1","defaultModel":"gpt-4o"}]""";
        var p = new ProviderPresets(_file);
        p.EnsureSeeded(bundled);
        Assert.True(File.Exists(_file));
        var preset = Assert.Single(p.All);
        Assert.Equal("OpenAI", preset.Name);
        Assert.Equal("https://api.openai.com/v1", preset.BaseUrl);
        Assert.Equal("gpt-4o", preset.DefaultModel);
    }

    [Fact]
    public void EnsureSeeded_DoesNotOverwriteExisting()
    {
        File.WriteAllText(_file, """[{"name":"Mine","baseUrl":"http://x/v1"}]""");
        var p = new ProviderPresets(_file);
        p.EnsureSeeded("""[{"name":"OpenAI","baseUrl":"https://api.openai.com/v1"}]""");
        Assert.Equal("Mine", Assert.Single(p.All).Name);
    }

    [Fact]
    public void InvalidFile_YieldsEmpty()
    {
        File.WriteAllText(_file, "not json");
        var p = new ProviderPresets(_file);
        p.EnsureSeeded("[]");
        Assert.Empty(p.All);
    }
}
