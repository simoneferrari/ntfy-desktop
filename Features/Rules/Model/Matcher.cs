using System.Text.RegularExpressions;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Rules.Model;

/// <summary>
/// A predicate over a received message. All set fields are ANDed; an all-null
/// matcher matches every message. Regex fields are case-insensitive substring
/// searches (use ^/$ to anchor). Compiled regexes are cached per instance.
/// </summary>
public sealed record Matcher
{
    public string? Topic { get; init; }
    public Priority? MinPriority { get; init; }
    public string? TitleRegex { get; init; }
    public string? BodyRegex { get; init; }
    public string? Tag { get; init; }

    private Regex? _titleRe;
    private Regex? _bodyRe;

    public bool Matches(NtfyMessage message) =>
        Matches(message.Topic, message.Title, message.Message, message.Priority, message.Tags);

    public bool Matches(string topic, string? title, string? body, Priority priority, IReadOnlyList<string>? tags)
    {
        if (Topic is not null &&
            !string.Equals(Topic, topic, StringComparison.OrdinalIgnoreCase))
            return false;

        if (MinPriority is { } min && priority < min)
            return false;

        if (TitleRegex is not null)
        {
            _titleRe ??= Compile(TitleRegex);
            if (title is null || !_titleRe.IsMatch(title)) return false;
        }

        if (BodyRegex is not null)
        {
            _bodyRe ??= Compile(BodyRegex);
            if (body is null || !_bodyRe.IsMatch(body)) return false;
        }

        if (Tag is not null &&
            (tags is null ||
             !tags.Any(t => string.Equals(t, Tag, StringComparison.OrdinalIgnoreCase))))
            return false;

        return true;
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
