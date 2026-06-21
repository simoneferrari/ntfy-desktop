using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NtfyDesktop.Features.Rules.Ai;

public sealed record ProviderPreset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel);

/// <summary>
/// Loads provider presets from an overridable providers.json (so base URLs can be
/// changed without an app release). Fails soft to an empty list.
/// </summary>
public sealed class ProviderPresets
{
    private readonly string _filePath;

    public ProviderPresets(string filePath) => _filePath = filePath;

    public IReadOnlyList<ProviderPreset> All { get; private set; } = [];

    public void EnsureSeeded(string bundledJson)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (!File.Exists(_filePath))
                File.WriteAllText(_filePath, bundledJson);
            All = JsonSerializer.Deserialize<List<ProviderPreset>>(File.ReadAllText(_filePath)) ?? [];
        }
        catch
        {
            All = [];
        }
    }
}
