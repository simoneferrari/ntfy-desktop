# Notification Rule Engine — Phase 1c Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Let the user generate rule packs with AI — pick sample messages + an optional one-line intent, and the app (which owns the prompt) drafts a validated pack to review, edit, and save. Plus the `RulesEnabled` master toggle and AI endpoint configuration with overridable provider presets and a live model list.

**Architecture:** A `PackDraftService` orchestrates *build request → call `IChatClient` → extract/parse/validate → summarize*. The app supplies a built-in, schema-aware **system prompt**; the user supplies samples + intent. `IChatClient` wraps an OpenAI-compatible `/v1/chat/completions` call (real HTTP impl isolated behind the interface). Provider base URLs come from an overridable `providers.json`; model lists are fetched live from the provider's `/v1/models`. A WPF dialog drives the flow; Settings gains endpoint config + the `RulesEnabled` toggle.

**Tech Stack:** .NET 10 / WPF + WPF-UI, `HttpClient` (precedent: `AttachmentImageService`), `System.Text.Json`, `CommunityToolkit.Mvvm`, xUnit.

## Scope

Phase 1c, on `feature/notification-rule-engine`. The deterministic core (prompt assembly, response extraction, pack summarizer, provider-preset parsing, the `PackDraftService` orchestration against a fake `IChatClient`) is TDD'd. The HTTP `IChatClient`, the Settings fields, and the draft dialog are build- + running-app-verified (held for the maintainer's test, per protocol).

**Deferred:** Phase 2 (full rule-builder UI).

## Design decisions (confirmed)

- Samples: **pick from history** (checklist of a topic's recent messages) + paste fallback.
- Steering: optional **one-line intent** + auto; the app owns the system prompt.
- Review: **plain-English summary + editable JSON** before save.
- Entry points: **per-topic rail menu** "Draft rules from this topic…" + **Settings** "Draft rules with AI…".
- Providers: **overridable `providers.json`** (base URLs) + **live `/v1/models`** + **Custom**; graceful fallback to a preset default model / manual field.
- `RulesEnabled` toggle ships here.

## Global Constraints

(Same as prior phases.) `net10.0-windows10.0.17763.0`; feature-isolated under `Features/Rules/`. Secrets DPAPI-wrapped via `TokenProtector`; `PasswordBox` isn't bindable — pump it manually with a suspend flag (see `SettingsPage.xaml.cs` token pattern). Settings preferences use snapshot dirty-tracking; bindings use `UpdateSourceTrigger=PropertyChanged`. WPF-UI control styles must use `BasedOn`. Engine/AI **fail soft** — a bad endpoint or unparseable response surfaces an error, never crashes. Nothing is sent to any endpoint unless the user clicks Generate. Build: `dotnet build NtfyDesktop.csproj`. Test: `dotnet test`. One commit per task; UI/HTTP commits held until the maintainer confirms in the running app.

## File Structure

**New (testable core):**
- `Features/Rules/Ai/ChatMessage.cs` — `record ChatMessage(string Role, string Content)`.
- `Features/Rules/Ai/IChatClient.cs` — `Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)`.
- `Features/Rules/Ai/DraftPrompt.cs` — the built-in system prompt + `BuildMessages(samples, intent)`.
- `Features/Rules/Ai/JsonExtraction.cs` — pull a JSON object out of model text (strip fences/prose).
- `Features/Rules/Ai/PackSummarizer.cs` — `RulePack` → plain-English lines.
- `Features/Rules/Ai/PackDraftService.cs` — orchestration; `DraftResult` (ok+pack+json+summary | error).
- `Features/Rules/Ai/Providers.cs` — `ProviderPreset` record + `ProviderPresets` loader (overridable `providers.json`).

**New (integration / UI):**
- `Features/Rules/Ai/OpenAiChatClient.cs` — `IChatClient` over `/v1/chat/completions`.
- `Features/Rules/Ai/ModelCatalog.cs` — live `GET /v1/models` fetch (+ fallback).
- `Features/Rules/Ai/DraftRulesDialog.xaml(.cs)` + `DraftRulesViewModel.cs` — the dialog.
- `assets/providers.json` (bundled default, copied to `App.DataPath` on first run).

**Modified:**
- `Features/Settings/AppSettings.cs` — AI endpoint fields (base URL, model, DPAPI key) + helpers.
- `Features/Settings/SettingsViewModel.cs` / `SettingsPage.xaml(.cs)` — endpoint config + `RulesEnabled` toggle + "Draft rules with AI…" button.
- `Features/Rules/PackStore.cs` — `Save(name, json)` helper (write file + `Reload`).
- `Features/Rules/RulesFeature.cs` — register `IChatClient`, `PackDraftService`, `ModelCatalog`, `ProviderPresets`, dialog VM.
- `Features/Shell/MainWindow.xaml(.cs)` — per-topic "Draft rules from this topic…" menu item.

---

## Task C1: AppSettings — AI endpoint fields + RulesEnabled persistence

**Files:** Modify `Features/Settings/AppSettings.cs`. (`RulesEnabled` already exists from Phase 1a.)

**Interfaces:** Produces `AiBaseUrl` (string), `AiModel` (string), `GetAiApiKey()` / `SetAiApiKey(string)` (DPAPI via `TokenProtector`, mirroring the access-token methods).

- [ ] **Step 1:** Find the access-token storage in `AppSettings.cs` (`GetAccessToken`/`SetAccessToken` + the DPAPI-wrapped backing field). Add parallel members for the AI key, plus plain properties for base URL + model:

```csharp
public string AiBaseUrl { get; set; } = string.Empty;
public string AiModel { get; set; } = string.Empty;

// DPAPI-wrapped at rest, exactly like the access token.
[JsonPropertyName("aiApiKeyProtected")]
public string? AiApiKeyProtected { get; set; }

public string GetAiApiKey() => TokenProtector.Unprotect(AiApiKeyProtected);
public void SetAiApiKey(string? value) => AiApiKeyProtected = TokenProtector.Protect(value);
```

(Match the exact `TokenProtector` call shape used by `GetAccessToken`/`SetAccessToken` in this file — adjust names if they differ.)

- [ ] **Step 2:** Build — `dotnet build NtfyDesktop.csproj` succeeds.
- [ ] **Step 3:** Commit — `git commit -m "feat(rules): add AI endpoint settings storage"`

---

## Task C2: Provider presets (overridable providers.json)

**Files:** Create `Features/Rules/Ai/Providers.cs`; `assets/providers.json`; Test `NtfyDesktop.Tests/Rules/ProviderPresetsTests.cs`. Modify `NtfyDesktop.csproj` (bundle the asset).

**Interfaces:**
- `record ProviderPreset(string Name, string BaseUrl, string? DefaultModel)`.
- `ProviderPresets(string filePath)` with `IReadOnlyList<ProviderPreset> All` and `void EnsureSeeded(string bundledJson)` — writes the bundled default to `filePath` if absent, then loads. Invalid file → empty list (fail soft). Always-present synthetic `Custom` entry is added by the VM, not the file.

- [ ] **Step 1: Write failing tests**

```csharp
using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class ProviderPresetsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;
    public ProviderPresetsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfyprov_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "providers.json");
    }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void EnsureSeeded_WritesBundled_WhenAbsent()
    {
        const string bundled = """[{"name":"OpenAI","baseUrl":"https://api.openai.com/v1","defaultModel":"gpt-4o"}]""";
        var p = new ProviderPresets(_file);
        p.EnsureSeeded(bundled);
        Assert.True(File.Exists(_file));
        var preset = Assert.Single(p.All);
        Assert.Equal("OpenAI", preset.Name);
        Assert.Equal("https://api.openai.com/v1", preset.BaseUrl);
        Assert.Equal("gpt-4o", preset.DefaultModel);
    }

    [Fact]
    public void EnsureSeeded_DoesNotOverwriteExisting()
    {
        File.WriteAllText(_file, """[{"name":"Mine","baseUrl":"http://x/v1"}]""");
        var p = new ProviderPresets(_file);
        p.EnsureSeeded("""[{"name":"OpenAI","baseUrl":"https://api.openai.com/v1"}]""");
        Assert.Equal("Mine", Assert.Single(p.All).Name);
    }

    [Fact]
    public void InvalidFile_YieldsEmpty()
    {
        File.WriteAllText(_file, "not json");
        var p = new ProviderPresets(_file);
        p.EnsureSeeded("[]");
        Assert.Empty(p.All);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement `Features/Rules/Ai/Providers.cs`**

```csharp
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NtfyDesktop.Features.Rules.Ai;

public sealed record ProviderPreset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("defaultModel")] string? DefaultModel);

/// <summary>
/// Loads provider presets from an overridable providers.json (so base URLs can be
/// changed without an app release). Fails soft to an empty list.
/// </summary>
public sealed class ProviderPresets
{
    private readonly string _filePath;

    public ProviderPresets(string filePath) => _filePath = filePath;

    public IReadOnlyList<ProviderPreset> All { get; private set; } = [];

    public void EnsureSeeded(string bundledJson)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (!File.Exists(_filePath))
                File.WriteAllText(_filePath, bundledJson);
            All = JsonSerializer.Deserialize<List<ProviderPreset>>(File.ReadAllText(_filePath)) ?? [];
        }
        catch
        {
            All = [];
        }
    }
}
```

- [ ] **Step 4: Create `assets/providers.json`** (the bundled default; OpenAI-compatible base URLs):

```json
[
  { "name": "OpenAI", "baseUrl": "https://api.openai.com/v1", "defaultModel": "gpt-4o" },
  { "name": "Anthropic", "baseUrl": "https://api.anthropic.com/v1", "defaultModel": "claude-3-5-sonnet-latest" },
  { "name": "Google Gemini", "baseUrl": "https://generativelanguage.googleapis.com/v1beta/openai", "defaultModel": "gemini-1.5-pro" },
  { "name": "Ollama (local)", "baseUrl": "http://localhost:11434/v1", "defaultModel": "" }
]
```

- [ ] **Step 5: Bundle the asset** — in `NtfyDesktop.csproj`, add to an `<ItemGroup>` (so it ships and can be read at runtime via the app base dir):

```xml
    <Content Include="assets\providers.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
```

- [ ] **Step 6: Run — expect PASS.**
- [ ] **Step 7: Commit** — `git commit -m "feat(rules): add overridable provider presets"`

---

## Task C3: Built-in prompt assembly

**Files:** Create `Features/Rules/Ai/ChatMessage.cs`, `Features/Rules/Ai/IChatClient.cs`, `Features/Rules/Ai/DraftPrompt.cs`; Test `NtfyDesktop.Tests/Rules/DraftPromptTests.cs`.

**Interfaces:**
- `record ChatMessage(string Role, string Content)`.
- `interface IChatClient { Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct); }`.
- `static class DraftPrompt { const string System; IReadOnlyList<ChatMessage> BuildMessages(IReadOnlyList<string> samples, string? intent); }` — returns a system message (the built-in prompt) + one user message embedding the samples and intent.

- [ ] **Step 1: Write failing tests**

```csharp
using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class DraftPromptTests
{
    [Fact]
    public void BuildMessages_IncludesSystemThenUser_WithSamplesAndIntent()
    {
        var msgs = DraftPrompt.BuildMessages(
            ["PROBLEM #7 disk full", "RESOLVED #7 disk ok"],
            "pair problems with resolutions by the #number");

        Assert.Equal(2, msgs.Count);
        Assert.Equal("system", msgs[0].Role);
        Assert.Contains("correlate", msgs[0].Content); // schema/rule-type guidance present
        Assert.Equal("user", msgs[1].Role);
        Assert.Contains("PROBLEM #7", msgs[1].Content);
        Assert.Contains("pair problems", msgs[1].Content);
    }

    [Fact]
    public void BuildMessages_NoIntent_StillValid()
    {
        var msgs = DraftPrompt.BuildMessages(["x"], null);
        Assert.Equal(2, msgs.Count);
        Assert.Contains("x", msgs[1].Content);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement the three files**

`ChatMessage.cs`:
```csharp
namespace NtfyDesktop.Features.Rules.Ai;

public sealed record ChatMessage(string Role, string Content);
```

`IChatClient.cs`:
```csharp
namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Minimal chat-completion seam over an OpenAI-compatible endpoint.
/// Returns the assistant's raw text content.</summary>
public interface IChatClient
{
    Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
}
```

`DraftPrompt.cs`:
```csharp
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

        Pack shape:
        { "name": "<short name>", "rules": [ <rule>, ... ] }

        Rule types:
        - match:     { "type":"match", "when": <matcher>, "do":["suppressToast"|"tag:<text>"] }
                     Use to silence routine noise (no toast, hidden from the feed).
        - correlate: { "type":"correlate", "open": <matcher>, "close": <matcher>,
                       "key": { "from":"title"|"body", "regex":"...(?<key>...)..." } }
                     Pairs a problem with its resolution. BOTH must contain the SAME key
                     (a named group "key"); without a shared key, correlation cannot pair.
        - expect:    { "type":"expect", "when": <matcher>, "every":"26h", "grace":"1h",
                       "onAbsence": { "priority":"urgent", "title":"...", "message":"..." },
                       "onRecovery": { "priority":"default", "title":"..." } }
                     Use for "alert me if these messages STOP arriving". onRecovery optional.

        Matcher fields (all optional, ANDed): topic, minPriority (min|low|default|high|urgent),
        titleRegex, bodyRegex, tag. Regexes are case-insensitive; anchor with ^ / $.

        Base decisions only on the provided samples and the user's intent. Prefer specific
        regexes over broad ones. If unsure a correlation key exists, do not emit a correlate rule.
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
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add AI prompt assembly + chat seam"`

---

## Task C4: Response JSON extraction

**Files:** Create `Features/Rules/Ai/JsonExtraction.cs`; Test `NtfyDesktop.Tests/Rules/JsonExtractionTests.cs`.

**Interfaces:** `static class JsonExtraction { static string? ExtractObject(string text); }` — returns the first balanced `{...}` JSON object (tolerating ```` ```json ```` fences or leading prose), else null.

- [ ] **Step 1: Write failing tests**

```csharp
using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class JsonExtractionTests
{
    [Fact]
    public void Extract_Bare() => Assert.Equal("""{"a":1}""", JsonExtraction.ExtractObject("""{"a":1}"""));

    [Fact]
    public void Extract_FromFence() =>
        Assert.Equal("""{"a":1}""", JsonExtraction.ExtractObject("```json\n{\"a\":1}\n```"));

    [Fact]
    public void Extract_FromProse() =>
        Assert.Equal("""{"x":{"y":2}}""", JsonExtraction.ExtractObject("""Here you go: {"x":{"y":2}} done"""));

    [Fact]
    public void Extract_NoObject_ReturnsNull() => Assert.Null(JsonExtraction.ExtractObject("no json here"));
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

```csharp
namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Pulls the first balanced JSON object out of a model response that may be
/// wrapped in markdown fences or surrounded by prose.</summary>
public static class JsonExtraction
{
    public static string? ExtractObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): extract JSON object from model responses"`

---

## Task C5: PackSummarizer

**Files:** Create `Features/Rules/Ai/PackSummarizer.cs`; Test `NtfyDesktop.Tests/Rules/PackSummarizerTests.cs`.

**Interfaces:** `static class PackSummarizer { static IReadOnlyList<string> Summarize(RulePack pack); }` — one plain-English line per rule.

- [ ] **Step 1: Write failing tests**

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Ai;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackSummarizerTests
{
    [Fact]
    public void Summarize_CoversEachRuleType()
    {
        var pack = new RulePack("P",
            [new MatchRule(new Matcher { TitleRegex = "succeeded" }, [new RuleAction(RuleActionKind.SuppressToast)])],
            [new CorrelateRule("P#1", new Matcher { TitleRegex = "^PROBLEM" }, new Matcher { TitleRegex = "^RESOLVED" },
                new KeySelector { From = KeyField.Title, Regex = "#(?<key>\\d+)" })],
            [new ExpectRule("P#2", new Matcher { Topic = "backups" }, TimeSpan.FromHours(26), TimeSpan.FromHours(1),
                new AlertSpec(Priority.Urgent, "missed", null), null)]);

        var lines = PackSummarizer.Summarize(pack);
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Contains("Suppress") && l.Contains("succeeded"));
        Assert.Contains(lines, l => l.Contains("Pair") || l.Contains("Correlate"));
        Assert.Contains(lines, l => l.Contains("Alert") && l.Contains("26"));
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

```csharp
using System.Text;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Turns a parsed pack into human-readable lines for the review step.</summary>
public static class PackSummarizer
{
    public static IReadOnlyList<string> Summarize(RulePack pack)
    {
        var lines = new List<string>();

        foreach (var r in pack.MatchRules)
            lines.Add($"Suppress messages where {Describe(r.When)} (no toast, hidden from feed).");

        foreach (var r in pack.CorrelateRules)
            lines.Add($"Pair: open when {Describe(r.Open)}, close when {Describe(r.Close)}, " +
                      $"matched by the key from the {r.Key.From.ToString().ToLowerInvariant()}; " +
                      "both toast, then fold out of the feed.");

        foreach (var r in pack.ExpectRules)
            lines.Add($"Alert ({r.OnAbsence.Title}) if no message where {Describe(r.When)} " +
                      $"arrives within {(int)r.Every.TotalHours}h (+{(int)r.Grace.TotalMinutes}m grace)" +
                      (r.OnRecovery is null ? "." : "; notify on recovery."));

        return lines;
    }

    private static string Describe(Matcher m)
    {
        var parts = new List<string>();
        if (m.Topic is not null) parts.Add($"topic = {m.Topic}");
        if (m.MinPriority is { } p) parts.Add($"priority ≥ {p}");
        if (m.TitleRegex is not null) parts.Add($"title ~ /{m.TitleRegex}/");
        if (m.BodyRegex is not null) parts.Add($"body ~ /{m.BodyRegex}/");
        if (m.Tag is not null) parts.Add($"tagged '{m.Tag}'");
        return parts.Count == 0 ? "any message" : string.Join(" and ", parts);
    }
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add pack summarizer"`

---

## Task C6: PackDraftService orchestration

**Files:** Create `Features/Rules/Ai/PackDraftService.cs`; Test `NtfyDesktop.Tests/Rules/PackDraftServiceTests.cs` (+ a `FakeChatClient`).

**Interfaces:**
- `record DraftResult(bool Ok, RulePack? Pack, string? Json, IReadOnlyList<string> Summary, string? Error)`.
- `PackDraftService(IChatClient client)` with `Task<DraftResult> DraftAsync(IReadOnlyList<string> samples, string? intent, CancellationToken ct)`.
- Flow: `DraftPrompt.BuildMessages` → `client.CompleteAsync` → `JsonExtraction.ExtractObject` → `PackParser.Parse` → `PackSummarizer.Summarize`. Any failure (no JSON, parse throws, empty rules, client throws) → `Ok=false` with a message. Never throws.

- [ ] **Step 1: Write failing tests**

```csharp
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Ai;

namespace NtfyDesktop.Tests.Rules;

public class PackDraftServiceTests
{
    private sealed class FakeChatClient(string response) : IChatClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> m, CancellationToken ct) =>
            Task.FromResult(response);
    }

    [Fact]
    public async Task DraftAsync_ValidResponse_ReturnsPackAndSummary()
    {
        const string resp = """
            ```json
            { "name":"AI","rules":[ {"type":"match","when":{"titleRegex":"succeeded"},"do":["suppressToast"]} ] }
            ```
            """;
        var result = await new PackDraftService(new FakeChatClient(resp)).DraftAsync(["x"], null, default);

        Assert.True(result.Ok);
        Assert.NotNull(result.Pack);
        Assert.Single(result.Pack!.MatchRules);
        Assert.NotEmpty(result.Summary);
        Assert.Contains("{", result.Json!);
    }

    [Fact]
    public async Task DraftAsync_NoJson_ReturnsError()
    {
        var result = await new PackDraftService(new FakeChatClient("sorry, no")).DraftAsync(["x"], null, default);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DraftAsync_EmptyPack_ReturnsError()
    {
        var result = await new PackDraftService(new FakeChatClient("""{"name":"x","rules":[]}"""))
            .DraftAsync(["x"], null, default);
        Assert.False(result.Ok);
    }

    [Fact]
    public async Task DraftAsync_ClientThrows_ReturnsError()
    {
        var throwing = new ThrowingClient();
        var result = await new PackDraftService(throwing).DraftAsync(["x"], null, default);
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    private sealed class ThrowingClient : IChatClient
    {
        public Task<string> CompleteAsync(IReadOnlyList<ChatMessage> m, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

```csharp
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
        IReadOnlyList<string> samples, string? intent, CancellationToken ct)
    {
        string raw;
        try
        {
            raw = await client.CompleteAsync(DraftPrompt.BuildMessages(samples, intent), ct);
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
```

- [ ] **Step 4: Run — expect PASS.** Then the full suite — `dotnet test` → all green.
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add PackDraftService orchestration"`

---

## Task C7: PackStore.Save (write + reload)

**Files:** Modify `Features/Rules/PackStore.cs`; Test `NtfyDesktop.Tests/Rules/PackStoreSaveTests.cs`.

**Interfaces:** `string Save(string suggestedName, string json)` — writes `<sanitized-name>.json` into the packs dir (suffixing to avoid clobber), calls `Reload()`, returns the file path.

- [ ] **Step 1: Write failing test**

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackStoreSaveTests : IDisposable
{
    private readonly string _dir;
    public PackStoreSaveTests() => _dir = Path.Combine(Path.GetTempPath(), "ntfysave_" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Save_WritesFile_AndReloads()
    {
        var store = new PackStore(_dir);
        var path = store.Save("AI Backups",
            """{ "name":"AI Backups","rules":[{"type":"match","when":{"topic":"x"},"do":["suppressToast"]}] }""");

        Assert.True(File.Exists(path));
        Assert.Single(store.Packs);
        Assert.Equal("AI Backups", store.Packs[0].Name);
    }

    [Fact]
    public void Save_Twice_DoesNotClobber()
    {
        var store = new PackStore(_dir);
        store.Save("dup", """{ "name":"dup","rules":[] }""");
        store.Save("dup", """{ "name":"dup","rules":[] }""");
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement** — add to `PackStore`:

```csharp
    /// <summary>Writes a pack JSON to the packs directory (unique filename) and reloads.
    /// Returns the written path.</summary>
    public string Save(string suggestedName, string json)
    {
        Directory.CreateDirectory(_directory);

        var slug = new string((suggestedName ?? "pack")
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())
            .Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "pack";

        var path = Path.Combine(_directory, slug + ".json");
        var n = 1;
        while (File.Exists(path))
            path = Path.Combine(_directory, $"{slug}-{n++}.json");

        File.WriteAllText(path, json);
        Reload();
        return path;
    }
```

(Requires `using System.IO;` and `using System.Linq;` in the file — add if missing.)

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add PackStore.Save with reload"`

---

## Task C8: OpenAiChatClient + ModelCatalog (HTTP)

Integration — **build-verified here, exercised in Task C11's running-app test.**

**Files:** Create `Features/Rules/Ai/OpenAiChatClient.cs`, `Features/Rules/Ai/ModelCatalog.cs`.

**Interfaces:**
- `OpenAiChatClient(Func<(string BaseUrl, string Model, string ApiKey)> config) : IChatClient` — POSTs `{base}/chat/completions` with `{ model, messages }`, `Authorization: Bearer <key>`; returns `choices[0].message.content`.
- `ModelCatalog` — `Task<IReadOnlyList<string>> FetchAsync(string baseUrl, string apiKey, CancellationToken ct)` → `GET {base}/models`, returns `data[].id` sorted; empty list on any failure (caller falls back).

- [ ] **Step 1: Implement `OpenAiChatClient.cs`**

```csharp
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>IChatClient over an OpenAI-compatible /chat/completions endpoint. Config is
/// read per-call (via the supplied accessor) so Settings changes take effect without
/// re-registration.</summary>
public sealed class OpenAiChatClient(Func<(string BaseUrl, string Model, string ApiKey)> config) : IChatClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<string> CompleteAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var (baseUrl, model, apiKey) = config();
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("No AI endpoint configured.");
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("No model selected.");

        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model,
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
```

- [ ] **Step 2: Implement `ModelCatalog.cs`**

```csharp
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
```

- [ ] **Step 3: Build** — `dotnet build NtfyDesktop.csproj` succeeds.
- [ ] **Step 4: Commit (held)** — `git commit -m "feat(rules): add OpenAI-compatible chat client + model catalog"` *(commit after C11 verification, per protocol).*

---

## Task C9: DI registration

**Files:** Modify `Features/Rules/RulesFeature.cs`.

- [ ] **Step 1: Register the AI services** in `AddRules()`:

```csharp
            services.AddSingleton<ProviderPresets>(_ =>
            {
                var presets = new ProviderPresets(Path.Combine(App.DataPath, "providers.json"));
                var bundled = Path.Combine(AppContext.BaseDirectory, "assets", "providers.json");
                presets.EnsureSeeded(File.Exists(bundled) ? File.ReadAllText(bundled) : "[]");
                return presets;
            });

            services.AddSingleton<ModelCatalog>();

            services.AddSingleton<IChatClient>(sp =>
            {
                var settings = sp.GetRequiredService<AppSettings>();
                return new OpenAiChatClient(() => (settings.AiBaseUrl, settings.AiModel, settings.GetAiApiKey()));
            });

            services.AddSingleton<PackDraftService>();
            services.AddTransient<DraftRulesViewModel>();
```

Add `using NtfyDesktop.Features.Rules.Ai;` and `using System;` (for `AppContext`) as needed.

- [ ] **Step 2: Build** — succeeds (after Task C10 the VM type exists; if building before C10, temporarily omit the `DraftRulesViewModel` line, then add it in C10).
- [ ] **Step 3: Commit (held)** — `git commit -m "feat(rules): register AI drafting services"`

---

## Task C10: Settings UI — endpoint config + RulesEnabled toggle

WPF — **build- + running-app-verified.**

**Files:** Modify `Features/Settings/SettingsViewModel.cs`, `Features/Settings/SettingsPage.xaml`, `Features/Settings/SettingsPage.xaml.cs`.

- [ ] **Step 1: ViewModel** — add observable properties and load/save, following the existing snapshot pattern. Add `RulesEnabled` (bool), `AiProviderName` (string, the selected preset or "Custom"), `AiBaseUrl`, `AiModel`, an `ObservableCollection<string> AiModels`, and an `AiApiKey` shadow (not snapshotted — pumped from `PasswordBox`). Inject `ProviderPresets` + `ModelCatalog`. In `Load()` seed from `AppSettings`; in `SaveAsync()` write back (`_settings.RulesEnabled = RulesEnabled; _settings.AiBaseUrl = …; _settings.AiModel = …; _settings.SetAiApiKey(AiApiKey);`). Add `RulesEnabled` and the AI string fields to `FormSnapshot`/`TakeSnapshot` (key excluded — persisted on save like the token). Add a `[RelayCommand] RefreshModelsAsync()` that calls `ModelCatalog.FetchAsync` and fills `AiModels` (fallback: keep the preset default / current value). Add an `ObservableCollection<string> AiProviders` from `ProviderPresets.All` plus `"Custom"`; selecting one sets `AiBaseUrl`/`AiModel` from the preset.

- [ ] **Step 2: SettingsPage.xaml** — add a "Notification rules" card: a `ui:ToggleSwitch` bound to `RulesEnabled`; a provider `ComboBox` (`AiProviders`, `AiProviderName`); a base-URL `TextBox` (`AiBaseUrl`, enabled when Custom); a model row = editable `ComboBox` (`AiModels`, `AiModel`) + a "Refresh models" `ui:Button` (`RefreshModelsCommand`); a `PasswordBox` for the key (manual sync — see Step 3); and a "Draft rules with AI…" `ui:Button` (opens the dialog, Task C11). Bindings use `UpdateSourceTrigger=PropertyChanged`; styles `BasedOn` WPF-UI.

- [ ] **Step 3: SettingsPage.xaml.cs** — pump the AI key `PasswordBox` ↔ `_vm.AiApiKey` with a suspend flag, mirroring the existing access-token box. On load set `.Password` from `_vm.AiApiKey`; on `PasswordChanged` write back.

- [ ] **Step 4: Build + run.** Verify: the card renders; selecting OpenAI fills the base URL + default model; "Refresh models" populates the dropdown when a valid key is set (and silently no-ops otherwise); Save persists (reopen Settings shows values; key round-trips); toggling `RulesEnabled` off then publishing a matching message → no suppression (engine dormant).
- [ ] **Step 5: Commit (held)** — `git commit -m "feat(settings): AI endpoint config + rules master toggle"`

---

## Task C11: Draft-rules dialog + entry points

WPF — **build- + running-app-verified (the end-to-end gate).**

**Files:** Create `Features/Rules/Ai/DraftRulesViewModel.cs`, `DraftRulesDialog.xaml(.cs)`; Modify `Features/Shell/MainWindow.xaml(.cs)` (topic menu) and the Settings "Draft rules…" button (Task C10).

- [ ] **Step 1: DraftRulesViewModel** — ctor takes `HistoryRepository`, `PackDraftService`, `PackStore`, and an optional initial `Guid? topicId`. State: `ObservableCollection<SampleVm>` (recent messages for the topic via `HistoryRepository.Query(topicId, limit: 50)`, each with an `IsSelected` + display text), a paste `TextBox` string, an `Intent` string, `IsBusy`, `ErrorText`, `SummaryLines`, `DraftJson` (editable), and `CanSave`. Commands: `GenerateAsync` (gather selected samples + non-empty paste lines → `PackDraftService.DraftAsync` → populate summary + JSON or error), `Save` (`PackStore.Save(pack.Name, DraftJson)` then close), `Cancel`. Re-validate `DraftJson` with `PackParser` on save (user may have edited it) — show an error instead of saving if invalid.

- [ ] **Step 2: DraftRulesDialog.xaml(.cs)** — a `Window`/WPF-UI dialog with `DialogResult` (mirror `TopicEditorDialog`): samples checklist, paste box, intent box, Generate button (disabled while `IsBusy`), an error `TextBlock`, the summary list, the editable JSON `TextBox`, and Save/Cancel. Register the XAML in `NtfyDesktop.csproj` `<Page>` items like the other dialogs.

- [ ] **Step 3: Entry points.**
  - Settings "Draft rules with AI…" button → resolve `DraftRulesViewModel` (no topic), show the dialog.
  - `MainWindow.xaml(.cs)` topic three-dot menu → add "Draft rules from this topic…" → resolve the VM with that `topicId`, show the dialog. Follow the existing context-menu item pattern (e.g. the pause/move items).

- [ ] **Step 4: Build + run — end-to-end.** With a real endpoint configured (Task C10): open from a topic's menu → its recent messages are listed → select a few, add an intent → Generate → a summary + JSON appear → Save → the pack file lands in `…\rules\` and takes effect immediately (verify a drafted suppress/correlate/expect rule actually fires per the earlier phases' manual tests). Also verify error paths (bad key, garbage response) show a message rather than crashing.
- [ ] **Step 5: Commit (held)** — `git commit -m "feat(rules): add AI draft-rules dialog and entry points"`

---

## Task C12: Running-app verification & commit held work

- [ ] **Step 1:** Run the full suite — `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj` → all green.
- [ ] **Step 2:** Maintainer verifies in the running app (Tasks C10/C11 checklists): endpoint config + model fetch, the draft flow end-to-end, `RulesEnabled` toggle, and error handling.
- [ ] **Step 3:** On confirmation, commit the held C8–C11 changes.

---

## Self-Review

**Spec coverage (Phase 1c):** endpoint storage + DPAPI key → C1; overridable presets → C2; app-owned prompt → C3; response parsing → C4; summary → C5; orchestration → C6; save+reload → C7; HTTP client + live models → C8; DI → C9; Settings UI + `RulesEnabled` toggle → C10; dialog + entry points → C11; verification → C12.

**Placeholder scan:** none in the testable tasks (C1–C7) — full code + assertions. C8–C11 (HTTP/UI) give complete code for the non-XAML classes and precise structure + binding/verification steps for the WPF, consistent with how Phases 1a/1b handled UI/integration.

**Type consistency:** `IChatClient.CompleteAsync`, `ChatMessage(Role,Content)`, `DraftPrompt.BuildMessages`, `JsonExtraction.ExtractObject`, `PackSummarizer.Summarize`, `PackDraftService.DraftAsync`/`DraftResult`, `ProviderPresets.{EnsureSeeded,All}`/`ProviderPreset`, `ModelCatalog.FetchAsync`, `PackStore.Save`, and `AppSettings.{AiBaseUrl,AiModel,GetAiApiKey,SetAiApiKey,RulesEnabled}` are used consistently across C1–C11.
