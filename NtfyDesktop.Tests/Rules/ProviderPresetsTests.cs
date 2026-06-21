using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class ProviderPresetsTests : IDisposable
{
    private const string BuiltIn = """
        [{"name":"OpenAI","baseUrl":"https://api.openai.com/v1","defaultModel":"gpt-4o"},
         {"name":"Anthropic","baseUrl":"https://api.anthropic.com/v1","defaultModel":"claude"}]
        """;

    private readonly string _dir;
    private readonly string _userFile;

    public ProviderPresetsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfyprov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _userFile = Path.Combine(_dir, "providers.json");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public void NoUserFile_UsesBuiltIn()
    {
        var p = new ProviderPresets(_userFile);
        p.Load(BuiltIn);
        Assert.Equal(2, p.All.Count);
        Assert.Contains(p.All, x => x.Name == "OpenAI");
        Assert.Contains(p.All, x => x.Name == "Anthropic");
    }

    [Fact]
    public void UserFile_AddsNewProvider()
    {
        File.WriteAllText(_userFile, """[{"name":"Local","baseUrl":"http://x/v1"}]""");
        var p = new ProviderPresets(_userFile);
        p.Load(BuiltIn);
        Assert.Equal(3, p.All.Count);
        Assert.Contains(p.All, x => x.Name == "Local");
    }

    [Fact]
    public void UserFile_OverridesBuiltInByName()
    {
        File.WriteAllText(_userFile, """[{"name":"OpenAI","baseUrl":"http://override/v1","defaultModel":"custom"}]""");
        var p = new ProviderPresets(_userFile);
        p.Load(BuiltIn);

        Assert.Equal(2, p.All.Count); // override, not append
        var openai = p.All.First(x => x.Name == "OpenAI");
        Assert.Equal("http://override/v1", openai.BaseUrl);
        Assert.Equal("custom", openai.DefaultModel);
    }

    [Fact]
    public void InvalidUserFile_FallsBackToBuiltIn()
    {
        File.WriteAllText(_userFile, "not json");
        var p = new ProviderPresets(_userFile);
        p.Load(BuiltIn);
        Assert.Equal(2, p.All.Count);
    }

    [Fact]
    public void InvalidBuiltIn_AndNoUser_Empty()
    {
        var p = new ProviderPresets(_userFile);
        p.Load("nope");
        Assert.Empty(p.All);
    }
}
