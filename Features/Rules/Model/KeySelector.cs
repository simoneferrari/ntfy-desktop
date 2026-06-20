using System.Text.RegularExpressions;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Rules.Model;

public enum KeyField { Title, Body }

/// <summary>
/// Extracts a correlation key from a message via a regex over its title or body.
/// Prefers a named capture group called "key"; otherwise uses capture group 1.
/// Returns null when the source field is null or the pattern doesn't match.
/// </summary>
public sealed record KeySelector
{
    public KeyField From { get; init; }
    public string Regex { get; init; } = string.Empty;

    private System.Text.RegularExpressions.Regex? _re;

    public string? Extract(NtfyMessage message)
    {
        var source = From == KeyField.Title ? message.Title : message.Message;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(Regex)) return null;

        _re ??= new Regex(Regex, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        var match = _re.Match(source);
        if (!match.Success) return null;

        var named = match.Groups["key"];
        if (named.Success) return named.Value;
        return match.Groups.Count > 1 ? match.Groups[1].Value : null;
    }
}
