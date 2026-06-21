using Microsoft.Extensions.Hosting;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.History.Events;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Heartbeat / dead-man's-switch monitor. Tracks when each expect rule last saw a matching
/// message and raises a synthetic alert when one is overdue. Updates last-seen from every
/// newly-stored message (live or backfill) via MessageInserted, so a reconnect's catch-up
/// counts; a startup grace keeps it from false-firing before catch-up settles. De-dupes
/// (one alert per outage) and re-arms when matching messages resume, optionally notifying.
/// </summary>
public sealed class ExpectationMonitor : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    private readonly PackStore _packs;
    private readonly ExpectationStore _store;
    private readonly HistoryRepository _history;
    private readonly AppSettings _settings;
    private DateTimeOffset _monitorStart;

    public ExpectationMonitor(PackStore packs, ExpectationStore store,
        HistoryRepository history, AppSettings settings, EventBus bus)
    {
        _packs = packs;
        _store = store;
        _history = history;
        _settings = settings;
        bus.Subscribe<MessageInserted>(this, e => OnMessageInserted(e.Message));
    }

    private IEnumerable<ExpectRule> Rules() => _packs.Packs.SelectMany(p => p.ExpectRules);

    private void OnMessageInserted(HistoryMessage m)
    {
        if (!_settings.RulesEnabled) return;

        var tags = string.IsNullOrEmpty(m.Tags) ? null
            : m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rule in Rules())
        {
            try
            {
                if (!rule.When.Matches(m.Topic, m.Title, m.Body, m.Priority, tags)) continue;

                var wasAlerted = _store.RecordSeen(rule.Id, m.Timestamp.ToUnixTimeSeconds(), m.TopicId);
                if (wasAlerted && rule.OnRecovery is { } recovery)
                    RaiseAlert(recovery, m.TopicId);
            }
            catch { /* fail open */ }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _monitorStart = DateTimeOffset.UtcNow;

        // Seed any rule without state so a brand-new rule doesn't instantly fire.
        foreach (var rule in Rules())
            if (_store.Get(rule.Id) is null)
                _store.Seed(rule.Id, _monitorStart.ToUnixTimeSeconds());

        var timer = new PeriodicTimer(ScanInterval);
        try
        {
            do { Scan(); } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void Scan()
    {
        if (!_settings.RulesEnabled) return;

        var now = DateTimeOffset.UtcNow;
        if (now < _monitorStart + StartupGrace) return; // let catch-up settle

        foreach (var rule in Rules())
        {
            try
            {
                var state = _store.Get(rule.Id);
                if (state is null) { _store.Seed(rule.Id, now.ToUnixTimeSeconds()); continue; }
                if (state.Alerted) continue;
                if (!ExpectationEvaluator.IsOverdue(state.LastSeenAt, rule.Every, rule.Grace, now)) continue;

                RaiseAlert(rule.OnAbsence, state.TopicId);
                _store.MarkAlerted(rule.Id);
            }
            catch { /* fail open */ }
        }
    }

    // Stores a synthetic message (feed + unread) and pushes it through the toast pipeline.
    private void RaiseAlert(AlertSpec spec, Guid topicId)
    {
        var topic = _settings.GetTopicById(topicId);
        var topicName = topic?.Name ?? "rules";
        var serverId = topic?.ServerId ?? Guid.Empty;

        var synthetic = new NtfyMessage
        {
            Id = $"rule-alert-{Guid.NewGuid():N}",
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Topic = topicName,
            Priority = spec.Priority,
            Title = spec.Title,
            Message = spec.Message,
        };

        // Insert → MessageInserted (feed + unread). Then NtfyMessageReceived → toast
        // (honours pause / active-hours like any message). Not suppressed.
        if (_history.Insert(synthetic, topicId, serverId))
            _ = new NtfyMessageReceived(synthetic, topicId).PublishAsync();
    }
}
