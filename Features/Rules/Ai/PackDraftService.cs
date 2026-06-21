using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Ai;

public sealed record DraftResult(
    bool Ok, RulePack? Pack, string? Json, IReadOnlyList<string> Summary, string? Error);

/// <summary>
/// Orchestrates AI pack drafting: build the app-owned prompt, call the endpoint, extract
/// and validate the JSON, summarize. Never throws — failures come back as Ok=false.
/// </summary>
public sealed class PackDraftService(IChatClient client)
{
    public async Task<DraftResult> DraftAsync(
        IReadOnlyList<string> samples, string? intent, string? model, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await client.CompleteAsync(DraftPrompt.BuildMessages(samples, intent), model, ct);
        }
        catch (Exception ex)
        {
            return Fail($"The AI request failed: {ex.Message}");
        }

        var json = JsonExtraction.ExtractObject(raw);
        if (json is null) return Fail("The model didn't return any JSON. Try again or refine your intent.");

        RulePack pack;
        try { pack = PackParser.Parse(json); }
        catch (Exception ex) { return Fail($"The drafted pack wasn't valid JSON: {ex.Message}"); }

        if (pack.MatchRules.Count == 0 && pack.CorrelateRules.Count == 0 && pack.ExpectRules.Count == 0)
            return Fail("The model didn't produce any usable rules. Try adding more samples or an intent.");

        return new DraftResult(true, pack, json, PackSummarizer.Summarize(pack), null);
    }

    private static DraftResult Fail(string error) => new(false, null, null, [], error);
}
