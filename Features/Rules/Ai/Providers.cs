using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NtfyDesktop.Features.Rules.Ai;

public sealed record ProviderPreset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel);

/// <summary>
/// Supplies AI provider presets by merging a built-in list (shipped and updated with the
/// app) with an optional user-maintained providers.json in the app data folder. A user
/// entry overrides a built-in of the same name and adds new ones; built-ins the user
/// hasn't touched stay current across app updates. Fails soft to whatever it can parse.
/// </summary>
public sealed class ProviderPresets
{
    private readonly string _userFilePath;

    public ProviderPresets(string userFilePath) => _userFilePath = userFilePath;

    public IReadOnlyList<ProviderPreset> All { get; private set; } = [];

    /// <summary>Loads the merged preset list. <paramref name="builtInJson"/> is the bundled
    /// default (always current with the app); the optional user file overrides/extends it.</summary>
    public void Load(string builtInJson)
    {
        var builtIn = Parse(builtInJson);
        var user = TryReadUser();

        var userByName = new Dictionary<string, ProviderPreset>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in user) userByName[p.Name] = p;

        var merged = new List<ProviderPreset>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Built-in order first; a same-named user entry overrides it in place.
        foreach (var b in builtIn)
        {
            merged.Add(userByName.TryGetValue(b.Name, out var u) ? u : b);
            seen.Add(b.Name);
        }
        // User-only entries appended after the built-ins.
        foreach (var u in user)
            if (seen.Add(u.Name))
                merged.Add(u);

        All = merged;
    }

    private IReadOnlyList<ProviderPreset> TryReadUser()
    {
        try { return File.Exists(_userFilePath) ? Parse(File.ReadAllText(_userFilePath)) : []; }
        catch { return []; }
    }

    private static IReadOnlyList<ProviderPreset> Parse(string json)
    {
        try { return JsonSerializer.Deserialize<List<ProviderPreset>>(json) ?? []; }
        catch { return []; }
    }
}
