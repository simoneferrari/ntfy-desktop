using System.Text.Json.Serialization;

namespace NtfyDesktop.Domain;

/// <summary>
/// An optional file attached to an ntfy message. Mirrors the top-level "attachment"
/// object in the subscribe JSON: <c>{ name, type, size, expires, url }</c>. The file
/// itself lives at <see cref="Url"/> (either on the ntfy server or an external host).
/// </summary>
public sealed record NtfyAttachment
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>MIME type, e.g. <c>image/png</c>. Drives <see cref="IsImage"/>.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Size in bytes, when the server reported it.</summary>
    [JsonPropertyName("size")]
    public long? Size { get; init; }

    /// <summary>Unix timestamp after which a server-hosted attachment is purged.</summary>
    [JsonPropertyName("expires")]
    public long? Expires { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    private static readonly string[] _imageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp"];

    /// <summary>
    /// True when the attachment is an image we can render inline. Prefers the MIME type,
    /// but ntfy omits <c>type</c> for an external <c>Attach:</c> URL, so we fall back to the
    /// file extension of the name/URL. Without either signal we treat it as a non-image
    /// (a link chip) rather than risk a broken inline render.
    /// </summary>
    [JsonIgnore]
    public bool IsImage =>
        Type is { Length: > 0 }
            ? Type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            : HasImageExtension(Name) || HasImageExtension(Url);

    private static bool HasImageExtension(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Strip any query/fragment so "photo.png?w=400" still matches.
        var path = value.Split('?', '#')[0];
        return _imageExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}
