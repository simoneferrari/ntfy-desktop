using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Features.Connections.Events;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings;
using NtfyDesktop.Features.Settings.Events;
using NtfyDesktop.Features.Topics.Events;

namespace NtfyDesktop.Features.Connections;

// Backs the Connections page: per-topic connection state plus Reconnect /
// Disconnect-all / Reconnect-all hard resets. Notification pause is a
// separate concern — it lives on the title bar and per-topic context menu,
// not on this page.
public sealed partial class ConnectionsViewModel : ObservableObject
{
    private readonly ConnectionManager _connections;
    private readonly AppSettings _settings;
    private readonly HistoryRepository _history;

    public ObservableCollection<TopicConnectionRow> Rows { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;

    // Gates the dev-only "Reset catch-up" button — present in Debug builds only.
    public bool ShowDevTools =>
#if DEBUG
        true;
#else
        false;
#endif

    public ConnectionsViewModel(ConnectionManager connections, AppSettings settings, HistoryRepository history, EventBus bus)
    {
        _connections = connections;
        _settings = settings;
        _history = history;

        // Status — high-frequency: replace just the affected row, no rebuild.
        bus.Subscribe<TopicConnectionStatusChanged>(this, OnTopicConnectionStatusChanged, ThreadOption.UIThread);

        // Structural / display — low-frequency: rebuild the row set (recomputes the
        // ShowServer cross-cut). Deletes are covered too — Refresh sources rows from
        // settings, which no longer list the removed topics.
        bus.Subscribe<TopicAdded>(this, _ => Refresh(), ThreadOption.UIThread);
        bus.Subscribe<TopicUpdated>(this, _ => Refresh(), ThreadOption.UIThread);
        bus.Subscribe<TopicDeleted>(this, _ => Refresh(), ThreadOption.UIThread);
        bus.Subscribe<ServerDisplayChanged>(this, _ => Refresh(), ThreadOption.UIThread);
        bus.Subscribe<ServerDeleted>(this, _ => Refresh(), ThreadOption.UIThread);

        Refresh();
    }

    private void OnTopicConnectionStatusChanged(TopicConnectionStatusChanged e)
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].TopicId != e.TopicId) continue;
            Rows[i] = Rows[i].WithStatus(e.Status, e.LastError);
            break;
        }
        DisconnectAllCommand.NotifyCanExecuteChanged();
    }

    private void Refresh()
    {
        Rows.Clear();
        var states = _connections.GetTopicStates();
        // Show the server only when there's more than one in play — otherwise it's noise.
        var showServer = states.Select(s => s.ServerName).Distinct().Count() > 1;
        foreach (var s in states)
            Rows.Add(new TopicConnectionRow(s, showServer));
        IsEmpty = Rows.Count == 0;
        DisconnectAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Reconnect(Guid topicId)
    {
        if (topicId == Guid.Empty) return;
        _connections.ReconnectTopic(topicId);
    }

    [RelayCommand]
    private async Task ReconnectAll() => await _connections.RestartAllAsync();

    [RelayCommand(CanExecute = nameof(CanDisconnectAll))]
    private async Task DisconnectAll() => await _connections.DisconnectAllAsync();

    private bool CanDisconnectAll() =>
        Rows.Any(r => r.ConnectionStatus != TopicConnectionStatus.Disconnected);

#if DEBUG
    // Dev aid: rewind every configured topic's catch-up cursor to the epoch and reconnect,
    // forcing ntfy to replay its whole cache (~12h) so catch-up can be re-tested without
    // hand-editing the database. Hidden in Release (see ShowDevTools / button visibility).
    [RelayCommand]
    private async Task DevResetCatchUp()
    {
        _history.DevRewindCursors(_settings.Topics.Select(t => t.Id), time: 0);
        await _connections.RestartAllAsync();
    }
#endif
}
