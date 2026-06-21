using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class MatcherFieldsTests
{
    [Fact]
    public void Matches_PrimitiveFields()
    {
        var m = new Matcher { Topic = "backups", TitleRegex = "succeeded", Tag = "ok" };
        Assert.True(m.Matches("backups", "Backup succeeded", body: null, Priority.Default, ["ok"]));
        Assert.False(m.Matches("alerts", "Backup succeeded", null, Priority.Default, ["ok"]));
    }
}
