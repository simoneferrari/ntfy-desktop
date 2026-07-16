using NtfyDesktop.Domain;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackHistorySimulatorTests
{
    private static HistoryMessage Msg(string id, string topic, string? title, string? body, long time) => new()
    {
        MessageId = id, Topic = topic, Title = title, Body = body,
        Priority = Priority.Default, Timestamp = DateTimeOffset.FromUnixTimeSeconds(time),
    };

    [Fact]
    public void Match_SuppressToast_MarksHidden()
    {
        var pack = new RulePack("p",
            [new MatchRule(new Matcher { TitleRegex = "succeeded" },
                [new RuleAction(RuleActionKind.SuppressToast)]) { Id = "m" }], [], []);

        var report = PackHistorySimulator.Simulate(pack,
            [Msg("1", "backups", "Backup succeeded", null, 100),
             Msg("2", "backups", "Backup FAILED", null, 200)]);

        Assert.True(report.Results.Single(r => r.Message.MessageId == "1").Hidden);
        Assert.False(report.Results.Single(r => r.Message.MessageId == "2").Hidden);
    }

    [Fact]
    public void Correlate_CloseAfterOpen_FoldsBoth()
    {
        var pack = new RulePack("z", [],
            [new CorrelateRule("c",
                new Matcher { TitleRegex = "^PROBLEM" },
                new Matcher { TitleRegex = "^RESOLVED" },
                new KeySelector { From = KeyField.Body, Regex = @"ID:(?<key>\d+)" })], []);

        var report = PackHistorySimulator.Simulate(pack,
            [Msg("p1", "z", "PROBLEM cpu", "ID:7", 100),
             Msg("r1", "z", "RESOLVED cpu", "ID:7", 200)]);

        var close = report.Results.Single(r => r.Message.MessageId == "r1");
        Assert.True(close.Hidden);                       // resolution folds from feed
        Assert.Equal("p1", close.DismissMessageId);      // and dismisses its problem
        Assert.True(report.Results.Single(r => r.Message.MessageId == "p1").OpensIncident);
    }

    [Fact]
    public void Expect_GapBeyondInterval_ReportsAbsence()
    {
        var pack = new RulePack("hb", [], [],
            [new ExpectRule("e", new Matcher { TitleRegex = "succeeded" },
                Every: TimeSpan.FromHours(1), Grace: TimeSpan.Zero,
                OnAbsence: new AlertSpec(Priority.High, "missed", null), OnRecovery: null)]);

        // Two successes 5h apart → one >1h gap.
        var report = PackHistorySimulator.Simulate(pack,
            [Msg("a", "x", "succeeded", null, 0),
             Msg("b", "x", "succeeded", null, 5 * 3600)]);

        var w = Assert.Single(report.Absences);
        Assert.Equal("missed", w.RuleTitle);
        Assert.True(w.Gap >= TimeSpan.FromHours(1));
    }
}
