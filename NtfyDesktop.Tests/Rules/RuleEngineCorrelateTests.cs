using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Tests.Rules;

public class RuleEngineCorrelateTests
{
    private static NtfyMessage Msg(string id, string title, string body) =>
        new() { Id = id, Topic = "zabbix", Title = title, Message = body, Time = 1000 };

    private static CorrelateRule ZabbixRule() => new(
        Id: "zabbix#0",
        Open: new Matcher { TitleRegex = "^PROBLEM" },
        Close: new Matcher { TitleRegex = "^RESOLVED" },
        Key: new KeySelector { From = KeyField.Body, Regex = @"Event ID: (?<key>\d+)" });

    private static (RuleEngine engine, FakeIncidentStore store) Engine(CorrelateRule rule)
    {
        var store = new FakeIncidentStore();
        var pack = new RulePack("zabbix", [], [rule], []);
        var engine = new RuleEngine(new AppSettings { RulesEnabled = true }, () => [pack], store);
        return (engine, store);
    }

    [Fact]
    public void OpenMessage_ProducesOpenIncident_ShownEverywhere()
    {
        var (engine, _) = Engine(ZabbixRule());
        var v = engine.Evaluate(Msg("p1", "PROBLEM: disk", "Event ID: 42"));

        // A problem toasts and shows in the feed — it's an open issue.
        Assert.False(v.SuppressToast);
        Assert.False(v.HideFromFeed);
        Assert.NotNull(v.OpenIncident);
        Assert.Equal("zabbix#0", v.OpenIncident!.RuleId);
        Assert.Equal("42", v.OpenIncident.Key);
        Assert.Equal("p1", v.OpenIncident.MessageId);
    }

    [Fact]
    public void CloseMessage_WithOpenIncident_ToastsButFoldsFeed_AndDismissesOriginal()
    {
        var (engine, store) = Engine(ZabbixRule());

        // Open it first and apply the side effect (simulating ConnectionManager).
        var open = engine.Evaluate(Msg("p1", "PROBLEM: disk", "Event ID: 42"));
        engine.ApplyIncidentSideEffects(open);
        Assert.True(store.HasOpen("zabbix#0", "42"));

        var close = engine.Evaluate(Msg("r1", "RESOLVED: disk", "Event ID: 42"));
        // The resolution still toasts (the "all good" signal)...
        Assert.False(close.SuppressToast);
        // ...but folds out of the feed, and dismisses the original problem row.
        Assert.True(close.HideFromFeed);
        Assert.Equal("p1", close.DismissMessageId);
        Assert.Equal(("zabbix#0", "42"), close.CloseIncident);

        engine.ApplyIncidentSideEffects(close);
        Assert.False(store.HasOpen("zabbix#0", "42"));
    }

    [Fact]
    public void CloseMessage_WithoutOpenIncident_IsShownNormally()
    {
        var (engine, _) = Engine(ZabbixRule());
        // A stray RESOLVED with no preceding PROBLEM — surface it like any message.
        var v = engine.Evaluate(Msg("r1", "RESOLVED: disk", "Event ID: 99"));
        Assert.False(v.SuppressToast);
        Assert.False(v.HideFromFeed);
        Assert.Null(v.CloseIncident);
        Assert.Null(v.DismissMessageId);
    }

    [Fact]
    public void OpenMessage_WithoutExtractableKey_ProducesNoIncident()
    {
        var (engine, _) = Engine(ZabbixRule());
        var v = engine.Evaluate(Msg("p1", "PROBLEM: disk", "no event id here"));
        Assert.Null(v.OpenIncident);
    }
}
