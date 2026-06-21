using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Tests.Rules;

public class RuleEngineMatchTests
{
    private static NtfyMessage Msg(string topic = "backups", string? title = null) =>
        new() { Id = "m1", Topic = topic, Title = title };

    private static RuleEngine Engine(IReadOnlyList<RulePack> packs, bool enabled = true)
    {
        var settings = new AppSettings { RulesEnabled = enabled };
        return new RuleEngine(settings, () => packs, new FakeIncidentStore());
    }

    private static RulePack Pack(params MatchRule[] match) =>
        new("test", match, [], []);

    [Fact]
    public void NoRules_PassesThrough()
    {
        var v = Engine([]).Evaluate(Msg());
        Assert.False(v.SuppressToast);
        Assert.False(v.HideFromFeed);
        Assert.Empty(v.Tags);
    }

    [Fact]
    public void MatchingSuppressRule_SuppressesToastAndHidesFromFeed()
    {
        var rule = new MatchRule(
            new Matcher { Topic = "backups", TitleRegex = "succeeded" },
            [new RuleAction(RuleActionKind.SuppressToast)]);

        var v = Engine([Pack(rule)]).Evaluate(Msg(title: "Backup succeeded"));
        Assert.True(v.SuppressToast);
        Assert.True(v.HideFromFeed);
    }

    [Fact]
    public void NonMatchingRule_DoesNotSuppress()
    {
        var rule = new MatchRule(
            new Matcher { TitleRegex = "succeeded" },
            [new RuleAction(RuleActionKind.SuppressToast)]);

        var v = Engine([Pack(rule)]).Evaluate(Msg(title: "Backup FAILED"));
        Assert.False(v.SuppressToast);
        Assert.False(v.HideFromFeed);
    }

    [Fact]
    public void TagAction_CollectsTagValue()
    {
        var rule = new MatchRule(
            new Matcher { Topic = "backups" },
            [new RuleAction(RuleActionKind.Tag, "noise")]);

        var v = Engine([Pack(rule)]).Evaluate(Msg());
        Assert.Contains("noise", v.Tags);
    }

    [Fact]
    public void Disabled_PassesThroughEvenWithMatchingRule()
    {
        var rule = new MatchRule(
            new Matcher { Topic = "backups" },
            [new RuleAction(RuleActionKind.SuppressToast)]);

        var v = Engine([Pack(rule)], enabled: false).Evaluate(Msg());
        Assert.False(v.SuppressToast);
        Assert.False(v.HideFromFeed);
    }
}
