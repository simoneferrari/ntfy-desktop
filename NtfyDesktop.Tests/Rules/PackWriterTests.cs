using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackWriterTests
{
    [Fact]
    public void Write_Match_RoundTrips()
    {
        var pack = new RulePack("Backups",
            MatchRules: [new MatchRule(
                new Matcher { Topic = "backups", TitleRegex = "succeeded", MinPriority = Priority.Low },
                [new RuleAction(RuleActionKind.SuppressToast), new RuleAction(RuleActionKind.Tag, "noise")])
                { Id = "m1", Enabled = false }],
            CorrelateRules: [], ExpectRules: []) { Enabled = false };

        var back = PackParser.Parse(PackWriter.Write(pack));

        Assert.Equal("Backups", back.Name);
        Assert.False(back.Enabled);
        var r = Assert.Single(back.MatchRules);
        Assert.Equal("m1", r.Id);
        Assert.False(r.Enabled);
        Assert.Equal("backups", r.When.Topic);
        Assert.Equal("succeeded", r.When.TitleRegex);
        Assert.Equal(Priority.Low, r.When.MinPriority);
        Assert.Contains(r.Actions, a => a.Kind == RuleActionKind.SuppressToast);
        Assert.Contains(r.Actions, a => a.Kind == RuleActionKind.Tag && a.Value == "noise");
    }

    [Fact]
    public void Write_Correlate_RoundTrips()
    {
        var pack = new RulePack("Zabbix", [],
            CorrelateRules: [new CorrelateRule("c1",
                new Matcher { TitleRegex = "^PROBLEM" },
                new Matcher { TitleRegex = "^RESOLVED" },
                new KeySelector { From = KeyField.Body, Regex = @"ID: (?<key>\d+)" }) { Enabled = true }],
            ExpectRules: []);

        var r = Assert.Single(PackParser.Parse(PackWriter.Write(pack)).CorrelateRules);
        Assert.Equal("c1", r.Id);
        Assert.Equal("^PROBLEM", r.Open.TitleRegex);
        Assert.Equal("^RESOLVED", r.Close.TitleRegex);
        Assert.Equal(KeyField.Body, r.Key.From);
        Assert.Equal(@"ID: (?<key>\d+)", r.Key.Regex);
    }

    [Fact]
    public void Write_Expect_RoundTrips()
    {
        var pack = new RulePack("HB", [], [],
            ExpectRules: [new ExpectRule("e1",
                new Matcher { Topic = "backups", TitleRegex = "succeeded" },
                Every: TimeSpan.FromHours(26), Grace: TimeSpan.FromHours(1),
                OnAbsence: new AlertSpec(Priority.Urgent, "Backup missed", "no backup"),
                OnRecovery: new AlertSpec(Priority.Default, "Backup back", null)) { Enabled = true }]);

        var r = Assert.Single(PackParser.Parse(PackWriter.Write(pack)).ExpectRules);
        Assert.Equal("e1", r.Id);
        Assert.Equal(TimeSpan.FromHours(26), r.Every);
        Assert.Equal(TimeSpan.FromHours(1), r.Grace);
        Assert.Equal("Backup missed", r.OnAbsence.Title);
        Assert.Equal(Priority.Urgent, r.OnAbsence.Priority);
        Assert.NotNull(r.OnRecovery);
        Assert.Equal("Backup back", r.OnRecovery!.Title);
    }
}
