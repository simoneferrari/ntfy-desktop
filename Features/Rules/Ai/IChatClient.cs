namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Minimal chat-completion seam over an OpenAI-compatible endpoint.
/// Returns the assistant's raw text content. <paramref name="model"/> overrides the
/// configured model for this call (null/empty = use the configured one).</summary>
public interface IChatClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, string? model, CancellationToken ct);
}
