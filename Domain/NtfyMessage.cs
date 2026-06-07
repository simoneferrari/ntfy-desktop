using System.Text.Json.Serialization;

namespace NtfyDesktop.Domain;

public sealed record NtfyMessage
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("time")]
    public long Time { get; init; }

    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    [JsonPropertyName("priority")]
    public Priority Priority { get; init; } = Priority.Default;

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; init; }

    [JsonPropertyName("click")]
    public string? Click { get; init; }

    [JsonPropertyName("attachment")]
    public NtfyAttachment? Attachment { get; init; }

    [JsonPropertyName("actions")]
    public List<NtfyAction>? Actions { get; init; }

    [JsonPropertyName("expires")]
    public long? Expires { get; init; }

    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeSeconds(Time);
}
