using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings.Events;
using NtfyDesktop.Features.Updates;

namespace NtfyDesktop.Features.Settings;

// Backs the Settings page. Server management is immediate-persist (via the server
// editor dialog, like topics); the page's Save/Discard snapshot covers the simple
// preference rows (defaults, system, active hours, rail display).
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly ConnectionManager _connections;
    private readonly HistoryRepository _history;
    private readonly UpdateService _updates;

    private bool _loading;
    private FormSnapshot _snapshot = FormSnapshot.Empty;

    private static readonly HashSet<string> _nonDirtyProperties =
    [
        nameof(IsDirty),
        nameof(CanSave),
        nameof(DefaultServerId),
        // Transient update-check UI — never part of the saved form.
        nameof(UpdateStatus),
        nameof(HasUpdateStatus),
        nameof(IsCheckingUpdate),
        nameof(CanCheckUpdate),
        // Channel switching acts immediately (like server CRUD), not via Save.
        nameof(UpdatesSupported),
        nameof(InstalledChannel),
        nameof(SelectedChannel),
        nameof(IsChannelSwitchPending),
        nameof(ChannelStatusText),
        nameof(SwitchButtonText),
        nameof(RevertButtonText),
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
    [ObservableProperty] private bool _autoDownloadAttachments;
    [ObservableProperty] private int _autoDownloadMaxFileMb = 5;
    [ObservableProperty] private int _attachmentCacheMaxMb = 100;
    [ObservableProperty] private bool _autoUpdateCheckEnabled = true;

    [ObservableProperty] private bool _isDirty;

    // ===== Updates (manual check — transient, not part of the saved form) =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    private string _updateStatus = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckUpdate))]
    private bool _isCheckingUpdate;

    public bool HasUpdateStatus => UpdateStatus.Length > 0;
    public bool CanCheckUpdate => !IsCheckingUpdate;

    // Carries the full SemVer including any pre-release suffix (e.g. 0.7.0-dev.1),
    // resolved once by AppVersion.
    public string CurrentVersion => AppVersion.Current;
    public string CurrentVersionText => $"You're running version {CurrentVersion}.";

    // ===== Update channel (immediate action, not part of the saved form) =====
    // Whether the channel selector is meaningful — only on a Velopack install (hidden
    // from the IDE / non-installed runs, where switching can't apply).
    public bool UpdatesSupported => _updates.IsSupported;

    // The channel actually running now vs. the one the user has selected; they differ
    // while a switch is staged but not yet applied.
    public string InstalledChannel => _updates.InstalledChannel;
    public string SelectedChannel => _updates.SelectedChannel;
    public bool IsChannelSwitchPending => _updates.IsChannelSwitchPending;

    // The channel a (non-pending) switch would move to — the other one.
    public string TargetChannel =>
        InstalledChannel == UpdateChannels.Stable ? UpdateChannels.Dev : UpdateChannels.Stable;

    public string SwitchButtonText => $"Switch to {TargetChannel} channel";
    public string RevertButtonText => $"Stay on {InstalledChannel}";

    public string ChannelStatusText
    {
        get
        {
            if (IsChannelSwitchPending)
                return $"Switching to the {SelectedChannel} channel — use the update banner to " +
                       "apply it, or revert below.";
            return InstalledChannel == UpdateChannels.Dev
                ? "You're on the dev channel — new features land here first, but builds may be less stable."
                : "You're on the stable channel. Switch to dev to get new features early (they may be less stable).";
        }
    }

    public SettingsViewModel(AppSettings settings, ConnectionManager connections,
        HistoryRepository history, UpdateService updates)
    {
        _settings = settings;
        _connections = connections;
        _history = history;
        _updates = updates;
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
        AutoDownloadAttachments = _settings.AutoDownloadAttachments;
        AutoDownloadMaxFileMb   = _settings.AutoDownloadMaxFileMb;
        AttachmentCacheMaxMb    = _settings.AttachmentCacheMaxMb;
        AutoUpdateCheckEnabled  = _settings.AutoUpdateCheckEnabled;

        ReloadServers();
        RefreshChannel();

        _snapshot = TakeSnapshot();
        _loading = false;
        IsDirty = false;
    }

    // Re-raise the computed channel getters (which read live off UpdateService) so the
    // selector reflects the current state when the page loads and after a switch.
    private void RefreshChannel()
    {
        OnPropertyChanged(nameof(UpdatesSupported));
        OnPropertyChanged(nameof(InstalledChannel));
        OnPropertyChanged(nameof(SelectedChannel));
        OnPropertyChanged(nameof(IsChannelSwitchPending));
        OnPropertyChanged(nameof(ChannelStatusText));
        OnPropertyChanged(nameof(SwitchButtonText));
        OnPropertyChanged(nameof(RevertButtonText));
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

        // A rename changes the server's DisplayLabel (and an add changes the server
        // count), which the rail subtitle and the All-topics feed chip show — refresh.
        _ = new ServerDisplayChanged().PublishAsync();

        if (original is null)
            return; // brand-new server has no topics yet — nothing to (re)connect

        // Only the connection-relevant fields matter. Compare the effective Authorization
        // header (covers method switch + token/username/password), not the encrypted blob
        // (DPAPI re-encrypts non-deterministically each save).
        var connectionChanged =
            !string.Equals(original.Url, edited.Url, StringComparison.Ordinal) ||
            !string.Equals(original.GetAuthorizationHeader(), edited.GetAuthorizationHeader(), StringComparison.Ordinal);

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
                _history.DeleteByTopicId(id, MessageDeletionSource.Removal);

        // ApplySettings drops the connections for the now-deleted topics; other
        // servers' sockets are left untouched. AppSettings.RemoveServer already
        // published ServerDeleted (carrying the removed topic ids) for the UI.
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
        _settings.AutoDownloadAttachments = AutoDownloadAttachments;
        _settings.AutoDownloadMaxFileMb   = AutoDownloadMaxFileMb;
        _settings.AttachmentCacheMaxMb    = AttachmentCacheMaxMb;

        var autoUpdateChanged = _settings.AutoUpdateCheckEnabled != AutoUpdateCheckEnabled;
        _settings.AutoUpdateCheckEnabled = AutoUpdateCheckEnabled;

        if (TimeOnly.TryParseExact(ActiveHoursStartText, "HH:mm", out var start))
            _settings.ActiveHoursStart = start;
        if (TimeOnly.TryParseExact(ActiveHoursEndText, "HH:mm", out var end))
            _settings.ActiveHoursEnd = end;

        _settings.Save();
        StartupManager.Apply(StartWithWindows);

        _snapshot = TakeSnapshot();
        IsDirty = false;

        if (railChanged)
            _ = new ServerDisplayChanged().PublishAsync();

        // Let the Updates feature decide what to do (an immediate check on enable) —
        // it's not Settings' concern.
        if (autoUpdateChanged)
            _ = new AutoUpdateCheckSettingChanged(AutoUpdateCheckEnabled).PublishAsync();

        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Discard() => Load();

    // Manual update check (the "Check for updates" button). CheckAsync raises the
    // banner + toast itself when something's found; here we drive the inline status
    // text, including the up-to-date / failed / unsupported cases that produce no event.
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        UpdateStatus = "Checking…";
        try
        {
            UpdateStatus = await _updates.CheckAsync() switch
            {
                UpdateCheckResult.UpdateAvailable => $"Version {_updates.PendingVersion} is available — use the banner to install it.",
                UpdateCheckResult.UpToDate        => "You're on the latest version.",
                UpdateCheckResult.Failed          => "Couldn't check for updates. Please try again later.",
                _                                 => "Automatic updates aren't available in this build.",
            };
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    // Applies a channel switch (the page confirms first). Delegates to UpdateService —
    // which persists the choice, repoints the updater, and re-checks — then surfaces the
    // outcome inline and refreshes the selector. A found cross-channel build (a downgrade
    // for dev→stable) also raises the banner/toast via the same check.
    public async Task SetChannelAsync(string channel)
    {
        if (IsCheckingUpdate) return;

        IsCheckingUpdate = true;
        UpdateStatus = "Checking…";
        try
        {
            await _updates.SetChannelAsync(channel);
            UpdateStatus = _updates.IsUpdatePending
                ? $"Version {_updates.PendingVersion} is available on the {channel} channel — use the banner to install it."
                : $"You're on the latest {channel} version.";
        }
        finally
        {
            IsCheckingUpdate = false;
            RefreshChannel();
        }
    }

    private FormSnapshot TakeSnapshot() => new(
        GlobalMinPriority,
        HistoryRetentionDays,
        StartWithWindows,
        ActiveHoursEnabled,
        ActiveHoursStartText,
        ActiveHoursEndText,
        ShowServerLabel,
        AutoDownloadAttachments,
        AutoDownloadMaxFileMb,
        AttachmentCacheMaxMb,
        AutoUpdateCheckEnabled);

    private readonly record struct FormSnapshot(
        Priority GlobalMinPriority,
        int HistoryRetentionDays,
        bool StartWithWindows,
        bool ActiveHoursEnabled,
        string ActiveHoursStartText,
        string ActiveHoursEndText,
        bool ShowServerLabel,
        bool AutoDownloadAttachments,
        int AutoDownloadMaxFileMb,
        int AttachmentCacheMaxMb,
        bool AutoUpdateCheckEnabled)
    {
        public static readonly FormSnapshot Empty = new(
            Priority.Min, 0, false, false, string.Empty, string.Empty, true, false, 5, 100, true);
    }
}

// Row model for the Servers list. Wraps a ServerConfig with its is-default flag.
public sealed record ServerRowVm(ServerConfig Server, bool IsDefault)
{
    public string Name => Server.DisplayLabel;
    public string Url => Server.Url;
}

