using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Editor;

namespace NtfyDesktop.Tests.Rules;

public class RulePackManagerViewModelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ntfymgr_" + Guid.NewGuid().ToString("N"));

    public RulePackManagerViewModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private RulePackManagerViewModel NewVm() =>
        new(new PackStore(_dir), historyService: null!, topicNames: () => []);

    [Fact]
    public void AddRule_MintsId_AndAddsToSelectedPack()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.AddRule("Match");

        var rule = Assert.Single(vm.SelectedPack!.Rules);
        Assert.False(string.IsNullOrEmpty(rule.Id));
    }

    [Fact]
    public void Save_NewPack_WritesFile_AndReloadsIntoStore()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.SelectedPack!.Name = "Backups";
        vm.AddRule("Match");
        ((MatchRuleViewModel)vm.SelectedPack.Rules[0]).When.TitleRegex = "succeeded";

        Assert.True(vm.Save());
        Assert.Single(Directory.GetFiles(_dir, "*.json"));

        // A fresh store sees the persisted, enabled pack.
        Assert.Equal("Backups", Assert.Single(new PackStore(_dir).Packs).Name);
    }

    [Fact]
    public void DisablePack_ThenSave_RemovesItFromEngineView()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.SelectedPack!.Name = "Off";
        vm.AddRule("Match");
        ((MatchRuleViewModel)vm.SelectedPack.Rules[0]).When.Topic = "x";
        vm.SelectedPack.Enabled = false;

        Assert.True(vm.Save());
        var store = new PackStore(_dir);
        Assert.Empty(store.Packs);                  // engine ignores disabled pack
        Assert.Single(store.GetEditablePacks());    // still editable
    }

    [Fact]
    public void DeleteSelectedPack_RemovesSavedFile()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.SelectedPack!.Name = "Doomed";
        vm.AddRule("Match");
        ((MatchRuleViewModel)vm.SelectedPack.Rules[0]).When.Topic = "x";
        Assert.True(vm.Save());

        vm.SelectedPack = vm.Packs[0];
        vm.DeleteSelectedPack();
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }
}
