using System.IO;
using System.Reflection;
using System.Text.Json;

namespace NtfyDesktop.Domain;

/// <summary>
/// Maps ntfy tag strings (like "warning", "rotating_light") to their unicode emoji
/// glyphs, matching ntfy's web client behaviour. Unknown tags pass through as plain
/// text labels.
///
/// The source data is bundled as the embedded resource Domain/Emojis/emoji.json —
/// the same file ntfy ships in their repo at scripts/emoji.json. Parsed once on first
/// access.
/// </summary>
public static class EmojiTags
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> _map = new(LoadMap);

    /// <summary>
    /// Splits a tag list into a concatenated emoji string and a list of plain-text labels
    /// (for unmapped tags). Known tags are converted to their unicode glyph; unknown tags
    /// stay as text.
    /// </summary>
    public static (string Emojis, IReadOnlyList<string> Labels) Format(IEnumerable<string>? tags)
    {
        if (tags is null) return (string.Empty, Array.Empty<string>());

        var emojis = new List<string>();
        var labels = new List<string>();

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag)) continue;

            if (_map.Value.TryGetValue(tag, out var glyph))
                emojis.Add(glyph);
            else
                labels.Add(tag);
        }

        return (string.Concat(emojis), labels);
    }

    private static IReadOnlyDictionary<string, string> LoadMap()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("emoji.json", StringComparison.OrdinalIgnoreCase));
            if (resourceName is null) return new Dictionary<string, string>();

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null) return new Dictionary<string, string>();

            using var doc = JsonDocument.Parse(stream);
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("emoji", out var emojiEl)) continue;
                if (!entry.TryGetProperty("aliases", out var aliasesEl)) continue;

                var glyph = emojiEl.GetString();
                if (string.IsNullOrEmpty(glyph)) continue;

                foreach (var alias in aliasesEl.EnumerateArray())
                {
                    var key = alias.GetString();
                    if (string.IsNullOrEmpty(key)) continue;
                    // First alias wins on collision (shouldn't happen in practice).
                    dict.TryAdd(key, glyph);
                }
            }

            return dict;
        }
        catch
        {
            // If the resource is malformed or missing, fall back to an empty map —
            // tags will render as plain text, but the app keeps working.
            return new Dictionary<string, string>();
        }
    }
}
