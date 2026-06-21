using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class DraftPromptTests
{
    [Fact]
    public void BuildMessages_IncludesSystemThenUser_WithSamplesAndIntent()
    {
        var msgs = DraftPrompt.BuildMessages(
            ["PROBLEM #7 disk full", "RESOLVED #7 disk ok"],
            "pair problems with resolutions by the #number");

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("correlate", msgs[0].Content); // schema/rule-type guidance present
        Assert.Equal("user", msgs[1].Role);
        Assert.Contains("PROBLEM #7", msgs[1].Content);
        Assert.Contains("pair problems", msgs[1].Content);
    }

    [Fact]
    public void BuildMessages_NoIntent_StillValid()
    {
        var msgs = DraftPrompt.BuildMessages(["x"], null);
        Assert.Equal(2, msgs.Count);
        Assert.Contains("x", msgs[1].Content);
    }
}
