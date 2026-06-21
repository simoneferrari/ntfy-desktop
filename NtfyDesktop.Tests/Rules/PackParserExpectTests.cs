using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackParserExpectTests
{
    [Fact]
    public void Parse_ExpectRule_Full()
    {
        const string json = """
            { "name": "Backups", "rules": [
              { "type": "expect",
                "when": { "topic": "backups", "titleRegex": "succeeded" },
                "every": "26h", "grace": "1h",
                "onAbsence": { "priority": "urgent", "title": "Backup missed", "message": "none in 26h" },
                "onRecovery": { "priority": "default", "title": "Backups resumed" } } ] }
            """;

        var rule = Assert.Single(PackParser.Parse(json).ExpectRules);
        Assert.Equal("Backups#0", rule.Id);
        Assert.Equal("backups", rule.When.Topic);
        Assert.Equal(TimeSpan.FromHours(26), rule.Every);
        Assert.Equal(TimeSpan.FromHours(1), rule.Grace);
        Assert.Equal(Priority.Urgent, rule.OnAbsence.Priority);
        Assert.Equal("Backup missed", rule.OnAbsence.Title);
        Assert.NotNull(rule.OnRecovery);
        Assert.Equal("Backups resumed", rule.OnRecovery!.Title);
    }

    [Fact]
    public void Parse_ExpectRule_MinimalDefaults()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "expect", "when": { "topic": "x" }, "every": "1h",
                "onAbsence": { "title": "gone" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).ExpectRules);
        Assert.Equal(TimeSpan.Zero, rule.Grace);
        Assert.Equal(Priority.High, rule.OnAbsence.Priority); // default
        Assert.Null(rule.OnRecovery);
    }

    [Fact]
    public void Parse_ExpectRule_InvalidEvery_Skipped()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "expect", "when": { "topic": "x" }, "every": "soon",
                "onAbsence": { "title": "gone" } } ] }
            """;
        Assert.Empty(PackParser.Parse(json).ExpectRules);
    }

    [Fact]
    public void Parse_ExpectRule_MissingOnAbsence_Skipped()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "expect", "when": { "topic": "x" }, "every": "1h" } ] }
            """;
        Assert.Empty(PackParser.Parse(json).ExpectRules);
    }
}
