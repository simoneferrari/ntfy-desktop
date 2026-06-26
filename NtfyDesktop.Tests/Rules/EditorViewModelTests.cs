using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Editor;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class EditorViewModelTests
{
    [Fact]
    public void Matcher_BadRegex_FailsValidation()
    {
        var vm = new MatcherViewModel { TitleRegex = "(" };
        Assert.False(vm.TryValidate(out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void MatchRule_ToModel_BuildsActions()
    {
        var vm = new MatchRuleViewModel { Id = "m" };
        vm.When.Topic = "backups";
        vm.SuppressToast = true;
        vm.TagValue = "noise";

        var model = vm.ToModel();
        Assert.Equal("backups", model.When.Topic);
        Assert.Contains(model.Actions, a => a.Kind == RuleActionKind.SuppressToast);
        Assert.Contains(model.Actions, a => a.Kind == RuleActionKind.Tag && a.Value == "noise");
    }

    [Fact]
    public void ExpectRule_MissingTitle_FailsValidation()
    {
        var vm = new ExpectRuleViewModel { Id = "e", Every = "26h", AbsenceTitle = "" };
        Assert.False(vm.TryValidate(out _));
    }

    [Fact]
    public void ExpectRule_BadDuration_FailsValidation()
    {
        var vm = new ExpectRuleViewModel { Id = "e", Every = "nope", AbsenceTitle = "x" };
        Assert.False(vm.TryValidate(out _));
    }

    [Fact]
    public void Pack_ToJson_RoundTripsThroughParser()
    {
        var pack = new PackViewModel { Name = "Backups", Enabled = true };
        var rule = new MatchRuleViewModel { Id = "m", Enabled = true };
        rule.When.TitleRegex = "succeeded";
        rule.SuppressToast = true;
        pack.Rules.Add(rule);

        var parsed = PackParser.Parse(pack.ToJson());
        Assert.Equal("Backups", parsed.Name);
        var m = Assert.Single(parsed.MatchRules);
        Assert.Equal("m", m.Id);
        Assert.Equal("succeeded", m.When.TitleRegex);
    }
}
