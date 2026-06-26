using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class PackViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "New pack";
    [ObservableProperty] private bool _enabled = true;
    public ObservableCollection<RuleViewModel> Rules { get; } = [];

    /// <summary>The file this pack was loaded from; null for a not-yet-saved pack.</summary>
    public string? FilePath { get; set; }

    public static PackViewModel FromEditable(EditablePack e)
    {
        var vm = new PackViewModel { Name = e.Pack.Name, Enabled = e.Pack.Enabled, FilePath = e.Path };
        foreach (var m in e.Pack.MatchRules) vm.Rules.Add(MatchRuleViewModel.FromModel(m));
        foreach (var c in e.Pack.CorrelateRules) vm.Rules.Add(CorrelateRuleViewModel.FromModel(c));
        foreach (var x in e.Pack.ExpectRules) vm.Rules.Add(ExpectRuleViewModel.FromModel(x));
        return vm;
    }

    public RulePack ToModel() => new(
        Name.Trim(),
        Rules.OfType<MatchRuleViewModel>().Select(r => r.ToModel()).ToList(),
        Rules.OfType<CorrelateRuleViewModel>().Select(r => r.ToModel()).ToList(),
        Rules.OfType<ExpectRuleViewModel>().Select(r => r.ToModel()).ToList()) { Enabled = Enabled };

    public string ToJson() => PackWriter.Write(ToModel());

    public bool TryValidate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Name)) { error = "The pack needs a name."; return false; }
        foreach (var r in Rules)
            if (!r.TryValidate(out error)) { error = $"{r.Kind} rule: {error}"; return false; }
        error = ""; return true;
    }
}
