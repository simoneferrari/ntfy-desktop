using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Feed;

// Backs the message feed for both All-topics and per-topic views.
// CurrentTopicId == null means "all topics".
public sealed partial class FeedViewModel : ObservableObject
{
    private const int MaxDisplayed = 500;

    private readonly HistoryRepository _history;
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;
    private readonly AppSettings _settings;

    [ObservableProperty] private Guid? _currentTopicId;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private Priority _minPriority = Priority.Min;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;

    // True when CurrentTopicId is set and its connection is unhealthy
    // (not Connected and not Paused). Drives the Reconnect button in the header.
    [ObservableProperty] private bool _showReconnectButton;

    public ObservableCollection<HistoryMessage> Messages { get; } = new();

    public string Title =>
        CurrentTopicId is null ? "All topics" : (TopicName() ?? "Topic");

    public string Subtitle =>
        CurrentTopicId is null
            ? "Messages from every subscribed topic."
            : $"Messages from {TopicName() ?? "this topic"}.";

    private string? TopicName() =>
        CurrentTopicId is { } id ? _settings.GetTopicById(id)?.EffectiveDisplayName : null;

    public FeedViewModel(HistoryRepository history, ConnectionManager connections, NotificationGate gate, AppSettings settings)
    {
        _history = history;
        _connections = connections;
        _gate = gate;
        _settings = settings;
        history.MessageInserted += OnHistoryMessageInserted;
        connections.ConnectionStatusChanged += OnConnectionsChanged;
        // Topic set changed (added/removed, e.g. via server deletion) — reload so the
        // feed drops messages whose topic/history was removed.
        connections.TopicsChanged += OnTopicsChanged;
        gate.GlobalStatusChanged += OnGateChanged;
        gate.TopicPauseChanged += OnTopicPauseChanged;
        _ = ReloadAsync();
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(RefreshReconnectVisibility);

    private void OnTopicsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(() => _ = ReloadAsync());

    private void OnGateChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(RefreshReconnectVisibility);

    private void OnTopicPauseChanged(object? sender, Guid topicId) =>
        Application.Current?.Dispatcher.Invoke(RefreshReconnectVisibility);

    partial void OnCurrentTopicIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        RefreshReconnectVisibility();
        _ = ReloadAsync();
    }

    private void RefreshReconnectVisibility()
    {
        if (CurrentTopicId is not { } id)
        {
            ShowReconnectButton = false;
            return;
        }
        var state = _connections.GetTopicStates()
            .FirstOrDefault(t => t.TopicId == id);
        if (state is null)
        {
            ShowReconnectButton = false;
            return;
        }
        // Hide Reconnect when the topic is paused — the socket may be Connected
        // even when paused (pause only gates toasts), but if it's not Connected
        // and the topic is paused, "reconnect" doesn't really make sense as the
        // primary call to action.
        ShowReconnectButton = !_gate.IsTopicPaused(id)
            && state.Status != TopicConnectionStatus.Connected;
    }

    [RelayCommand]
    private void Reconnect()
    {
        if (CurrentTopicId is { } id)
            _connections.ReconnectTopic(id);
    }
    partial void OnSearchTextChanged(string value) => _ = ReloadAsync();
    partial void OnMinPriorityChanged(Priority value) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        IsLoading = true;

        var topicId = CurrentTopicId;
        var minP = MinPriority;
        var search = SearchText;

        var loaded = await Task.Run(() =>
        {
            var raw = _history.Query(topicId: topicId, minPriority: minP, limit: MaxDisplayed);
            return string.IsNullOrWhiteSpace(search)
                ? raw
                : raw.Where(m => Matches(m, search)).ToList();
        });

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Messages.Clear();
            foreach (var m in loaded) Messages.Add(m);
            IsEmpty = Messages.Count == 0;
            IsLoading = false;
        });
    }

    private void OnHistoryMessageInserted(object? sender, HistoryMessage m)
    {
        if (CurrentTopicId is { } id && m.TopicId != id) return;
        if (m.Priority < MinPriority) return;
        if (!string.IsNullOrWhiteSpace(SearchText) && !Matches(m, SearchText)) return;

        Application.Current?.Dispatcher.Invoke((Action) (() =>
        {
            Messages.Insert(0, m);
            while (Messages.Count > MaxDisplayed)
                Messages.RemoveAt(Messages.Count - 1);
            IsEmpty = false;
        }));
    }

    private static bool Matches(HistoryMessage m, string q) =>
        (m.Title?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (m.Body?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (m.Tags?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
        m.Topic.Contains(q, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void Clear()
    {
        if (CurrentTopicId is { } id)
            _history.DeleteByTopicId(id);
        else
            _history.DeleteAll();

        Messages.Clear();
        IsEmpty = true;
    }

    [RelayCommand]
    private void DeleteMessage(HistoryMessage? message)
    {
        if (message is null) return;
        _history.DeleteByRowId(message.RowId);
        Messages.Remove(message);
        IsEmpty = Messages.Count == 0;
    }

    // Bound to row-level MouseBinding and to the open-link icon button.
    // SafeUrl.Open silently no-ops if the URL is missing or fails the allow-list.
    [RelayCommand]
    private void OpenClick(HistoryMessage? message) => Domain.SafeUrl.Open(message?.Click);
}
