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

    public void Show(NtfyMessage message, Guid topicId)
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

            var xml = BuildToastXml(title, body, message.Topic, message.Priority, message.Click, topicId,
                message.Id, message.Actions);
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

    /// <summary>
    /// One "N messages while you were away" toast summarising backfilled catch-up messages
    /// (see <see cref="BackfillSummaryNotifier"/>), rather than one toast per missed message.
    /// Clicking it opens the single topic, or the combined feed when several topics are involved.
    /// </summary>
    public void ShowBackfillSummary(int total, IReadOnlyList<(string Label, int Count)> topics, Guid clickTopicId)
    {
        if (_notifier == null || total <= 0) return;

        try
        {
            var (title, body) = FormatSummary(total, topics);
            var launchUrl = BuildAppActivationUrl(clickTopicId);

            var doc = new XmlDocument();
            doc.LoadXml($"""
                <toast activationType="protocol" launch="{EscapeXml(launchUrl)}">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{EscapeXml(title)}</text>
                      <text>{EscapeXml(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """);

            var toast = new ToastNotification(doc)
            {
                // Fixed group/tag so a newer catch-up wave replaces the previous summary
                // rather than stacking another bubble.
                Group = "ntfy-desktop-summary",
                Tag = "backfill-summary",
            };

            _notifier.Show(toast);
        }
        catch { /* toast delivery failure is non-fatal */ }
    }

    private static (string Title, string Body) FormatSummary(int total, IReadOnlyList<(string Label, int Count)> topics)
    {
        var title = $"{total} {(total == 1 ? "message" : "messages")} while you were away";

        if (topics.Count == 1)
            return (title, $"in {topics[0].Label}");

        const int max = 4;
        var body = string.Join(", ", topics.Take(max).Select(t => $"{t.Label} ({t.Count})"));
        if (topics.Count > max)
            body += $", +{topics.Count - max} more";

        return (title, body);
    }

    private static XmlDocument BuildToastXml(string title, string body, string topic, Priority priority, string? clickUrl, Guid topicId,
        string messageId, IReadOnlyList<NtfyAction>? actions)
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
            : BuildAppActivationUrl(topicId);
        var clickAttrs = $@" activationType=""protocol"" launch=""{EscapeXml(launchUrl)}""";

        var audioElement = priority switch
        {
            Priority.Urgent => @"<audio src=""ms-winsoundevent:Notification.Looping.Alarm"" loop=""true"" />",
            Priority.High   => @"<audio src=""ms-winsoundevent:Notification.Looping.Call"" />",
            _               => string.Empty,
        };

        var attribution = FormatAttribution(topic, priority);

        // Action buttons. Schema order is visual → actions → audio.
        var actionsElement = BuildActionsXml(messageId, actions);

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
              {actionsElement}
              {audioElement}
            </toast>
            """);
        return doc;
    }

    // Renders up to three message action buttons (Windows allows 5; ntfy caps at 3).
    // `view` launches its URL directly via protocol activation (browser); `http`/`copy`
    // launch us back through ntfy-desktop://action?msg=&i= so the action is looked up in
    // history and run (with confirmation for http) by MessageActionInvoker. broadcast and
    // any unsupported/unsafe action are skipped. Returns "" when there's nothing to render.
    private static string BuildActionsXml(string messageId, IReadOnlyList<NtfyAction>? actions)
    {
        if (actions is not { Count: > 0 }) return string.Empty;

        var buttons = new List<string>();
        for (var i = 0; i < actions.Count && buttons.Count < 3; i++)
        {
            var action = actions[i];
            if (!action.IsSupported) continue;

            // All buttons activate through our own scheme rather than launching the URL
            // directly: a toast-activated app gets the foreground grant, so when *it* opens
            // the browser the page comes to the foreground (a raw URL handed to the shell
            // does not). view carries its own URL (self-contained, no history needed);
            // http/copy carry the message id + this action's index for the app to resolve.
            var arguments = action.IsView
                ? $"{ProtocolRegistration.SCHEME}://view?url={Uri.EscapeDataString(action.Url!)}"
                : $"{ProtocolRegistration.SCHEME}://action?msg={Uri.EscapeDataString(messageId)}&i={i}";

            var label = string.IsNullOrWhiteSpace(action.Label) ? "Open" : action.Label;

            buttons.Add(
                $"""<action content="{EscapeXml(label)}" activationType="protocol" arguments="{EscapeXml(arguments)}" />""");
        }

        return buttons.Count == 0 ? string.Empty : $"<actions>{string.Join("", buttons)}</actions>";
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

    private static string BuildAppActivationUrl(Guid topicId) =>
        $"{ProtocolRegistration.SCHEME}://show?topic={topicId}";

    private static string EscapeXml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}
