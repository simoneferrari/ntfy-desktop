using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class RulePackManagerViewModel : ObservableObject
{
    private readonly PackStore _store;
    private readonly RulePackHistoryService _history;
    private readonly Func<IReadOnlyList<(Guid Id, string Name)>> _topicNames;

    public RulePackManagerViewModel(
        PackStore store, RulePackHistoryService historyService,
        Func<IReadOnlyList<(Guid, string)>> topicNames)
    {
        _store = store;
        _history = historyService;
        _topicNames = topicNames;
        Reload();
    }

    public ObservableCollection<PackViewModel> Packs { get; } = [];

    [ObservableProperty] private PackViewModel? _selectedPack;
    [ObservableProperty] private RuleViewModel? _selectedRule;
    [ObservableProperty] private string _errorText = "";

    public IReadOnlyList<int> ScopeCounts { get; } = [50, 200, 1000];
    [ObservableProperty] private int _scopeCount = 200;

    public ObservableCollection<TopicScope> Topics { get; } = [];
    [ObservableProperty] private TopicScope? _selectedScopeTopic;

    // Bindable preview output (populated by Preview()).
    public ObservableCollection<SimResult> PreviewResults { get; } = [];
    public ObservableCollection<AbsenceWindow> PreviewAbsences { get; } = [];
    [ObservableProperty] private string _previewSummary = "";

    public void Reload()
    {
        Packs.Clear();
        foreach (var e in _store.GetEditablePacks()) Packs.Add(PackViewModel.FromEditable(e));
        SelectedPack = Packs.FirstOrDefault();

        Topics.Clear();
        Topics.Add(new TopicScope(null, "All topics"));
        foreach (var (id, name) in _topicNames()) Topics.Add(new TopicScope(id, name));
        SelectedScopeTopic = Topics[0];
    }

    public void NewBlankPack()
    {
        var pack = new PackViewModel { Name = "New pack", FilePath = null };
        Packs.Add(pack);
        SelectedPack = pack;
    }

    public void DeleteSelectedPack()
    {
        if (SelectedPack is not { } p) return;
        if (p.FilePath is { } path) _store.Delete(path);
        Packs.Remove(p);
        SelectedPack = Packs.FirstOrDefault();
    }

    public void AddRule(string kind)
    {
        if (SelectedPack is not { } p) return;
        RuleViewModel rule = kind switch
        {
            "Correlate" => new CorrelateRuleViewModel { Id = RuleId.NewId() },
            "Expect" => new ExpectRuleViewModel { Id = RuleId.NewId() },
            _ => new MatchRuleViewModel { Id = RuleId.NewId() },
        };
        p.Rules.Add(rule);
        SelectedRule = rule;
    }

    public void DeleteSelectedRule()
    {
        if (SelectedPack is { } p && SelectedRule is { } r) { p.Rules.Remove(r); SelectedRule = null; }
    }

    public bool Save()
    {
        foreach (var p in Packs)
            if (!p.TryValidate(out var err)) { ErrorText = $"“{p.Name}”: {err}"; return false; }

        foreach (var p in Packs)
        {
            var json = p.ToJson();
            if (p.FilePath is { } path) _store.Overwrite(path, json);
            else p.FilePath = _store.Save(p.Name, json);
        }
        ErrorText = "";
        return true;
    }

    public SimReport? Preview()
    {
        if (SelectedPack is not { } p) return null;
        if (!p.TryValidate(out var err)) { ErrorText = err; return null; }
        ErrorText = "";

        var report = _history.Preview(p.ToModel(), SelectedScopeTopic?.Id, ScopeCount);

        PreviewResults.Clear();
        foreach (var r in report.Results.Where(r => r.Hidden || r.OpensIncident || r.Tags.Count > 0))
            PreviewResults.Add(r);
        PreviewAbsences.Clear();
        foreach (var a in report.Absences) PreviewAbsences.Add(a);

        var hidden = report.Results.Count(r => r.Hidden);
        PreviewSummary = $"{hidden} hidden, {report.Absences.Count} absence window(s) over {report.Results.Count} message(s).";
        return report;
    }

    public ApplyOutcome? Apply()
    {
        if (SelectedPack is not { } p) return null;
        if (!p.TryValidate(out var err)) { ErrorText = err; return null; }
        ErrorText = "";
        return _history.Apply(p.ToModel(), SelectedScopeTopic?.Id, ScopeCount);
    }
}

public sealed record TopicScope(Guid? Id, string Name);
