using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;

namespace NtfyDesktop.Features.Settings;

// Backs the Settings page. Server management is immediate-persist (via the server
// editor dialog, like topics); the page's Save/Discard snapshot covers the simple
// preference rows (defaults, system, active hours, rail display).
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly HistoryRepository _history;

    private bool _loading;
    private FormSnapshot _snapshot = FormSnapshot.Empty;

    private static readonly HashSet<string> _nonDirtyProperties =
    [
        nameof(IsDirty),
        nameof(CanSave),
        nameof(DefaultServerId),
    ];

    // ===== Servers (immediate-persist) =====
    public ObservableCollection<ServerRowVm> Servers { get; } = new();

    [ObservableProperty] private Guid _defaultServerId;

    // ===== Snapshot-tracked preferences =====
    [ObservableProperty] private Priority _globalMinPriority = Priority.Min;
    [ObservableProperty] private int _historyRetentionDays = 30;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _activeHoursEnabled;
    [ObservableProperty] private string _activeHoursStartText = "09:00";
    [ObservableProperty] private string _activeHoursEndText   = "18:00";
    [ObservableProperty] private bool _showServerLabel = true;

    [ObservableProperty] private bool _isDirty;

    public SettingsViewModel(AppSettings settings, ConnectionManager connections, HistoryRepository history)
    {
        _settings = settings;
        _connections = connections;
        _history = history;
        Load();
    }

    public void Load()
    {
        _loading = true;
        GlobalMinPriority    = _settings.GlobalMinPriority;
        HistoryRetentionDays = _settings.HistoryRetentionDays;
        StartWithWindows     = StartupManager.IsEnabled();
        ActiveHoursEnabled   = _settings.ActiveHoursEnabled;
        ActiveHoursStartText = _settings.ActiveHoursStart.ToString("HH:mm");
        ActiveHoursEndText   = _settings.ActiveHoursEnd.ToString("HH:mm");
        ShowServerLabel      = _settings.ShowServerLabel;

        ReloadServers();

        _snapshot = TakeSnapshot();
        _loading = false;
        IsDirty = false;
    }

    public void ReloadServers()
    {
        DefaultServerId = _settings.DefaultServerId;
        Servers.Clear();
        foreach (var s in _settings.Servers)
            Servers.Add(new ServerRowVm(s, s.Id == _settings.DefaultServerId));
    }

    public int TopicCountForServer(Guid serverId) =>
        _settings.Topics.Count(t => t.ServerId == serverId);

    // ===== Server CRUD (persist immediately + reconnect) =====

    public async Task AddOrUpdateServerAsync(ServerConfig edited, ServerConfig? original)
    {
        if (original is not null)
        {
            var idx = _settings.Servers.IndexOf(original);
            if (idx >= 0) _settings.Servers[idx] = edited;
        }
        else
        {
            _settings.Servers.Add(edited);
            if (_settings.DefaultServerId == Guid.Empty)
                _settings.DefaultServerId = edited.Id;
        }

        _settings.Save();
        ReloadServers();

        // A rename changes the server's DisplayLabel, which the rail subtitle and
        // the All-topics feed chip show — refresh them.
        _settings.RaiseDisplayChanged();

        if (original is null)
            return; // brand-new server has no topics yet — nothing to (re)connect

        // Only the connection-relevant fields matter. Compare the decrypted token,
        // not the encrypted blob (DPAPI re-encrypts non-deterministically each save).
        var connectionChanged =
            !string.Equals(original.Url, edited.Url, StringComparison.Ordinal) ||
            !string.Equals(original.GetAccessToken(), edited.GetAccessToken(), StringComparison.Ordinal);

        if (connectionChanged)
            await _connections.RebuildServerAsync(edited.Id);
    }

    public async Task RemoveServerAsync(ServerConfig server, bool deleteHistory)
    {
        // Capture the cascade-removed topics before they're gone, so we can delete
        // their history by topic id (covers messages received while the topic lived
        // on a different server).
        var topicIds = _settings.Topics
            .Where(t => t.ServerId == server.Id)
            .Select(t => t.Id)
            .ToList();

        _settings.RemoveServer(server.Id);   // cascade-removes its topics
        _settings.Save();
        ReloadServers();

        if (deleteHistory)
            foreach (var id in topicIds)
                _history.DeleteByTopicId(id);

        // ApplySettings drops the connections for the now-deleted topics; other
        // servers' sockets are left untouched.
        await _connections.ApplySettingsAsync();
    }

    [RelayCommand]
    private void SetDefaultServer(ServerConfig? server)
    {
        if (server is null) return;
        _settings.DefaultServerId = server.Id;
        _settings.Save();
        ReloadServers();
    }

    // ===== Page-level Save/Discard =====

    public bool CanSave => true;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        if (e.PropertyName is null) return;
        if (_nonDirtyProperties.Contains(e.PropertyName)) return;

        IsDirty = !TakeSnapshot().Equals(_snapshot);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync()
    {
        var railChanged = _settings.ShowServerLabel != ShowServerLabel;

        _settings.GlobalMinPriority    = GlobalMinPriority;
        _settings.HistoryRetentionDays = HistoryRetentionDays;
        _settings.ActiveHoursEnabled   = ActiveHoursEnabled;
        _settings.ShowServerLabel      = ShowServerLabel;
        if (TimeOnly.TryParseExact(ActiveHoursStartText, "HH:mm", out var start))
            _settings.ActiveHoursStart = start;
        if (TimeOnly.TryParseExact(ActiveHoursEndText, "HH:mm", out var end))
            _settings.ActiveHoursEnd = end;

        _settings.Save();
        StartupManager.Apply(StartWithWindows);

        _snapshot = TakeSnapshot();
        IsDirty = false;

        if (railChanged)
            _settings.RaiseDisplayChanged();

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Discard() => Load();

    private FormSnapshot TakeSnapshot() => new(
        GlobalMinPriority,
        HistoryRetentionDays,
        StartWithWindows,
        ActiveHoursEnabled,
        ActiveHoursStartText,
        ActiveHoursEndText,
        ShowServerLabel);

    private readonly record struct FormSnapshot(
        Priority GlobalMinPriority,
        int HistoryRetentionDays,
        bool StartWithWindows,
        bool ActiveHoursEnabled,
        string ActiveHoursStartText,
        string ActiveHoursEndText,
        bool ShowServerLabel)
    {
        public static readonly FormSnapshot Empty = new(
            Priority.Min, 0, false, false, string.Empty, string.Empty, true);
    }
}

// Row model for the Servers list. Wraps a ServerConfig with its is-default flag.
public sealed record ServerRowVm(ServerConfig Server, bool IsDefault)
{
    public string Name => Server.DisplayLabel;
    public string Url => Server.Url;
}

