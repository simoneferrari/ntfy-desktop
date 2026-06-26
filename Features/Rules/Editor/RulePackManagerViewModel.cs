using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class RulePackManagerViewModel : ObservableObject
{
    private readonly PackStore _store;
    private readonly RulePackHistoryService _history;
    private readonly Func<IReadOnlyList<TopicInfo>> _topics;

    public RulePackManagerViewModel(
        PackStore store, RulePackHistoryService historyService,
        Func<IReadOnlyList<TopicInfo>> topics)
    {
        _store = store;
        _history = historyService;
        _topics = topics;
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

    /// <summary>Topic choices for matcher dropdowns (raw ntfy name + a grouped label).</summary>
    public ObservableCollection<TopicOption> TopicOptions { get; } = [];

    public void Reload()
    {
        Packs.Clear();
        foreach (var e in _store.GetEditablePacks()) Packs.Add(PackViewModel.FromEditable(e));
        SelectedPack = Packs.FirstOrDefault();

        Topics.Clear();
        Topics.Add(new TopicScope(null, "All topics"));
        TopicOptions.Clear();
        TopicOptions.Add(new TopicOption("", "(any topic)"));
        foreach (var t in _topics())
        {
            Topics.Add(new TopicScope(t.Id, t.Display));
            var label = string.IsNullOrWhiteSpace(t.Group) ? t.Display : $"{t.Group} / {t.Display}";
            TopicOptions.Add(new TopicOption(t.Name, label));
        }
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

    /// <summary>Adds a pre-filled rule from a starter template (see <see cref="RuleTemplates"/>).</summary>
    public void AddTemplate(string key)
    {
        if (SelectedPack is not { } p) return;
        var rule = RuleTemplates.Create(key);
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
        return _history.Preview(p.ToModel(), SelectedScopeTopic?.Id, ScopeCount);
    }

    public ApplyOutcome? Apply()
    {
        if (SelectedPack is not { } p) return null;
        if (!p.TryValidate(out var err)) { ErrorText = err; return null; }
        ErrorText = "";
        return _history.Apply(p.ToModel(), SelectedScopeTopic?.Id, ScopeCount);
    }
}

/// <summary>Topic data the manager needs: stable id, raw ntfy name, friendly display, and group.</summary>
public sealed record TopicInfo(Guid Id, string Name, string Display, string? Group);

/// <summary>A preview-scope choice (null id = all topics).</summary>
public sealed record TopicScope(Guid? Id, string Name);

/// <summary>A matcher topic choice: the raw ntfy name (empty = any) with a grouped label.</summary>
public sealed record TopicOption(string Name, string Label);
