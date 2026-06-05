using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Feed;

/// <summary>
/// Downloads and caches message attachments for the feed. Inline images get two cache
/// layers — an in-memory map of decoded (downscaled, frozen) bitmaps for the session, plus
/// an on-disk byte cache under <c>App.DataPath\attachments\</c> keyed by a hash of the URL.
/// Any attachment (image or not) can also be materialised to a local file via
/// <see cref="EnsureFileAsync"/> so it opens with the OS default app. The disk layer means
/// files survive an app restart and outlive the ntfy server's short attachment retention;
/// stale files are swept by <see cref="AttachmentCacheSweepService"/>.
/// </summary>
public sealed class AttachmentImageService
{
    // Bound memory use: decode to at most this width (height scales proportionally).
    private const int DecodeWidth = 400;

    // Refuse to pull arbitrary multi-hundred-MB files off a publisher's URL (covers both
    // inline-image decode and open-locally). ntfy's own default attachment cap is ~15 MB.
    private const long MaxBytes = 25 * 1024 * 1024;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly AppSettings _settings;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, ImageSource> _memory = new();

    // Bound how many images download/decode at once so opening a feed full of attachments
    // doesn't fire a burst of requests at the server (cache hits skip the gate).
    private readonly SemaphoreSlim _downloadGate = new(4);

    public AttachmentImageService(AppSettings settings)
    {
        _settings = settings;
        _cacheDir = Path.Combine(App.DataPath, "attachments");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Returns the decoded inline image for the message's attachment, or null when there's
    /// no usable image / the download or decode fails (a broken image just doesn't render).
    /// Safe to call repeatedly — memory and disk caches make recycled rows cheap.
    /// </summary>
    public async Task<ImageSource?> LoadAsync(HistoryMessage message, CancellationToken ct = default)
    {
        var attachment = message.Attachment;
        var url = attachment?.Url;
        if (attachment is null || !SafeUrl.IsAllowed(url)) return null;

        if (_memory.TryGetValue(url!, out var cached)) return cached;

        try
        {
            var path = await EnsureFileAsync(attachment, message.TopicId, ct);
            if (path is null) return null;

            // A concurrent caller may have decoded it while we fetched — re-check.
            if (_memory.TryGetValue(url!, out cached)) return cached;

            var image = Decode(await File.ReadAllBytesAsync(path, ct));
            if (image is not null) _memory[url!] = image;
            return image;
        }
        catch
        {
            return null; // network/decode failure is non-fatal — the row just shows no image
        }
    }

    /// <summary>
    /// Downloads the attachment to the on-disk cache (auth-aware; the bearer token is sent
    /// only for a same-origin https URL on the topic's server) and returns the local file
    /// path, or null on failure. The cached file keeps the attachment's real extension so the
    /// OS can open it with the right app. Backs both inline-image decoding and open-locally.
    /// </summary>
    /// <param name="maxBytes">Per-file size limit for this fetch (defaults to the hard safety
    /// cap). The prefetch handler passes the user's smaller auto-download cap.</param>
    public async Task<string?> EnsureFileAsync(NtfyAttachment attachment, Guid topicId, CancellationToken ct = default, long? maxBytes = null)
    {
        var url = attachment.Url;
        if (!SafeUrl.IsAllowed(url)) return null;

        var limit = Math.Min(maxBytes ?? MaxBytes, MaxBytes);
        var path = CacheFilePath(url!, GetExtension(attachment));
        if (File.Exists(path)) { Touch(path); return path; }

        await _downloadGate.WaitAsync(ct);
        try
        {
            if (File.Exists(path)) { Touch(path); return path; } // a concurrent caller just fetched it

            var bytes = await DownloadAsync(url!, topicId, limit, ct);
            if (bytes is null) return null;

            await WriteCacheAsync(path, bytes, ct);
            EnforceQuota(keep: path); // never evict the file we just wrote (it's about to be used)
            return path;
        }
        catch
        {
            return null;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    // Authenticating only for the topic's own server, fetch the URL into memory (size-capped).
    private async Task<byte[]?> DownloadAsync(string url, Guid topicId, long maxBytes, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ResolveAuthToken(url, topicId) is { } token)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength is { } len && len > maxBytes) return null;

        return await ReadCappedAsync(response, maxBytes, ct);
    }

    // Bounded read so a server that omits Content-Length can't stream us past the cap.
    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // Bump the file's modified time so the LRU age-sweep and quota eviction treat a
    // recently-used file as fresh (Windows last-*access* tracking is unreliable).
    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { /* non-fatal */ }
    }

    // Keep the on-disk cache under the configured total budget by deleting least-recently-used
    // files (oldest modified time) first. The just-written file is exempt so opening a large
    // attachment can't delete it before the OS reads it (cache may run briefly over budget).
    private void EnforceQuota(string? keep)
    {
        try
        {
            var capBytes = (long)Math.Max(1, _settings.AttachmentCacheMaxMb) * 1024 * 1024;

            var files = new DirectoryInfo(_cacheDir).GetFiles()
                .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.LastWriteTimeUtc) // least-recently-used first
                .ToList();

            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= capBytes) break;
                if (string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
                // A locked file (e.g. open in its viewer) is left in place — total isn't
                // decremented, so a later pass retries it once released. TODO: log.
                try { var len = f.Length; f.Delete(); total -= len; } catch { /* locked/gone — skip */ }
            }
        }
        catch { /* eviction is best-effort; TODO: log */ }
    }

    private static async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken ct)
    {
        try
        {
            // Write to a temp file then move, so a crash mid-write can't leave a truncated
            // file that later reads as a corrupt image.
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* caching is best-effort; the in-memory copy still serves this session */ }
    }

    private static ImageSource? Decode(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // fully decode now so we can drop the stream
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.DecodePixelWidth = DecodeWidth;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();
        image.Freeze(); // cross-thread + immutable so the UI can bind it directly
        return image;
    }

    /// <summary>
    /// The bearer token to send for this attachment URL, or null. Only returned when the URL
    /// is same-origin (scheme+host+port) with the topic's configured server AND that origin is
    /// https — so a publisher can't point <c>attachment.url</c> at their own host to harvest the
    /// token, and the token is never sent over cleartext.
    /// </summary>
    private string? ResolveAuthToken(string url, Guid topicId)
    {
        var topic = _settings.GetTopicById(topicId);
        var server = topic is null ? null : _settings.GetServer(topic.ServerId);
        if (server is null) return null;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var attachmentUri)) return null;
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var serverUri)) return null;
        if (!attachmentUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return null;

        var sameOrigin = string.Equals(attachmentUri.Scheme, serverUri.Scheme, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(attachmentUri.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase)
                         && attachmentUri.Port == serverUri.Port;
        if (!sameOrigin) return null;

        var token = server.GetAccessToken();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    // Cache file = hash of the URL (collision-free, no path-traversal from the name) plus the
    // attachment's real extension, so Windows opens it with the right app instead of prompting.
    private string CacheFilePath(string url, string extension)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(_cacheDir, hash + extension);
    }

    // A short, safe extension (incl. the dot) for the cached file. Prefer the filename, then
    // the URL path, then a MIME-type mapping; empty when none can be determined.
    private static string GetExtension(NtfyAttachment attachment)
    {
        var ext = SafeExt(Path.GetExtension(attachment.Name ?? string.Empty));
        if (ext.Length == 0 && Uri.TryCreate(attachment.Url, UriKind.Absolute, out var uri))
            ext = SafeExt(Path.GetExtension(uri.AbsolutePath));
        if (ext.Length == 0) ext = MimeToExtension(attachment.Type);
        return ext;
    }

    // Accept only a dot followed by 1..8 ASCII alphanumerics, so a hostile "name" can't slip
    // a path separator or oversized junk into the cache filename.
    private static string SafeExt(string ext) =>
        ext.Length is > 1 and <= 9 && ext[0] == '.' && ext.Skip(1).All(char.IsLetterOrDigit)
            ? ext.ToLowerInvariant()
            : string.Empty;

    private static string MimeToExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png"       => ".png",
        "image/jpeg"      => ".jpg",
        "image/gif"       => ".gif",
        "image/webp"      => ".webp",
        "image/bmp"       => ".bmp",
        "image/tiff"      => ".tiff",
        "application/pdf" => ".pdf",
        "text/plain"      => ".txt",
        _                 => string.Empty,
    };

    /// <summary>
    /// Drops cached files and in-memory bitmaps for the given attachment URLs — called when
    /// their messages are deleted so a removed message's files don't linger. Matches files by
    /// the URL hash (independent of the stored extension) in a single directory pass.
    /// </summary>
    public void RemoveFromCache(IEnumerable<string> urls)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (string.IsNullOrEmpty(url)) continue;
            _memory.TryRemove(url, out _);
            hashes.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))));
        }
        if (hashes.Count == 0) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_cacheDir))
                // Cache files are "<hash>" or "<hash>.<ext>" (or a "<hash>.<ext>.tmp" in flight) —
                // the name without its final extension is the hash.
                if (hashes.Contains(Path.GetFileNameWithoutExtension(file)))
                    // A file the user just opened may still be locked by its viewer; skip it
                    // (don't crash) and let a later quota eviction / age sweep reclaim it once
                    // it's released. TODO: log the failure.
                    try { File.Delete(file); } catch { /* locked/gone — skip */ }
        }
        catch { /* best-effort; TODO: log */ }
    }

    /// <summary>Deletes cached attachment files older than <paramref name="days"/> (best-effort).</summary>
    public void SweepStale(int days)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(_cacheDir))
            {
                // A locked file (open in its viewer) is skipped; the next hourly sweep retries
                // it once released. TODO: log.
                try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
                catch { /* skip a file that's locked or already gone */ }
            }
        }
        catch { /* sweep failure is non-fatal; TODO: log */ }
    }
}
