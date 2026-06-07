using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.History.Events;
using NtfyDesktop.Features.Notifications;
using NtfyDesktop.Features.Notifications.Events;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Settings.Events;
using NtfyDesktop.Features.Topics;
using NtfyDesktop.Features.Topics.Events;

namespace NtfyDesktop.Features.Feed;

// Backs the message feed for both All-topics and per-topic views.
// CurrentTopicId == null means "all topics".
public sealed partial class FeedViewModel : ObservableObject
{
    private const int MAX_DISPLAYED = 500;

    private readonly HistoryRepository _history;
    private readonly ConnectionManager _connections;
    private readonly NotificationGate _gate;
    private readonly AppSettings _settings;
    private readonly AttachmentImageService _images;
    private readonly MessageActionInvoker _actions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private Guid? _currentTopicId;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private Priority _minPriority = Priority.Min;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isLoading;

    // True when CurrentTopicId is set, enabled, unpaused and not Connected.
    // Drives the Reconnect button in the header.
    [ObservableProperty] private bool _showReconnectButton;

    public ObservableCollection<HistoryMessage> Messages { get; } = [];

    public string Title =>
        CurrentTopicId is null ? "All topics" : TopicName() ?? "Topic";

    public string Subtitle =>
        CurrentTopicId is null
            ? "Messages from every subscribed topic."
            : $"Messages from {TopicName() ?? "this topic"}.";

    private string? TopicName() =>
        CurrentTopicId is { } id ? _settings.GetTopicById(id)?.EffectiveDisplayName : null;

    public FeedViewModel(HistoryRepository history, ConnectionManager connections,
        NotificationGate gate, AppSettings settings, AttachmentImageService images,
        MessageActionInvoker actions, EventBus bus)
    {
        _history = history;
        _connections = connections;
        _gate = gate;
        _settings = settings;
        _images = images;
        _actions = actions;

        // All handlers run on the UI thread (the bus marshals), so they touch Messages
        // and observable state directly — no Dispatcher.Invoke here.
        bus.Subscribe<MessageInserted>(this, e => OnMessageInserted(e.Message), ThreadOption.UIThread);
        bus.Subscribe<MessagesDeleted>(this, OnMessagesDeleted, ThreadOption.UIThread);

        // Topic renamed / server-moved / enabled flip — re-enrich its rows and, if it's
        // the current topic, refresh the header + Reconnect button.
        bus.Subscribe<TopicUpdated>(this, e => OnTopicUpdated(e.Topic), ThreadOption.UIThread);
        // Server rename / show-label toggle / count change — re-enrich server labels.
        bus.Subscribe<ServerDisplayChanged>(this, _ => ReEnrichVisibleRows(), ThreadOption.UIThread);

        // Reconnect button reflects the current topic's connection + pause state.
        bus.Subscribe<TopicConnectionStatusChanged>(this,
            e => { if (e.TopicId == CurrentTopicId) RefreshReconnectVisibility(); }, ThreadOption.UIThread);
        bus.Subscribe<TopicNotificationsStatusChanged>(this,
            e => { if (e.TopicId == CurrentTopicId) RefreshReconnectVisibility(); }, ThreadOption.UIThread);
        bus.Subscribe<NotificationsStatusChanged>(this, _ => RefreshReconnectVisibility(), ThreadOption.UIThread);

        _ = ReloadAsync();
    }

    partial void OnCurrentTopicIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        RefreshReconnectVisibility();

        _ = ReloadAsync();
    }

    private void RefreshReconnectVisibility()
    {
        // Offer Reconnect only for the currently-viewed topic when it's enabled (a
        // disabled topic has no socket, so reconnect is a no-op), not paused (pause only
        // gates toasts, not the socket), and not already Connected.
        if (CurrentTopicId is not { } id || _settings.GetTopicById(id) is not { Enabled: true })
        {
            ShowReconnectButton = false;
            return;
        }

        ShowReconnectButton = !_gate.IsTopicPaused(id)
            && _connections.GetTopicConnectionStatus(id) != TopicConnectionStatus.Connected;
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

        var allTopics = topicId is null;

        var loaded = await Task.Run(() =>
        {
            var raw = _history.Query(topicId: topicId, minPriority: minP, limit: MAX_DISPLAYED);
            var list = string.IsNullOrWhiteSpace(search)
                ? raw
                : raw.Where(m => Matches(m, search)).ToList();
            foreach (var m in list) Enrich(m, allTopics);
            return list;
        });

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            Messages.Clear();
            foreach (var m in loaded) { Messages.Add(m); EnsureAttachmentLoaded(m); }
            IsEmpty = Messages.Count == 0;
            IsLoading = false;
        });
    }

    private void OnMessageInserted(HistoryMessage m)
    {
        if (CurrentTopicId is { } id && m.TopicId != id) return;
        if (m.Priority < MinPriority) return;
        if (!string.IsNullOrWhiteSpace(SearchText) && !Matches(m, SearchText)) return;

        Enrich(m, allTopics: CurrentTopicId is null);

        Messages.Insert(0, m);
        EnsureAttachmentLoaded(m);
        while (Messages.Count > MAX_DISPLAYED)
            Messages.RemoveAt(Messages.Count - 1);
        IsEmpty = false;
    }

    private void OnMessagesDeleted(MessagesDeleted e)
    {
        // The feed removes its own rows locally (Clear / delete-message) — ignore the echo.
        if (e.Source == MessageDeletionSource.Feed) return;

        if (e.TopicId is { } topicId)
        {
            // A topic's messages were removed wholesale — prune matching rows in place.
            for (var i = Messages.Count - 1; i >= 0; i--)
                if (Messages[i].TopicId == topicId)
                    Messages.RemoveAt(i);
            IsEmpty = Messages.Count == 0;
        }
        else
        {
            // Broad/unscoped deletion (all, retention) — re-sync from the DB.
            _ = ReloadAsync();
        }
    }

    private void OnTopicUpdated(TopicSettings topic)
    {
        // Re-enrich any visible rows for this topic (display name / server label).
        foreach (var m in Messages)
            if (m.TopicId == topic.Id)
                Enrich(m, allTopics: CurrentTopicId is null);

        // If it's the topic we're viewing, its name (Title/Subtitle) and enabled/connection
        // state (Reconnect button) may have changed.
        if (topic.Id == CurrentTopicId)
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Subtitle));
            RefreshReconnectVisibility();
        }
    }

    private void ReEnrichVisibleRows()
    {
        var allTopics = CurrentTopicId is null;
        foreach (var m in Messages)
            Enrich(m, allTopics);
    }

    // Populates the message's display-only fields (friendly topic label + server)
    // from current settings. ServerName is only set when showServer is true.
    private void Enrich(HistoryMessage m, bool allTopics)
    {
        var topic = _settings.GetTopicById(m.TopicId);
        m.TopicLabel = topic?.EffectiveDisplayName ?? m.Topic;
        // Topic + server chips only on the combined All-topics view; the server
        // chip additionally needs more than one server to be meaningful.
        m.ShowTopic = allTopics;
        // Server chip on the combined view whenever more than one server exists. This
        // is independent of the sidebar "show server label" toggle (a chip in the
        // mixed feed clarifies which server a row came from regardless).
        m.ServerName = allTopics && _settings.Servers.Count > 1 && topic is not null
            ? _settings.GetServer(topic.ServerId)?.DisplayLabel
            : null;
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
            _history.DeleteByTopicId(id, MessageDeletionSource.Feed);
        else
            _history.DeleteAll(MessageDeletionSource.Feed);

        Messages.Clear();
        IsEmpty = true;
    }

    [RelayCommand]
    private void DeleteMessage(HistoryMessage? message)
    {
        if (message is null) return;
        _history.DeleteByRowId(message.RowId, MessageDeletionSource.Feed);
        Messages.Remove(message);
        IsEmpty = Messages.Count == 0;
    }

    // Bound to row-level MouseBinding and to the open-link icon button.
    // SafeUrl.Open silently no-ops if the URL is missing or fails the allow-list.
    [RelayCommand]
    private void OpenClick(HistoryMessage? message) => Domain.SafeUrl.Open(message?.Click);

    // Bound to each action button in a message row. The invoker enforces the safety
    // rules (confirm http, sanitise view, ignore broadcast) in one place.
    [RelayCommand]
    private Task InvokeAction(NtfyAction? action) =>
        action is null ? Task.CompletedTask : _actions.InvokeAsync(action);

    /// <summary>
    /// Downloads + decodes a message's inline image once. Called as messages enter the feed
    /// (load + live insert); idempotent via the per-message guard, and the service caches +
    /// caps concurrency so a feed full of images doesn't stampede the server. Must be called
    /// on the UI thread — the AttachmentImage assignment raises PropertyChanged.
    /// </summary>
    public async void EnsureAttachmentLoaded(HistoryMessage message)
    {
        if (!message.HasImageAttachment || message.ImageLoadStarted) return;
        message.ImageLoadStarted = true;

        var image = await _images.LoadAsync(message);
        if (image is not null) message.AttachmentImage = image;
    }

    // Extensions we won't hand to ShellExecute — opening one would *run* it, and a publisher
    // controls the attachment. For these (and any download failure) we fall back to the
    // browser, which downloads rather than executes, leaving the choice to the user.
    private static readonly HashSet<string> _unsafeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe",
        ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".reg", ".jar", ".hta", ".cpl",
        ".lnk", ".inf", ".dll", ".sh",
    };

    // Open an attachment by downloading it (auth-aware, server-hosted files included) to the
    // local cache with its real extension, then launching it with the user's default app for
    // that type — image viewer, Notepad, PDF reader, etc. This both authenticates (the browser
    // can't) and lets Windows pick the right handler instead of prompting. Falls back to
    // opening the URL in the browser for executable types or any download failure.
    [RelayCommand]
    private async Task OpenAttachment(HistoryMessage? message)
    {
        var attachment = message?.Attachment;
        var url = attachment?.Url;
        if (attachment is null || string.IsNullOrEmpty(url)) return;

        var path = await _images.EnsureFileAsync(attachment, message!.TopicId);
        if (path is not null && !_unsafeExtensions.Contains(System.IO.Path.GetExtension(path)))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
                return;
            }
            catch { /* fall through to opening the URL */ }
        }

        Domain.SafeUrl.Open(url);
    }
}
