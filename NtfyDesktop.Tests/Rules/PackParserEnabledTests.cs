using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackParserEnabledTests
{
    [Fact]
    public void Parse_EnabledAbsent_DefaultsTrue()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "match", "when": { "topic": "x" }, "do": ["suppressToast"] } ] }
            """;
        var pack = PackParser.Parse(json);
        Assert.True(pack.Enabled);
        Assert.True(pack.MatchRules[0].Enabled);
    }

    [Fact]
    public void Parse_EnabledFalse_OnPackAndRule()
    {
        const string json = """
            { "name": "p", "enabled": false, "rules": [
              { "type": "match", "enabled": false, "when": { "topic": "x" }, "do": ["suppressToast"] } ] }
            """;
        var pack = PackParser.Parse(json);
        Assert.False(pack.Enabled);
        Assert.False(pack.MatchRules[0].Enabled);
    }

    [Fact]
    public void Parse_RuleId_PreferredOverSynthesised()
    {
        const string json = """
            { "name": "Zabbix", "rules": [
              { "type": "correlate", "id": "abc123",
                "open":  { "titleRegex": "^PROBLEM" },
                "close": { "titleRegex": "^RESOLVED" },
                "key":   { "from": "body", "regex": "ID: (?<key>\\d+)" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).CorrelateRules);
        Assert.Equal("abc123", rule.Id);
    }

    [Fact]
    public void Parse_RuleId_Absent_SynthesisesLegacyKey()
    {
        const string json = """
            { "name": "Zabbix", "rules": [
              { "type": "expect", "when": { "topic": "b" },
                "every": "26h", "onAbsence": { "title": "missed" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).ExpectRules);
        Assert.Equal("Zabbix#0", rule.Id);
    }
}
