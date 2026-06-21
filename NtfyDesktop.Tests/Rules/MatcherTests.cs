using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class MatcherTests
{
    private static NtfyMessage Msg(string topic = "backups", string? title = null,
        string? body = null, Priority priority = Priority.Default, params string[] tags) =>
        new()
        {
            Id = "m1",
            Topic = topic,
            Title = title,
            Message = body,
            Priority = priority,
            Tags = tags.Length > 0 ? tags.ToList() : null,
        };

    [Fact]
    public void EmptyMatcher_MatchesAnything()
    {
        Assert.True(new Matcher().Matches(Msg()));
    }

    [Fact]
    public void Topic_MatchesExactly_CaseInsensitive()
    {
        Assert.True(new Matcher { Topic = "Backups" }.Matches(Msg(topic: "backups")));
        Assert.False(new Matcher { Topic = "alerts" }.Matches(Msg(topic: "backups")));
    }

    [Fact]
    public void TitleRegex_Matches_Substring()
    {
        Assert.True(new Matcher { TitleRegex = "succeeded" }.Matches(Msg(title: "Backup succeeded")));
        Assert.False(new Matcher { TitleRegex = "^PROBLEM" }.Matches(Msg(title: "Backup succeeded")));
    }

    [Fact]
    public void BodyRegex_Matches_NullBody_IsFalse()
    {
        Assert.False(new Matcher { BodyRegex = "x" }.Matches(Msg(body: null)));
    }

    [Fact]
    public void MinPriority_MatchesAtOrAbove()
    {
        Assert.True(new Matcher { MinPriority = Priority.High }.Matches(Msg(priority: Priority.Urgent)));
        Assert.True(new Matcher { MinPriority = Priority.High }.Matches(Msg(priority: Priority.High)));
        Assert.False(new Matcher { MinPriority = Priority.High }.Matches(Msg(priority: Priority.Default)));
    }

    [Fact]
    public void Tag_MatchesWhenPresent_CaseInsensitive()
    {
        Assert.True(new Matcher { Tag = "Warning" }.Matches(Msg(tags: ["warning", "disk"])));
        Assert.False(new Matcher { Tag = "warning" }.Matches(Msg(tags: ["disk"])));
    }

    [Fact]
    public void MultipleConditions_AreAnded()
    {
        var m = new Matcher { Topic = "backups", TitleRegex = "succeeded" };
        Assert.True(m.Matches(Msg(topic: "backups", title: "Backup succeeded")));
        Assert.False(m.Matches(Msg(topic: "backups", title: "Backup FAILED")));
    }
}
