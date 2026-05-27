using System.Diagnostics;

namespace NtfyDesktop.Domain;

/// <summary>
/// Centralised allow-list for URLs we'll act on (toast click activation, in-app row
/// clicks). Only http and https are honoured — schemes like file://, cmd:, javascript:
/// etc. would let a publisher trigger unsafe shell behaviour and are refused.
/// </summary>
public static class SafeUrl
{
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https";
    }

    /// <summary>
    /// Opens the URL via the OS default protocol handler (browser), but only if it
    /// passes the allow-list check. Silently no-ops otherwise.
    /// </summary>
    public static void Open(string? url)
    {
        if (!IsAllowed(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ShellExecute failures (no handler, user cancellation, etc.) are non-fatal.
        }
    }
}
