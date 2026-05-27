using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Notifications;

namespace NtfyDesktop.Features.Feed;

// Backs the message feed for both All-topics and per-topic views.
// CurrentTopic == null means "all topics".
public sealed partial class FeedViewModel : ObservableObject
{
    private const int MaxDisplayed = 500;

    private readonly HistoryRepository _history;
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;

    [ObservableProperty] private string? _currentTopic;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private Priority _minPriority = Priority.Min;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;

    // True when CurrentTopic is set and its connection is unhealthy
    // (not Connected and not Paused). Drives the Reconnect button in the header.
    [ObservableProperty] private bool _showReconnectButton;

    public ObservableCollection<HistoryMessage> Messages { get; } = new();

    public string Title => string.IsNullOrEmpty(CurrentTopic) ? "All topics" : CurrentTopic!;
    public string Subtitle => string.IsNullOrEmpty(CurrentTopic)
        ? "Messages from every subscribed topic."
        : $"Messages from {CurrentTopic}.";

    public FeedViewModel(HistoryRepository history, ConnectionManager connections, NotificationGate gate)
    {
        _history = history;
        _connections = connections;
        _gate = gate;
        history.MessageInserted += OnHistoryMessageInserted;
        connections.ConnectionStatusChanged += OnConnectionsChanged;
        gate.GlobalStatusChanged += OnGateChanged;
        gate.TopicPauseChanged += OnTopicPauseChanged;
        _ = ReloadAsync();
    }

    private void OnConnectionsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(RefreshReconnectVisibility);

    private void OnGateChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.Invoke(RefreshReconnectVisibility);

    private void OnTopicPauseChanged(object? sender, string topicName) =>
        Application.Current?.Dispatcher.Invoke(RefreshReconnectVisibility);

    partial void OnCurrentTopicChanged(string? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        RefreshReconnectVisibility();
        _ = ReloadAsync();
    }

    private void RefreshReconnectVisibility()
    {
        if (string.IsNullOrEmpty(CurrentTopic))
        {
            ShowReconnectButton = false;
            return;
        }
        var state = _connections.GetTopicStates()
            .FirstOrDefault(t => t.TopicName == CurrentTopic);
        if (state is null)
        {
            ShowReconnectButton = false;
            return;
        }
        // Hide Reconnect when the topic is paused — the socket may be Connected
        // even when paused (pause only gates toasts), but if it's not Connected
        // and the topic is paused, "reconnect" doesn't really make sense as the
        // primary call to action.
        ShowReconnectButton = !_gate.IsTopicPaused(CurrentTopic!)
            && state.Status != TopicConnectionStatus.Connected;
    }

    [RelayCommand]
    private void Reconnect()
    {
        if (string.IsNullOrEmpty(CurrentTopic)) return;
        _connections.ReconnectTopic(CurrentTopic!);
    }
    partial void OnSearchTextChanged(string value) => _ = ReloadAsync();
    partial void OnMinPriorityChanged(Priority value) => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        IsLoading = true;

        var topic = CurrentTopic;
        var minP = MinPriority;
        var search = SearchText;

        var loaded = await Task.Run(() =>
        {
            var raw = _history.Query(topic: topic, minPriority: minP, limit: MaxDisplayed);
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
        if (!string.IsNullOrEmpty(CurrentTopic) && m.Topic != CurrentTopic) return;
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
        if (string.IsNullOrEmpty(CurrentTopic))
            _history.DeleteAll();
        else
            _history.DeleteByTopic(CurrentTopic!);

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
