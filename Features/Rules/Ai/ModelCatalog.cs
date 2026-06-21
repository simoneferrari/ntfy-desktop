using System.Net.Http;
using System.Text.Json;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Fetches the live model list from a provider's /models endpoint. Returns an
/// empty list on any failure, so the caller falls back to a default/manual model.</summary>
public sealed class ModelCatalog
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<IReadOnlyList<string>> FetchAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return [];

            using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new("Bearer", apiKey);

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return [];

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return [];

            return data.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .OrderBy(s => s)
                .ToList();
        }
        catch { return []; }
    }
}
