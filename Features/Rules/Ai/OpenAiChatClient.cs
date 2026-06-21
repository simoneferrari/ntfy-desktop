using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>IChatClient over an OpenAI-compatible /chat/completions endpoint. Config is
/// read per-call (via the supplied accessor) so Settings changes take effect without
/// re-registration.</summary>
public sealed class OpenAiChatClient(Func<(string BaseUrl, string Model, string ApiKey)> config) : IChatClient
{
    // Generous timeout: some providers/models (Gemini compat, reasoning models) take well
    // over a minute to return a full pack.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, string? model, CancellationToken ct)
    {
        var (baseUrl, cfgModel, apiKey) = config();
        var useModel = string.IsNullOrWhiteSpace(model) ? cfgModel : model;
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("No AI endpoint configured.");
        if (string.IsNullOrWhiteSpace(useModel)) throw new InvalidOperationException("No model selected.");

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = useModel,
                // Low temperature: pack drafting is structured extraction, not creative writing,
                // so we want consistent, faithful output rather than run-to-run variety.
                temperature = 0.2,
                messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            }),
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new("Bearer", apiKey);

        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}: {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? string.Empty;
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300];
}
