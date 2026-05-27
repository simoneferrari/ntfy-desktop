using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.History;

public class HistoryMessage
{
    public long RowId { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public Priority Priority { get; set; } = Priority.Default;
    public string? Title { get; set; }
    public string? Body { get; set; }

    /// <summary>Comma-joined tag list as stored in SQLite.</summary>
    public string? Tags { get; set; }

    public string? Click { get; set; }

    /// <summary>True when Click is a non-empty http(s) URL the app will follow.</summary>
    public bool HasClick => SafeUrl.IsAllowed(Click);

    private (string Emojis, IReadOnlyList<string> Labels)? _tagDisplay;
    private (string Emojis, IReadOnlyList<string> Labels) TagDisplay =>
        _tagDisplay ??= EmojiTags.Format(
            string.IsNullOrEmpty(Tags) ? null : Tags.Split(',', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Concatenated emoji glyphs for any recognized tags.</summary>
    public string TagEmojis => TagDisplay.Emojis;

    /// <summary>Unrecognized tags, rendered as plain-text chips next to the topic.</summary>
    public IReadOnlyList<string> TagLabels => TagDisplay.Labels;

    /// <summary>Title with any tag emojis prepended (matches the toast).</summary>
    public string DisplayTitle
    {
        get
        {
            var baseTitle = string.IsNullOrWhiteSpace(Title) ? Topic : Title;
            return string.IsNullOrEmpty(TagEmojis) ? baseTitle : $"{TagEmojis} {baseTitle}";
        }
    }
}
