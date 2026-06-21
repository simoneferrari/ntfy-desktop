using System.Net.Http;
using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class PackDraftServiceTests
{
    private sealed class FakeChatClient(string response) : IChatClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> m, string? model, CancellationToken ct) =>
            Task.FromResult(response);
    }

    private sealed class ThrowingClient : IChatClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> m, string? model, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }

    [Fact]
    public async Task DraftAsync_ValidResponse_ReturnsPackAndSummary()
    {
        const string resp = """
            ```json
            { "name":"AI","rules":[ {"type":"match","when":{"titleRegex":"succeeded"},"do":["suppressToast"]} ] }
            ```
            """;
        var result = await new PackDraftService(new FakeChatClient(resp)).DraftAsync(["x"], null, null, default);

        Assert.True(result.Ok);
        Assert.NotNull(result.Pack);
        Assert.Single(result.Pack!.MatchRules);
        Assert.NotEmpty(result.Summary);
        Assert.Contains("{", result.Json!);
    }

    [Fact]
    public async Task DraftAsync_NoJson_ReturnsError()
    {
        var result = await new PackDraftService(new FakeChatClient("sorry, no")).DraftAsync(["x"], null, null, default);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DraftAsync_EmptyPack_ReturnsError()
    {
        var result = await new PackDraftService(new FakeChatClient("""{"name":"x","rules":[]}"""))
            .DraftAsync(["x"], null, null, default);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task DraftAsync_ClientThrows_ReturnsError()
    {
        var result = await new PackDraftService(new ThrowingClient()).DraftAsync(["x"], null, null, default);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }
}
