using Microsoft.Win32;

namespace NtfyDesktop.Features.Shell;

/// <summary>
/// Registers the per-user "ntfy-desktop://" URL protocol so toast click activations
/// (and any other launch via the URL) start this app with the URL as its first argument.
/// Mirrors the StartupManager pattern — HKCU only, never HKLM, never elevation.
/// </summary>
public static class ProtocolRegistration
{
    public const string SCHEME = "ntfy-desktop";

    public static void Apply()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // HKCU\Software\Classes\ntfy-desktop
            //   (default)        = "URL:Ntfy Desktop"
            //   URL Protocol     = ""
            //   shell\open\command\(default) = "<exe>" "%1"
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{SCHEME}");
            root.SetValue(string.Empty, $"URL:{App.NAME}");
            root.SetValue("URL Protocol", string.Empty);

            using var cmd = root.CreateSubKey(@"shell\open\command");
            cmd.SetValue(string.Empty, $"\"{exePath}\" \"%1\"");
        }
        catch
        {
            // Registry write failure is non-fatal — the app just won't respond to
            // toast clicks until the user re-launches with write access.
        }
    }
}
