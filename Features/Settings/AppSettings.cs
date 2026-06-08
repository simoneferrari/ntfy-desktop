using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Settings.Events;
using NtfyDesktop.Features.Topics;

namespace NtfyDesktop.Features.Settings;

public class AppSettings
{
    private static string _path => Path.Combine(App.DataPath, "settings.json");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        AppSettings settings;
        if (!File.Exists(_path))
        {
            settings = new();
        }
        else
        {
            try
            {
                var json = File.ReadAllText(_path);
                settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
            }
            catch
            {
                settings = new();
            }
        }

        // Bring legacy single-server config up to the multi-server model. Persists
        // if anything changed so the synthesized server/ids stick.
        if (settings.Migrate())
            settings.Save();

        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(App.DataPath);
        var json = JsonSerializer.Serialize(this, _jsonOptions);
        File.WriteAllText(_path, json);
    }
    
    // Whether the default server has a usable http(s) URL. A topic added without one
    // can't connect, so the rail's "add topic" path checks this up front.
    public bool IsDefaultServerUsable
    {
        get
        {
            var url = DefaultServer.Url.Trim();
            return !string.IsNullOrEmpty(url)
                   && Uri.TryCreate(url, UriKind.Absolute, out var u)
                   && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        }
    }

    /// <summary>
    /// Migrates a pre-multi-server config: synthesizes a "Default" server from the
    /// legacy ServerUrl/token, assigns every topic to it, and gives each topic a
    /// stable Id. Also runs on a fresh install so there's always at least one server.
    /// Returns true if anything was changed (caller persists).
    /// </summary>
    public bool Migrate()
    {
        var changed = false;

        if (Servers.Count == 0)
        {
            var server = new ServerConfig
            {
                Name = "Default",
                Url = string.IsNullOrWhiteSpace(ServerUrl) ? "https://ntfy.sh" : ServerUrl,
                EncryptedAccessToken = EncryptedAccessToken,
            };
            Servers.Add(server);
            DefaultServerId = server.Id;
            changed = true;
        }

        if (DefaultServerId == Guid.Empty || GetServer(DefaultServerId) is null)
        {
            DefaultServerId = Servers[0].Id;
            changed = true;
        }

        foreach (var topic in Topics)
        {
            if (topic.Id == Guid.Empty)        { topic.Id = Guid.NewGuid(); changed = true; }
            if (topic.ServerId == Guid.Empty)  { topic.ServerId = DefaultServerId; changed = true; }
        }

        // Rail grouping is now user-defined groups, not by-server. The old
        // RailServerDisplay enum collapses to a single "show the server label"
        // bool: Grouped/Subtitle both implied server context (-> true), None -> false.
        // Migrate once, then drop the legacy field so it stops round-tripping.
        if (RailServerDisplay is { } legacy)
        {
            ShowServerLabel = legacy != Settings.RailServerDisplay.None;
            RailServerDisplay = null;
            changed = true;
        }

        // Seed manual ordering once. The rail used to render alphabetically; the
        // Topics list order (and GroupOrder) is now the source of truth, so sort the
        // list alphabetically the first time to match what the user already sees —
        // after that, order is whatever the user arranges.
        if (!OrderInitialized)
        {
            Topics = Topics
                .OrderBy(t => t.EffectiveDisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            GroupOrder = Topics
                .Select(t => t.GroupName?.Trim())
                .Where(g => !string.IsNullOrEmpty(g))
                .Select(g => g!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            OrderInitialized = true;
            changed = true;
        }

        return changed;
    }

    public ServerConfig? GetServer(Guid id) => Servers.FirstOrDefault(s => s.Id == id);

    public ServerConfig DefaultServer => GetServer(DefaultServerId) ?? Servers[0];

    public TopicSettings? GetTopicById(Guid id) => Topics.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// Removes a server and cascade-removes all of its topics. If the removed server
    /// was the default, the default is reassigned to the first remaining server.
    /// Does not persist — caller saves.
    /// </summary>
    public void RemoveServer(Guid serverId)
    {
        // Capture the cascade-removed topic ids before removal so ServerDeleted can
        // carry them — consumers handle the event after these topics are gone.
        var removedTopicIds = Topics.Where(t => t.ServerId == serverId).Select(t => t.Id).ToList();

        Servers.RemoveAll(s => s.Id == serverId);
        Topics.RemoveAll(t => t.ServerId == serverId);

        if (DefaultServerId == serverId)
            DefaultServerId = Servers.Count > 0 ? Servers[0].Id : Guid.Empty;

        _ = new ServerDeleted(serverId, removedTopicIds).PublishAsync();
    }

    /// <summary>
    /// The SQLCipher passphrase for history.db, generating and persisting one on first use.
    /// Stored DPAPI-wrapped (CurrentUser) in settings.json — the same protection class as the
    /// access token — so the encrypted database is only readable on this user's Windows
    /// session. The passphrase is 32 random bytes, Base64-encoded; SQLCipher derives the
    /// actual key (PBKDF2). Returns it in plaintext for the connection string.
    /// </summary>
    public string GetOrCreateHistoryKey()
    {
        if (string.IsNullOrEmpty(EncryptedHistoryKey))
        {
            var passphrase = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            EncryptedHistoryKey = TokenProtector.Encrypt(passphrase);
            Save();
            return passphrase;
        }

        return TokenProtector.Decrypt(EncryptedHistoryKey);
    }

    #region props

    public List<ServerConfig> Servers { get; set; } = new();
    public Guid DefaultServerId { get; set; }

    // Legacy single-server fields — retained only as a migration source. Not used
    // at runtime once Servers is populated. Kept so older settings.json deserialize.
    public string ServerUrl { get; set; } = "https://ntfy.sh";
    public string EncryptedAccessToken { get; set; } = string.Empty;

    /// <summary>DPAPI-wrapped (CurrentUser) SQLCipher passphrase for history.db. Generated on
    /// first use via <see cref="GetOrCreateHistoryKey"/>; empty until then. Losing it (or the
    /// user's Windows profile) makes the encrypted history unrecoverable — same as the token.</summary>
    public string EncryptedHistoryKey { get; set; } = string.Empty;

    public Priority GlobalMinPriority { get; set; } = Priority.Min;
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>Download a message's attachment in the background as soon as it arrives, so
    /// it's cached locally before the ntfy server expires it (servers keep attachments only
    /// briefly). Off by default — when off, attachments fetch on demand (files on open,
    /// images when their feed is viewed).</summary>
    public bool AutoDownloadAttachments { get; set; } = false;

    /// <summary>Skip auto-downloading attachments larger than this (MB). Opening one manually
    /// ignores this and fetches up to the service's hard safety cap.</summary>
    public int AutoDownloadMaxFileMb { get; set; } = 5;

    /// <summary>Total size budget (MB) for the on-disk attachment cache, across both
    /// auto-downloaded and on-demand files; the oldest (least-recently-used) files are evicted
    /// when it's exceeded.</summary>
    public int AttachmentCacheMaxMb { get; set; } = 100;

    /// <summary>Show each topic's server as a subtitle in the rail (only meaningful
    /// with more than one server). Replaces the old by-server grouping.</summary>
    public bool ShowServerLabel { get; set; } = true;

    /// <summary>Periodically check GitHub Releases for a newer version and surface it
    /// via the in-app banner and a Windows notification. Updates are only downloaded
    /// when the user chooses to install. On by default; only has any effect in an
    /// installed (Velopack) build — a manual check is always available regardless.</summary>
    public bool AutoUpdateCheckEnabled { get; set; } = true;

    /// <summary>The update channel the user has opted into ("stable" or "dev"), driving
    /// which releases the in-app updater follows. Empty means "follow the channel this
    /// build was installed from" (derived from the running version's pre-release suffix).
    /// Set when the user switches channel in Settings; a dev→stable switch is applied as a
    /// downgrade. See <see cref="Updates.UpdateService"/>.</summary>
    public string UpdateChannel { get; set; } = "";

    /// <summary>Group names whose rail folder is collapsed. Persisted so the
    /// expand/collapse state survives restarts.</summary>
    public List<string> CollapsedGroups { get; set; } = new();

    /// <summary>User-defined display order of group folders in the rail. Groups not
    /// listed here fall back to alphabetical after the listed ones.</summary>
    public List<string> GroupOrder { get; set; } = new();

    /// <summary>Set once the manual ordering seed (alphabetical) has run, so it
    /// doesn't re-sort and clobber the user's arrangement on later launches.</summary>
    public bool OrderInitialized { get; set; }

    // Legacy rail-display enum, retained only so older settings.json deserialize and
    // migrate into ShowServerLabel (see Migrate). Nullable + ignored-when-null so it
    // disappears from the file once migrated.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RailServerDisplay? RailServerDisplay { get; set; }
    // "Start with Windows" is stored exclusively in the HKCU\...\Run registry key
    // (see StartupManager); it has no representation in this file.
    public bool IsPaused { get; set; } = false;
    public bool ActiveHoursEnabled { get; set; } = false;
    public TimeOnly ActiveHoursStart { get; set; } = new TimeOnly(9, 0);
    public TimeOnly ActiveHoursEnd { get; set; } = new TimeOnly(18, 0);
    public List<TopicSettings> Topics { get; set; } = new();

    /// <summary>Persisted main-window placement so the size/position/maximized state the
    /// user leaves it in survives a restart. New installs default to maximized.</summary>
    public WindowPlacement Window { get; set; } = new();

    #endregion
}

/// <summary>Main-window placement persisted across restarts. <see cref="Left"/>/<see cref="Top"/>/
/// <see cref="Width"/>/<see cref="Height"/> are the *normal-state* (restored) bounds — null until the
/// window has been shown once, in which case the XAML defaults apply. <see cref="Maximized"/> is the
/// state to open in; it defaults to true so a fresh install fills the screen.</summary>
public class WindowPlacement
{
    public bool Maximized { get; set; } = true;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
}
