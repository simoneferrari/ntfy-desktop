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

    /// <summary>The engine-facing view: only enabled packs, and within them only
    /// enabled rules. Disabled packs/rules are filtered out so the engine stays oblivious.</summary>
    public IReadOnlyList<RulePack> Packs { get; private set; } = [];

    private IReadOnlyList<EditablePack> _editable = [];

    /// <summary>The editor-facing view: every pack file with its path and full
    /// (unfiltered) content, including disabled packs and rules.</summary>
    public IReadOnlyList<EditablePack> GetEditablePacks() => _editable;

    public void Reload()
    {
        if (!Directory.Exists(_directory))
        {
            Packs = [];
            _editable = [];
            return;
        }

        var editable = new List<EditablePack>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try { editable.Add(new EditablePack(file, PackParser.Parse(File.ReadAllText(file)))); }
            catch (Exception ex) { Debug.WriteLine($"[Rules] skipped invalid pack {file}: {ex.Message}"); }
        }
        _editable = editable;

        Packs = editable
            .Select(e => e.Pack)
            .Where(p => p.Enabled)
            .Select(p => p with
            {
                MatchRules = p.MatchRules.Where(r => r.Enabled).ToList(),
                CorrelateRules = p.CorrelateRules.Where(r => r.Enabled).ToList(),
                ExpectRules = p.ExpectRules.Where(r => r.Enabled).ToList(),
            })
            .ToList();
    }

    /// <summary>Rewrites an existing pack file in place, then reloads.</summary>
    public void Overwrite(string path, string json) { File.WriteAllText(path, json); Reload(); }

    /// <summary>Deletes a pack file, then reloads.</summary>
    public void Delete(string path) { if (File.Exists(path)) File.Delete(path); Reload(); }

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

/// <summary>A pack as loaded for editing: its file path plus the full (unfiltered) parsed content.</summary>
public sealed record EditablePack(string Path, RulePack Pack);
