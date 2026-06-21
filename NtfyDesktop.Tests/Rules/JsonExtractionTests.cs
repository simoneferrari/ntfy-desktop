using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class JsonExtractionTests
{
    [Fact]
    public void Extract_Bare() => Assert.Equal("""{"a":1}""", JsonExtraction.ExtractObject("""{"a":1}"""));

    [Fact]
    public void Extract_FromFence() =>
        Assert.Equal("""{"a":1}""", JsonExtraction.ExtractObject("```json\n{\"a\":1}\n```"));

    [Fact]
    public void Extract_FromProse() =>
        Assert.Equal("""{"x":{"y":2}}""", JsonExtraction.ExtractObject("""Here you go: {"x":{"y":2}} done"""));

    [Fact]
    public void Extract_IgnoresBracesInStrings() =>
        Assert.Equal("""{"a":"}"}""", JsonExtraction.ExtractObject("""{"a":"}"}"""));

    [Fact]
    public void Extract_NoObject_ReturnsNull() => Assert.Null(JsonExtraction.ExtractObject("no json here"));
}
