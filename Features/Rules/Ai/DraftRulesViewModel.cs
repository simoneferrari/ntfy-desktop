using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>A selectable sample message in the draft dialog.</summary>
public sealed partial class SampleVm : ObservableObject
{
    [ObservableProperty] private bool _isSelected; // unselected by default — the user picks
    public string Display { get; init; } = string.Empty;
    /// <summary>The text handed to the AI (title + body).</summary>
    public string Text { get; init; } = string.Empty;
}

/// <summary>A topic option in the dialog's source picker (null id = all topics).</summary>
public sealed record TopicChoice(Guid? Id, string Name);

/// <summary>
/// Backs the "Draft rules with AI" dialog. Services come from DI; the topic is supplied
/// via <see cref="Initialize"/> after resolution (so DI needn't know the topic id).
/// </summary>
public sealed partial class DraftRulesViewModel(
    HistoryRepository history, PackDraftService draft, PackStore packs,
    AppSettings settings, ModelCatalog modelCatalog) : ObservableObject
{
    public ObservableCollection<SampleVm> Samples { get; } = [];

    /// <summary>How many recent messages to offer as samples.</summary>
    public IReadOnlyList<int> LoadCounts { get; } = [15, 30, 50];

    [ObservableProperty] private int _loadCount = 15;

    // ===== Source: topic + model (overridable per draft) =====
    public ObservableCollection<TopicChoice> Topics { get; } = [];

    [ObservableProperty] private TopicChoice? _selectedTopic;

    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty] private string _selectedModel = string.Empty;

    [ObservableProperty] private bool _isFetchingModels;

    [ObservableProperty] private string _pasteText = string.Empty;
    [ObservableProperty] private string _intent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;

    [ObservableProperty] private string _draftJson = string.Empty;

    // Step 1 = inputs, Step 2 = review. Generate advances; Back returns.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInputStep))]
    private bool _isReviewStep;

    public bool IsInputStep => !IsReviewStep;
    public bool CanGenerate => !IsBusy;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public ObservableCollection<string> SummaryLines { get; } = [];

    /// <summary>Builds the topic + model pickers and loads samples for the starting topic.</summary>
    public void Initialize(Guid? topicId)
    {
        Topics.Clear();
        Topics.Add(new TopicChoice(null, "All topics"));
        foreach (var t in settings.Topics)
            Topics.Add(new TopicChoice(t.Id, t.EffectiveDisplayName));

        // Seed the model picker with the configured model (Refresh fetches the live list).
        SelectedModel = settings.AiModel;
        Models.Clear();
        if (!string.IsNullOrEmpty(SelectedModel)) Models.Add(SelectedModel);

        // Setting SelectedTopic triggers the first sample load.
        SelectedTopic = Topics.FirstOrDefault(t => t.Id == topicId) ?? Topics[0];
    }

    partial void OnLoadCountChanged(int value) => LoadSamples();
    partial void OnSelectedTopicChanged(TopicChoice? value) => LoadSamples();

    private void LoadSamples()
    {
        if (SelectedTopic is null) return;
        Samples.Clear();
        foreach (var m in history.Query(topicId: SelectedTopic.Id, limit: LoadCount))
        {
            var title = string.IsNullOrWhiteSpace(m.Title) ? m.Topic : m.Title!;
            var body = m.Body ?? string.Empty;
            var display = string.IsNullOrEmpty(body) ? title : $"{title} — {body}";
            Samples.Add(new SampleVm
            {
                Display = display.Length > 160 ? display[..160] + "…" : display,
                Text = string.IsNullOrEmpty(body) ? title : $"{title} | {body}",
            });
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var s in Samples) s.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var s in Samples) s.IsSelected = false;
    }

    private IReadOnlyList<string> GatherSamples()
    {
        var list = Samples.Where(s => s.IsSelected).Select(s => s.Text).ToList();
        if (!string.IsNullOrWhiteSpace(PasteText))
            list.AddRange(PasteText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return list;
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy) return;

        var samples = GatherSamples();
        if (samples.Count == 0)
        {
            ErrorText = "Select at least one sample message (or paste some).";
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            var model = string.IsNullOrWhiteSpace(SelectedModel) ? null : SelectedModel;
            var result = await draft.DraftAsync(samples, Intent, model, CancellationToken.None);
            if (!result.Ok)
            {
                ErrorText = result.Error ?? "Couldn't draft a pack.";
                return; // stay on the input step
            }

            SummaryLines.Clear();
            foreach (var line in result.Summary) SummaryLines.Add(line);
            DraftJson = result.Json ?? string.Empty;
            IsReviewStep = true; // advance to the review screen
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Back()
    {
        ErrorText = string.Empty;
        IsReviewStep = false;
    }

    // Fetches the live model list for the provider configured in Settings (best-effort).
    [RelayCommand]
    private async Task RefreshModelsAsync()
    {
        if (IsFetchingModels) return;
        IsFetchingModels = true;
        try
        {
            var list = await modelCatalog.FetchAsync(settings.AiBaseUrl, settings.GetAiApiKey(), CancellationToken.None);
            var current = SelectedModel;
            Models.Clear();
            foreach (var m in list) Models.Add(m);
            if (!string.IsNullOrEmpty(current) && !Models.Contains(current))
                Models.Insert(0, current);
            if (string.IsNullOrEmpty(SelectedModel) && Models.Count > 0)
                SelectedModel = Models[0];
        }
        finally
        {
            IsFetchingModels = false;
        }
    }

    /// <summary>Validates the (possibly user-edited) JSON and writes it. Returns true on save;
    /// sets <see cref="ErrorText"/> and returns false otherwise.</summary>
    public bool TrySave()
    {
        if (string.IsNullOrWhiteSpace(DraftJson))
        {
            ErrorText = "Nothing to save — generate a pack first.";
            return false;
        }

        RulePackName name;
        try { name = new RulePackName(PackParser.Parse(DraftJson).Name); }
        catch (Exception ex)
        {
            ErrorText = $"The pack JSON isn't valid: {ex.Message}";
            return false;
        }

        packs.Save(name.Value, DraftJson);
        return true;
    }

    // Tiny wrapper so a parse failure above is the only place that can throw.
    private readonly record struct RulePackName(string Value);
}
