using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Editor;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class EditorViewModelTests
{
    [Fact]
    public void Matcher_BadRegex_FailsValidation_InRegexMode()
    {
        var vm = new MatcherViewModel { TitleMode = MatchMode.Regex, TitleRegex = "(" };
        Assert.False(vm.TryValidate(out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Matcher_BadRegex_InContainsMode_IsLiteralAndValid()
    {
        var vm = new MatcherViewModel { TitleMode = MatchMode.Contains, TitleRegex = "(" };
        Assert.True(vm.TryValidate(out _));
        Assert.Equal(@"\(", vm.ToModel().TitleRegex);
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
        var vm = new ExpectRuleViewModel { Id = "e", EveryAmount = 26, EveryUnit = DurationUnit.Hours, AbsenceTitle = "" };
        Assert.False(vm.TryValidate(out _));
    }

    [Fact]
    public void ExpectRule_DurationUnit_BuildsTimeSpan()
    {
        var vm = new ExpectRuleViewModel
        {
            Id = "e", EveryAmount = 26, EveryUnit = DurationUnit.Hours,
            GraceAmount = 30, GraceUnit = DurationUnit.Minutes, AbsenceTitle = "x",
        };
        var model = vm.ToModel();
        Assert.Equal(TimeSpan.FromHours(26), model.Every);
        Assert.Equal(TimeSpan.FromMinutes(30), model.Grace);
    }

    [Fact]
    public void Matcher_ContainsMode_EscapesToLiteralRegex()
    {
        var vm = new MatcherViewModel { TitleMode = MatchMode.Contains, TitleRegex = "Backup (v2)" };
        Assert.Equal(@"Backup\ \(v2\)", vm.ToModel().TitleRegex);
    }

    [Fact]
    public void CorrelateKey_SimpleMode_BuildsLabelledNamedCapture()
    {
        var vm = new CorrelateRuleViewModel
        {
            Id = "c", KeyMode = KeyMode.Simple, KeyLabel = "Event ID:", KeyIdType = KeyIdType.Number,
        };
        Assert.Equal(@"Event\ ID:\s*(?<key>\d+)", vm.ToModel().Key.Regex);
    }

    [Fact]
    public void CorrelateKey_SimpleMode_NoLabel_IsJustCapture()
    {
        var vm = new CorrelateRuleViewModel { Id = "c", KeyMode = KeyMode.Simple, KeyLabel = "", KeyIdType = KeyIdType.Word };
        Assert.Equal(@"(?<key>\S+)", vm.ToModel().Key.Regex);
    }

    [Fact]
    public void CorrelateKey_RegexMode_UsesRawPattern_AndValidates()
    {
        var vm = new CorrelateRuleViewModel { Id = "c", KeyMode = KeyMode.Regex, KeyRegex = @"REF-(?<key>\d+)" };
        Assert.Equal(@"REF-(?<key>\d+)", vm.ToModel().Key.Regex);
        Assert.True(vm.TryValidate(out _));
    }

    [Fact]
    public void CorrelateKey_RegexMode_BadRegex_FailsValidation()
    {
        var vm = new CorrelateRuleViewModel { Id = "c", KeyMode = KeyMode.Regex, KeyRegex = "(?<key>" };
        Assert.False(vm.TryValidate(out _));
    }

    [Fact]
    public void Correlate_FromModel_ShowsRawRegexMode()
    {
        var model = new CorrelateRule("c",
            new Matcher { TitleRegex = "^A" }, new Matcher { TitleRegex = "^B" },
            new KeySelector { From = KeyField.Body, Regex = @"X(?<key>\d+)" });
        var vm = CorrelateRuleViewModel.FromModel(model);
        Assert.Equal(KeyMode.Regex, vm.KeyMode);
        Assert.Equal(@"X(?<key>\d+)", vm.EffectiveKeyRegex());
    }

    [Fact]
    public void Template_Heartbeat_ProducesValidExpectRule()
    {
        var rule = Assert.IsType<ExpectRuleViewModel>(RuleTemplates.Create("heartbeat"));
        Assert.False(string.IsNullOrEmpty(rule.Id));
        Assert.True(rule.TryValidate(out _));
        Assert.Equal(TimeSpan.FromHours(24), rule.ToModel().Every);
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
