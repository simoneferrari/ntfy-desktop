using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.History;

// Only the display-only fields (TopicLabel / ServerName / ShowTopic) are observable:
// they're re-enriched in place by FeedViewModel when topic/server display settings
// change, so the bound row updates without rebuilding the list. Everything else is
// write-once at load/insert.
public partial class HistoryMessage : ObservableObject
{
    public long RowId { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public Guid TopicId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public Priority Priority { get; set; } = Priority.Default;
    public string? Title { get; set; }
    public string? Body { get; set; }

    /// <summary>Comma-joined tag list as stored in SQLite.</summary>
    public string? Tags { get; set; }

    public string? Click { get; set; }

    /// <summary>True when Click is a non-empty http(s) URL the app will follow.</summary>
    public bool HasClick => SafeUrl.IsAllowed(Click);

    /// <summary>Optional file attached to the message (image rendered inline, others as a chip).</summary>
    public NtfyAttachment? Attachment { get; set; }

    /// <summary>The attachment is an image with a usable http(s) URL — render it inline.</summary>
    public bool HasImageAttachment => Attachment is { IsImage: true } && SafeUrl.IsAllowed(Attachment.Url);

    /// <summary>The attachment is a non-image file with a usable URL — show a link chip instead.</summary>
    public bool HasFileAttachment => Attachment is { IsImage: false } && SafeUrl.IsAllowed(Attachment.Url);

    /// <summary>Chip label for a non-image attachment: filename, plus size when known.</summary>
    public string AttachmentLabel
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Attachment?.Name) ? "attachment" : Attachment!.Name!;
            return Attachment?.Size is { } size && size > 0 ? $"{name} · {FormatSize(size)}" : name;
        }
    }

    /// <summary>The decoded inline image, populated asynchronously by the feed once the row
    /// is realized (see FeedViewModel.EnsureAttachmentLoaded). Null until loaded / on failure.</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? _attachmentImage;

    /// <summary>Guards against re-triggering the async load when the virtualized row recycles.</summary>
    public bool ImageLoadStarted { get; set; }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} {units[unit]}" : $"{size:0.#} {units[unit]}";
    }

    // Display-only fields populated by FeedViewModel from current settings (the
    // repository doesn't know about display names or servers).

    /// <summary>Friendly topic label for the chip; falls back to the raw topic name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTopic))]
    private string? _topicLabel;

    /// <summary>Server label to show on the row (set only when it should be shown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServer))]
    private string? _serverName;

    /// <summary>Whether to show the topic chip — only on the combined All-topics view
    /// (redundant on a single-topic view, where every row is the same topic).</summary>
    [ObservableProperty]
    private bool _showTopic;

    public string DisplayTopic => string.IsNullOrEmpty(TopicLabel) ? Topic : TopicLabel!;
    public bool HasServer => !string.IsNullOrEmpty(ServerName);

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
