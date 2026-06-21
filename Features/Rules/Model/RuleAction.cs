namespace NtfyDesktop.Features.Rules.Model;

public enum RuleActionKind
{
    SuppressToast,
    Tag,
}

/// <summary>An action a matched rule applies. <see cref="Value"/> holds the tag
/// text for <see cref="RuleActionKind.Tag"/>; null otherwise.</summary>
public sealed record RuleAction(RuleActionKind Kind, string? Value = null);
