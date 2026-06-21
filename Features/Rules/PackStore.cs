using System.Diagnostics;
using System.IO;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Loads rule packs from a directory of *.json files. A file that fails to parse is
/// skipped (logged), never fatal — one bad pack can't disable the whole engine.
/// </summary>
public sealed class PackStore
{
    private readonly string _directory;

    public PackStore(string directory)
    {
        _directory = directory;
        Reload();
    }

    public IReadOnlyList<RulePack> Packs { get; private set; } = [];

    public void Reload()
    {
        if (!Directory.Exists(_directory))
        {
            Packs = [];
            return;
        }

        var packs = new List<RulePack>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try { packs.Add(PackParser.Parse(File.ReadAllText(file))); }
            catch (Exception ex) { Debug.WriteLine($"[Rules] skipped invalid pack {file}: {ex.Message}"); }
        }
        Packs = packs;
    }

    /// <summary>Writes a pack JSON to the packs directory (unique filename) and reloads,
    /// so a freshly-drafted pack takes effect without a restart. Returns the written path.</summary>
    public string Save(string suggestedName, string json)
    {
        Directory.CreateDirectory(_directory);

        var slug = new string((suggestedName ?? "pack")
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "pack";

        var path = Path.Combine(_directory, slug + ".json");
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(_directory, $"{slug}-{n++}.json");

        File.WriteAllText(path, json);
        Reload();
        return path;
    }
}
