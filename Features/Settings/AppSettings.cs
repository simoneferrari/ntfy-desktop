using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NtfyDesktop.Domain;
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
        Servers.RemoveAll(s => s.Id == serverId);
        Topics.RemoveAll(t => t.ServerId == serverId);

        if (DefaultServerId == serverId)
            DefaultServerId = Servers.Count > 0 ? Servers[0].Id : Guid.Empty;
    }

    #region props

    public List<ServerConfig> Servers { get; set; } = new();
    public Guid DefaultServerId { get; set; }

    // Legacy single-server fields — retained only as a migration source. Not used
    // at runtime once Servers is populated. Kept so older settings.json deserialize.
    public string ServerUrl { get; set; } = "https://ntfy.sh";
    public string EncryptedAccessToken { get; set; } = string.Empty;

    public Priority GlobalMinPriority { get; set; } = Priority.Min;
    public int HistoryRetentionDays { get; set; } = 30;
    public RailServerDisplay RailServerDisplay { get; set; } = RailServerDisplay.Grouped;
    // "Start with Windows" is stored exclusively in the HKCU\...\Run registry key
    // (see StartupManager); it has no representation in this file.
    public bool IsPaused { get; set; } = false;
    public bool ActiveHoursEnabled { get; set; } = false;
    public TimeOnly ActiveHoursStart { get; set; } = new TimeOnly(9, 0);
    public TimeOnly ActiveHoursEnd { get; set; } = new TimeOnly(18, 0);
    public List<TopicSettings> Topics { get; set; } = new();

    #endregion
}
