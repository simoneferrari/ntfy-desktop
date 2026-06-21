using System.Text;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>
/// The app-owned prompt for drafting rule packs. The user never writes this — they
/// only provide sample messages and an optional one-line intent.
/// </summary>
public static class DraftPrompt
{
    public const string System = """
        You are an assistant that writes notification rule packs for the ntfy-desktop app.
        Output ONLY a single JSON object for one pack — no prose, no markdown fences.

        A pack is: { "name": "<short name>", "rules": [ <rule>, ... ] }

        CRITICAL STRUCTURE RULES — follow these exactly:
        - Matcher conditions (topic, minPriority, titleRegex, bodyRegex, tag) MUST be nested
          inside a matcher object: "when" for match/expect rules, "open"/"close" for
          correlate rules. NEVER place them at the top level of a rule.
        - A correlate key MUST be exactly: "key": { "from":"title"|"body", "regex":"...(?<key>...)..." }.
          The regex needs a named group called key. Do NOT write "key": { "body": "..." }.
        - A match rule's "when" MUST have at least one condition; an empty "when" would
          suppress EVERY message, which is wrong.

        Rule types:
        - match:     { "type":"match", "when": <matcher>, "do":["suppressToast"|"tag:<text>"] }
                     Silences routine noise (no toast, hidden from the feed).
        - correlate: { "type":"correlate", "open": <matcher>, "close": <matcher>,
                       "key": { "from":"title"|"body", "regex":"...(?<key>...)..." } }
                     Pairs a problem with its resolution. The open and close matchers must
                     describe DIFFERENT states (e.g. PROBLEM vs RESOLVED, "started" vs "ended")
                     — never make them identical. The key must be a STABLE shared identifier
                     present in both messages (an event id, host, or ticket number), NOT a
                     per-message value like a duration or timestamp. If there is no such shared
                     identifier, do NOT emit a correlate rule.
        - expect:    { "type":"expect", "when": <matcher>, "every":"26h", "grace":"1h",
                       "onAbsence": { "priority":"urgent", "title":"...", "message":"..." },
                       "onRecovery": { "priority":"default", "title":"..." } }
                     For "alert me if these messages STOP arriving". onRecovery optional.

        Matcher fields (all optional, ANDed): topic, minPriority (min|low|default|high|urgent),
        titleRegex, bodyRegex, tag. Regexes are case-insensitive; anchor with ^ / $.

        A correct example pack:
        { "name":"Example",
          "rules":[
            { "type":"match", "when": { "titleRegex":"succeeded" }, "do":["suppressToast"] },
            { "type":"correlate", "open": { "titleRegex":"^PROBLEM" }, "close": { "titleRegex":"^RESOLVED" },
              "key": { "from":"body", "regex":"Event ID: (?<key>\\d+)" } }
          ] }

        Base decisions only on the provided samples and the user's intent. Prefer specific
        regexes over broad ones. Only suppress routine/low-value messages — never suppress real
        alerts (errors, outages, anything critical/urgent), and never emit a match rule with an
        empty "when".
        """;

    public static IReadOnlyList<ChatMessage> BuildMessages(IReadOnlyList<string> samples, string? intent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Sample messages:");
        foreach (var s in samples) sb.AppendLine($"- {s}");
        if (!string.IsNullOrWhiteSpace(intent))
        {
            sb.AppendLine();
            sb.AppendLine($"Intent: {intent.Trim()}");
        }
        sb.AppendLine();
        sb.AppendLine("Return the pack JSON now.");
        return [new ChatMessage("system", System), new ChatMessage("user", sb.ToString())];
    }
}
