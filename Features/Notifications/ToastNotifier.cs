using System.IO;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Microsoft.Win32;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Shell;

namespace NtfyDesktop.Features.Notifications;

public class ToastNotifier
{
    private const string APP_ID = "NtfyDesktop";
    private Windows.UI.Notifications.ToastNotifier? _notifier;

    // Registers the AUMID in the registry so Windows can attribute toasts to this app.
    // No Start Menu shortcut or COM server needed for display-only toasts.
    public void Register()
    {
        try
        {
            const string KEY_PATH = $@"SOFTWARE\Classes\AppUserModelId\{APP_ID}";
            using var key = Registry.CurrentUser.CreateSubKey(KEY_PATH);
            key.SetValue("DisplayName", App.NAME);

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                key.SetValue("IconUri", exePath);

            _notifier = ToastNotificationManager.CreateToastNotifier(APP_ID);
        }
        catch
        {
            _notifier = null;
        }
    }

    public void Show(NtfyMessage message)
    {
        if (_notifier == null) return;

        try
        {
            var baseTitle = string.IsNullOrWhiteSpace(message.Title)
                ? message.Topic
                : message.Title;

            // Prepend any tag-derived emoji glyphs to the title (matches ntfy's web client).
            // Unmapped tags don't appear here — they're shown as small labels in the feed.
            var emojis = EmojiTags.Format(message.Tags).Emojis;
            var title = string.IsNullOrEmpty(emojis) ? baseTitle : $"{emojis} {baseTitle}";

            var body = message.Message ?? string.Empty;

            var xml = BuildToastXml(title, body, message.Topic, message.Priority, message.Click, message.Id);
            var toast = new ToastNotification(xml)
            {
                // Group toasts by topic; tag by message id so duplicates replace rather than stack
                Group = message.Topic,
                Tag = TruncateTag(message.Id),
            };

            _notifier.Show(toast);
        }
        catch { /* toast delivery failure is non-fatal */ }
    }

    private static XmlDocument BuildToastXml(string title, string body, string topic, Priority priority, string? clickUrl, string messageId)
    {
        // Urgent: persistent toast + alarm sound until dismissed
        var scenarioAttr = priority == Priority.Urgent ? @" scenario=""urgent""" : string.Empty;

        // Click activation:
        //   - Publisher-set http(s) URL → browser opens it
        //   - Otherwise → fall back to ntfy-desktop://show?topic=...&msg=... which
        //     brings our app to the foreground at the relevant topic feed
        // Other schemes from the publisher are refused (see Domain/SafeUrl.cs).
        var launchUrl = SafeUrl.IsAllowed(clickUrl)
            ? clickUrl!
            : BuildAppActivationUrl(topic, messageId);
        var clickAttrs = $@" activationType=""protocol"" launch=""{EscapeXml(launchUrl)}""";

        var audioElement = priority switch
        {
            Priority.Urgent => @"<audio src=""ms-winsoundevent:Notification.Looping.Alarm"" loop=""true"" />",
            Priority.High   => @"<audio src=""ms-winsoundevent:Notification.Looping.Call"" />",
            _               => string.Empty,
        };

        var attribution = FormatAttribution(topic, priority);

        var doc = new XmlDocument();
        doc.LoadXml($"""
            <toast{scenarioAttr}{clickAttrs}>
              <visual>
                <binding template="ToastGeneric">
                  <text>{EscapeXml(title)}</text>
                  <text>{EscapeXml(body)}</text>
                  <text placement="attribution">{EscapeXml(attribution)}</text>
                </binding>
              </visual>
              {audioElement}
            </toast>
            """);
        return doc;
    }

    private static string FormatAttribution(string topic, Priority priority) => priority switch
    {
        Priority.Urgent => $"🔴 Urgent · {topic}",
        Priority.High   => $"🟠 High · {topic}",
        Priority.Low    => $"🔵 Low · {topic}",
        Priority.Min    => $"⚪ Min · {topic}",
        _               => topic, // Default: no prefix
    };

    // Toast Tag is limited to 64 chars
    private static string TruncateTag(string id) =>
        string.IsNullOrEmpty(id) ? string.Empty : (id.Length <= 64 ? id : id[..64]);

    private static string BuildAppActivationUrl(string topic, string messageId) =>
        $"{ProtocolRegistration.SCHEME}://show?topic={Uri.EscapeDataString(topic)}&msg={Uri.EscapeDataString(messageId)}";

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
