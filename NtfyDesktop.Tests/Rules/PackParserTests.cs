using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackParserTests
{
    [Fact]
    public void Parse_MatchRule_WithActions()
    {
        const string json = """
            { "name": "Backups", "rules": [
              { "type": "match",
                "when": { "topic": "backups", "titleRegex": "succeeded" },
                "do": ["suppressToast", "tag:noise"] } ] }
            """;

        var pack = PackParser.Parse(json);
        Assert.Equal("Backups", pack.Name);
        var rule = Assert.Single(pack.MatchRules);
        Assert.Equal("backups", rule.When.Topic);
        Assert.Contains(rule.Actions, a => a.Kind == RuleActionKind.SuppressToast);
        Assert.Contains(rule.Actions, a => a.Kind == RuleActionKind.Tag && a.Value == "noise");
    }

    [Fact]
    public void Parse_MinPriority_Label()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "match", "when": { "minPriority": "high" }, "do": ["suppressToast"] } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).MatchRules);
        Assert.Equal(Priority.High, rule.When.MinPriority);
    }

    [Fact]
    public void Parse_CorrelateRule_WithGeneratedId()
    {
        const string json = """
            { "name": "Zabbix", "rules": [
              { "type": "correlate",
                "open":  { "titleRegex": "^PROBLEM" },
                "close": { "titleRegex": "^RESOLVED" },
                "key":   { "from": "body", "regex": "Event ID: (?<key>\\d+)" },
                "onClose": ["suppressToast"] } ] }
            """;

        var pack = PackParser.Parse(json);
        var rule = Assert.Single(pack.CorrelateRules);
        Assert.Equal("Zabbix#0", rule.Id);
        Assert.Equal(KeyField.Body, rule.Key.From);
        Assert.Equal("^RESOLVED", rule.Close.TitleRegex);
        Assert.Equal("^PROBLEM", rule.Open.TitleRegex);
    }

    [Fact]
    public void Parse_MatchRule_MatcherFieldsAtTopLevel_StillParsed()
    {
        // Weaker models put titleRegex beside "type" instead of inside "when".
        const string json = """
            { "name":"p", "rules":[
              { "type":"match", "titleRegex":"^Backup succeeded$", "do":["suppressToast"] } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).MatchRules);
        Assert.Equal("^Backup succeeded$", rule.When.TitleRegex);
    }

    [Fact]
    public void Parse_CorrelateKey_Shorthand_BodyRegex()
    {
        // Shorthand { "body": "<regex>" } instead of { "from":"body", "regex":"..." }.
        const string json = """
            { "name":"z", "rules":[
              { "type":"correlate",
                "open":  { "titleRegex":"^PROBLEM" },
                "close": { "titleRegex":"^RESOLVED" },
                "key":   { "body": "Event ID: (?<key>\\d+)" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).CorrelateRules);
        Assert.Equal(KeyField.Body, rule.Key.From);
        Assert.Equal(@"Event ID: (?<key>\d+)", rule.Key.Regex);
    }

    [Fact]
    public void Parse_UnknownActionString_IsIgnored()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "match", "when": { "topic": "x" }, "do": ["digest", "dismissOriginal"] } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).MatchRules);
        Assert.Empty(rule.Actions); // both unknown in phase 1a → skipped, no crash
    }

    [Fact]
    public void Parse_UnknownRuleType_IsSkipped()
    {
        const string json = """
            { "name": "p", "rules": [ { "type": "expect", "when": { "topic": "x" } } ] }
            """;
        var pack = PackParser.Parse(json);
        Assert.Empty(pack.MatchRules);
        Assert.Empty(pack.CorrelateRules);
    }
}
