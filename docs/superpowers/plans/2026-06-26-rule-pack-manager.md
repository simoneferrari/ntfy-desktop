# Rule-Pack Manager (Phase 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an in-app, form-based manager to browse, create, edit, enable/disable, delete, preview, and apply notification rule packs — without hand-editing JSON.

**Architecture:** Extend the Phase-1 `Features/Rules/` engine. The pack JSON gains per-pack and per-rule `enabled` flags and per-rule stable `id`s (parsed with back-compat). `PackStore.Packs` (what the engine consumes) is filtered to enabled-only, so the engine is untouched; a new editing view exposes everything. A `PackWriter` serialises the editor model back to JSON. A history simulator (reusing the engine's match/correlate core) powers a read-only preview and an additive "apply to history" backfill. A `FluentWindow` master–detail UI drives it all, launched from Settings → Rules.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.17763.0`), WPF + WPF-UI 4.3, CommunityToolkit.Mvvm 8.4 (`[ObservableProperty]`/`[RelayCommand]`), System.Text.Json, xUnit (tests in `NtfyDesktop.Tests`).

## Global Constraints

- **Engine code is not modified for enable/disable** — disabled packs/rules are filtered out of `PackStore.Packs`; `RuleEngine`/`ExpectationMonitor` stay as-is. (One additive refactor to `RuleEngine` is allowed in Task 4: extracting a static `EvaluateAgainst` that `Evaluate` then calls — behaviour-preserving.)
- **`enabled` absent ⇒ `true`**; every existing Phase-1 pack must keep loading and behaving identically.
- **Rule `id` absent ⇒ synthesise `"{packName}#{index}"`** (the legacy key) so `IncidentStore`/`ExpectationStore` state is preserved on first load.
- **The manager never edits raw JSON** — all editing is through forms.
- **Apply is additive** — it only ever sets `Suppressed = 1` (via `HistoryRepository.SuppressMessage`); never clears it. No auto-undo, no reset (deferred).
- **Apply never touches toasts** and **skips `expect` rules**.
- **Saving rewrites canonical JSON** via `PackWriter` (behaviourally-inert fields like `digest`/`onClose` and comments are not preserved).
- **New model fields are added as `init`-only properties with defaults**, not new positional record params, so existing construction sites and tests keep compiling.
- Tests: xUnit, `[Fact]`, plain `Assert.*`, temp dirs under `Path.GetTempPath()` with `IDisposable` cleanup (mirror `PackStoreTests`/`IncidentStoreTests`).
- Build: `dotnet build NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`. Test: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`.
- **Commit after each task. Do NOT push or open a PR** (the maintainer tests the running app and handles PRs). Spec lives at `docs/superpowers/specs/2026-06-26-notification-rule-engine-phase2-design.md` — fold it into the first commit.

---

### Task 1: Model fields (`id` + `enabled`) and parser support

**Files:**
- Modify: `Features/Rules/Model/Rules.cs` (add `Enabled` to `RulePack`, `MatchRule`, `CorrelateRule`; `Id`+`Enabled` to `MatchRule`)
- Modify: `Features/Rules/Model/Expect.cs` (add `Enabled` to `ExpectRule`)
- Create: `Features/Rules/Model/RuleId.cs` (fresh-id minting helper)
- Modify: `Features/Rules/PackParser.cs` (read `id` + `enabled`)
- Test: `NtfyDesktop.Tests/Rules/PackParserEnabledTests.cs`

**Interfaces:**
- Produces:
  - `RulePack(string Name, IReadOnlyList<MatchRule> MatchRules, IReadOnlyList<CorrelateRule> CorrelateRules, IReadOnlyList<ExpectRule> ExpectRules)` **plus** `bool Enabled { get; init; } = true;`
  - `MatchRule(Matcher When, IReadOnlyList<RuleAction> Actions)` **plus** `string Id { get; init; } = "";` and `bool Enabled { get; init; } = true;`
  - `CorrelateRule(string Id, Matcher Open, Matcher Close, KeySelector Key)` **plus** `bool Enabled { get; init; } = true;`
  - `ExpectRule(string Id, Matcher When, TimeSpan Every, TimeSpan Grace, AlertSpec OnAbsence, AlertSpec? OnRecovery)` **plus** `bool Enabled { get; init; } = true;`
  - `static string RuleId.NewId()` → 8-char lowercase base32 token.

- [ ] **Step 1: Write the failing test**

Create `NtfyDesktop.Tests/Rules/PackParserEnabledTests.cs`:

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackParserEnabledTests
{
    [Fact]
    public void Parse_EnabledAbsent_DefaultsTrue()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "match", "when": { "topic": "x" }, "do": ["suppressToast"] } ] }
            """;
        var pack = PackParser.Parse(json);
        Assert.True(pack.Enabled);
        Assert.True(pack.MatchRules[0].Enabled);
    }

    [Fact]
    public void Parse_EnabledFalse_OnPackAndRule()
    {
        const string json = """
            { "name": "p", "enabled": false, "rules": [
              { "type": "match", "enabled": false, "when": { "topic": "x" }, "do": ["suppressToast"] } ] }
            """;
        var pack = PackParser.Parse(json);
        Assert.False(pack.Enabled);
        Assert.False(pack.MatchRules[0].Enabled);
    }

    [Fact]
    public void Parse_RuleId_PreferredOverSynthesised()
    {
        const string json = """
            { "name": "Zabbix", "rules": [
              { "type": "correlate", "id": "abc123",
                "open":  { "titleRegex": "^PROBLEM" },
                "close": { "titleRegex": "^RESOLVED" },
                "key":   { "from": "body", "regex": "ID: (?<key>\\d+)" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).CorrelateRules);
        Assert.Equal("abc123", rule.Id);
    }

    [Fact]
    public void Parse_RuleId_Absent_SynthesisesLegacyKey()
    {
        const string json = """
            { "name": "Zabbix", "rules": [
              { "type": "expect", "when": { "topic": "b" },
                "every": "26h", "onAbsence": { "title": "missed" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).ExpectRules);
        Assert.Equal("Zabbix#0", rule.Id);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackParserEnabledTests`
Expected: FAIL — `RulePack`/`MatchRule` have no `Enabled` member (compile error).

- [ ] **Step 3: Add the model fields**

In `Features/Rules/Model/Rules.cs`, add `Enabled` (and `Id` to `MatchRule`) as init-only members on each record body:

```csharp
public sealed record MatchRule(Matcher When, IReadOnlyList<RuleAction> Actions)
{
    public string Id { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

public sealed record CorrelateRule(
    string Id,
    Matcher Open,
    Matcher Close,
    KeySelector Key)
{
    public bool Enabled { get; init; } = true;
}

public sealed record RulePack(
    string Name,
    IReadOnlyList<MatchRule> MatchRules,
    IReadOnlyList<CorrelateRule> CorrelateRules,
    IReadOnlyList<ExpectRule> ExpectRules)
{
    public bool Enabled { get; init; } = true;
}
```

In `Features/Rules/Model/Expect.cs`, add to `ExpectRule`:

```csharp
public sealed record ExpectRule(
    string Id,
    Matcher When,
    TimeSpan Every,
    TimeSpan Grace,
    AlertSpec OnAbsence,
    AlertSpec? OnRecovery)
{
    public bool Enabled { get; init; } = true;
}
```

Create `Features/Rules/Model/RuleId.cs`:

```csharp
namespace NtfyDesktop.Features.Rules.Model;

/// <summary>Mints short, stable, URL-safe rule ids for newly-created rules.</summary>
public static class RuleId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567"; // base32

    public static string NewId()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var chars = new char[8];
        for (var i = 0; i < 8; i++) chars[i] = Alphabet[bytes[i] & 31];
        return new string(chars);
    }
}
```

- [ ] **Step 4: Teach `PackParser` to read `id` + `enabled`**

In `Features/Rules/PackParser.cs`:

In `Parse`, read the pack-level flag and pass it onto the returned pack:

```csharp
var packEnabled = !root.TryGetProperty("enabled", out var pe) || pe.ValueKind != JsonValueKind.False;
```
…and change the final return to `return new RulePack(name, matchRules, correlateRules, expectRules) { Enabled = packEnabled };`

Add two helpers near the bottom:

```csharp
private static bool RuleEnabled(JsonElement rule) =>
    !rule.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;

private static string RuleId(JsonElement rule, string packName, int index) =>
    rule.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s
        ? s : $"{packName}#{index}";
```

In the `switch`, set the new fields:

```csharp
case "match":
    matchRules.Add(new MatchRule(ParseMatcher(rule, "when"), ParseActions(rule, "do"))
        { Id = RuleId(rule, name, index), Enabled = RuleEnabled(rule) });
    break;
case "correlate":
    correlateRules.Add(new CorrelateRule(
        Id: RuleId(rule, name, index),
        Open: ParseMatcher(rule, "open"),
        Close: ParseMatcher(rule, "close"),
        Key: ParseKey(rule)) { Enabled = RuleEnabled(rule) });
    break;
case "expect":
    if (TryParseExpect(rule, name, index) is { } expect)
        expectRules.Add(expect);
    break;
```

In `TryParseExpect`, change the `Id:` argument to `Id: RuleId(rule, packName, index)` and add `{ Enabled = RuleEnabled(rule) }` to the returned `ExpectRule`.

> Note: `PackParserTests.Parse_CorrelateRule_WithGeneratedId` (no `id` in JSON) still expects `"Zabbix#0"` — the synthesis path preserves it.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter "PackParserEnabledTests|PackParserTests|PackParserExpectTests"`
Expected: PASS (new tests green; existing parser tests unaffected).

- [ ] **Step 6: Commit**

```bash
git add Features/Rules/Model/Rules.cs Features/Rules/Model/Expect.cs Features/Rules/Model/RuleId.cs Features/Rules/PackParser.cs NtfyDesktop.Tests/Rules/PackParserEnabledTests.cs docs/superpowers/specs/2026-06-26-notification-rule-engine-phase2-design.md docs/superpowers/plans/2026-06-26-rule-pack-manager.md
git commit -m "feat(rules): add enabled flags + stable rule ids to pack model and parser"
```

---

### Task 2: `PackStore` enabled-filtering + editing API

**Files:**
- Modify: `Features/Rules/PackStore.cs`
- Test: `NtfyDesktop.Tests/Rules/PackStoreEditingTests.cs`

**Interfaces:**
- Consumes: `PackParser.Parse`, `RulePack` (with `Enabled` on pack + rules).
- Produces:
  - `record EditablePack(string Path, RulePack Pack)` (in `Features/Rules/PackStore.cs` namespace `NtfyDesktop.Features.Rules`).
  - `IReadOnlyList<EditablePack> PackStore.GetEditablePacks()` — every file, **unfiltered** (disabled included), paired with its absolute path.
  - `PackStore.Packs` — now the **enabled-only projection**: excludes disabled packs and, within enabled packs, disabled rules.
  - `void PackStore.Overwrite(string path, string json)` — rewrite an existing file, then `Reload()`.
  - `void PackStore.Delete(string path)` — delete a file, then `Reload()`.

- [ ] **Step 1: Write the failing test**

Create `NtfyDesktop.Tests/Rules/PackStoreEditingTests.cs`:

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackStoreEditingTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ntfyedit_" + Guid.NewGuid().ToString("N"));

    public PackStoreEditingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string Match(string name, bool packEnabled, bool ruleEnabled) => $$"""
        { "name": "{{name}}", "enabled": {{(packEnabled ? "true" : "false")}}, "rules": [
          { "type": "match", "enabled": {{(ruleEnabled ? "true" : "false")}},
            "when": { "topic": "x" }, "do": ["suppressToast"] } ] }
        """;

    [Fact]
    public void Packs_ExcludesDisabledPack_ButGetEditablePacksIncludesIt()
    {
        File.WriteAllText(Path.Combine(_dir, "off.json"), Match("Off", packEnabled: false, ruleEnabled: true));
        var store = new PackStore(_dir);

        Assert.Empty(store.Packs);                       // engine view: hidden
        Assert.Single(store.GetEditablePacks());         // editor view: present
        Assert.False(store.GetEditablePacks()[0].Pack.Enabled);
    }

    [Fact]
    public void Packs_ExcludesDisabledRule_WithinEnabledPack()
    {
        File.WriteAllText(Path.Combine(_dir, "p.json"), Match("P", packEnabled: true, ruleEnabled: false));
        var store = new PackStore(_dir);

        Assert.Single(store.Packs);                      // pack visible
        Assert.Empty(store.Packs[0].MatchRules);         // its disabled rule filtered out
        Assert.Single(store.GetEditablePacks()[0].Pack.MatchRules); // still there for editing
    }

    [Fact]
    public void Overwrite_ReplacesFileContent_AndReloads()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, Match("Before", true, true));
        var store = new PackStore(_dir);

        store.Overwrite(path, Match("After", true, true));
        Assert.Equal("After", Assert.Single(store.Packs).Name);
    }

    [Fact]
    public void Delete_RemovesFile_AndReloads()
    {
        var path = Path.Combine(_dir, "p.json");
        File.WriteAllText(path, Match("Doomed", true, true));
        var store = new PackStore(_dir);
        Assert.Single(store.Packs);

        store.Delete(path);
        Assert.Empty(store.Packs);
        Assert.False(File.Exists(path));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackStoreEditingTests`
Expected: FAIL — `GetEditablePacks`/`Overwrite`/`Delete` don't exist; `Packs` not yet filtered.

- [ ] **Step 3: Implement the editing API + filtering**

Rewrite `Features/Rules/PackStore.cs`'s state to keep both views:

```csharp
public IReadOnlyList<RulePack> Packs { get; private set; } = [];
private IReadOnlyList<EditablePack> _editable = [];

public IReadOnlyList<EditablePack> GetEditablePacks() => _editable;

public void Reload()
{
    if (!Directory.Exists(_directory)) { Packs = []; _editable = []; return; }

    var editable = new List<EditablePack>();
    foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
    {
        try { editable.Add(new EditablePack(file, PackParser.Parse(File.ReadAllText(file)))); }
        catch (Exception ex) { Debug.WriteLine($"[Rules] skipped invalid pack {file}: {ex.Message}"); }
    }
    _editable = editable;
    Packs = editable
        .Select(e => e.Pack)
        .Where(p => p.Enabled)
        .Select(p => p with
        {
            MatchRules = p.MatchRules.Where(r => r.Enabled).ToList(),
            CorrelateRules = p.CorrelateRules.Where(r => r.Enabled).ToList(),
            ExpectRules = p.ExpectRules.Where(r => r.Enabled).ToList(),
        })
        .ToList();
}

public void Overwrite(string path, string json) { File.WriteAllText(path, json); Reload(); }

public void Delete(string path) { if (File.Exists(path)) File.Delete(path); Reload(); }
```

Add the record at the bottom of the file (same namespace):

```csharp
/// <summary>A pack as loaded for editing: its file path plus the full (unfiltered) parsed content.</summary>
public sealed record EditablePack(string Path, RulePack Pack);
```

Keep the existing `Save(name, json)` method unchanged.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter "PackStoreEditingTests|PackStoreTests|PackStoreSaveTests"`
Expected: PASS (new + existing store tests green).

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/PackStore.cs NtfyDesktop.Tests/Rules/PackStoreEditingTests.cs
git commit -m "feat(rules): PackStore enabled-filtering + editing API (GetEditablePacks/Overwrite/Delete)"
```

---

### Task 3: `PackWriter` (model → canonical JSON, round-trip)

**Files:**
- Create: `Features/Rules/PackWriter.cs`
- Test: `NtfyDesktop.Tests/Rules/PackWriterTests.cs`

**Interfaces:**
- Consumes: `RulePack`, `MatchRule`, `CorrelateRule`, `ExpectRule`, `RuleAction`/`RuleActionKind`, `Matcher`, `KeySelector`/`KeyField`, `AlertSpec`, `Priority`, `Duration`.
- Produces: `static string PackWriter.Write(RulePack pack)` — canonical pack JSON including `enabled` (pack + each rule), each rule's `id`, and only the engine-supported fields. Output parses back via `PackParser.Parse` to a structurally-equal pack.

- [ ] **Step 1: Write the failing test**

Create `NtfyDesktop.Tests/Rules/PackWriterTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackWriterTests
{
    [Fact]
    public void Write_Match_RoundTrips()
    {
        var pack = new RulePack("Backups",
            MatchRules: [new MatchRule(
                new Matcher { Topic = "backups", TitleRegex = "succeeded", MinPriority = Priority.Low },
                [new RuleAction(RuleActionKind.SuppressToast), new RuleAction(RuleActionKind.Tag, "noise")])
                { Id = "m1", Enabled = false }],
            CorrelateRules: [], ExpectRules: []) { Enabled = false };

        var back = PackParser.Parse(PackWriter.Write(pack));

        Assert.Equal("Backups", back.Name);
        Assert.False(back.Enabled);
        var r = Assert.Single(back.MatchRules);
        Assert.Equal("m1", r.Id);
        Assert.False(r.Enabled);
        Assert.Equal("backups", r.When.Topic);
        Assert.Equal("succeeded", r.When.TitleRegex);
        Assert.Equal(Priority.Low, r.When.MinPriority);
        Assert.Contains(r.Actions, a => a.Kind == RuleActionKind.SuppressToast);
        Assert.Contains(r.Actions, a => a.Kind == RuleActionKind.Tag && a.Value == "noise");
    }

    [Fact]
    public void Write_Correlate_RoundTrips()
    {
        var pack = new RulePack("Zabbix", [],
            CorrelateRules: [new CorrelateRule("c1",
                new Matcher { TitleRegex = "^PROBLEM" },
                new Matcher { TitleRegex = "^RESOLVED" },
                new KeySelector { From = KeyField.Body, Regex = @"ID: (?<key>\d+)" }) { Enabled = true }],
            ExpectRules: []);

        var r = Assert.Single(PackParser.Parse(PackWriter.Write(pack)).CorrelateRules);
        Assert.Equal("c1", r.Id);
        Assert.Equal("^PROBLEM", r.Open.TitleRegex);
        Assert.Equal("^RESOLVED", r.Close.TitleRegex);
        Assert.Equal(KeyField.Body, r.Key.From);
        Assert.Equal(@"ID: (?<key>\d+)", r.Key.Regex);
    }

    [Fact]
    public void Write_Expect_RoundTrips()
    {
        var pack = new RulePack("HB", [], [],
            ExpectRules: [new ExpectRule("e1",
                new Matcher { Topic = "backups", TitleRegex = "succeeded" },
                Every: TimeSpan.FromHours(26), Grace: TimeSpan.FromHours(1),
                OnAbsence: new AlertSpec(Priority.Urgent, "Backup missed", "no backup"),
                OnRecovery: new AlertSpec(Priority.Default, "Backup back", null)) { Enabled = true }]);

        var r = Assert.Single(PackParser.Parse(PackWriter.Write(pack)).ExpectRules);
        Assert.Equal("e1", r.Id);
        Assert.Equal(TimeSpan.FromHours(26), r.Every);
        Assert.Equal(TimeSpan.FromHours(1), r.Grace);
        Assert.Equal("Backup missed", r.OnAbsence.Title);
        Assert.Equal(Priority.Urgent, r.OnAbsence.Priority);
        Assert.NotNull(r.OnRecovery);
        Assert.Equal("Backup back", r.OnRecovery!.Title);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackWriterTests`
Expected: FAIL — `PackWriter` does not exist.

- [ ] **Step 3: Implement `PackWriter`**

Create `Features/Rules/PackWriter.cs`. Build a `JsonNode`/`JsonObject` tree and serialise indented. Only emit fields the parser understands.

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

/// <summary>Serialises a pack back to canonical JSON (the inverse of <see cref="PackParser"/>).
/// Emits only engine-supported fields; behaviourally-inert constructs are not preserved.</summary>
public static class PackWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Write(RulePack pack)
    {
        var rules = new JsonArray();

        foreach (var r in pack.MatchRules)
        {
            var o = new JsonObject { ["type"] = "match", ["id"] = r.Id, ["enabled"] = r.Enabled };
            o["when"] = Matcher(r.When);
            var actions = new JsonArray();
            foreach (var a in r.Actions)
            {
                if (a.Kind == RuleActionKind.SuppressToast) actions.Add("suppressToast");
                else if (a.Kind == RuleActionKind.Tag && !string.IsNullOrEmpty(a.Value)) actions.Add($"tag:{a.Value}");
            }
            o["do"] = actions;
            rules.Add(o);
        }

        foreach (var r in pack.CorrelateRules)
        {
            rules.Add(new JsonObject
            {
                ["type"] = "correlate", ["id"] = r.Id, ["enabled"] = r.Enabled,
                ["open"] = Matcher(r.Open), ["close"] = Matcher(r.Close),
                ["key"] = new JsonObject
                {
                    ["from"] = r.Key.From == KeyField.Title ? "title" : "body",
                    ["regex"] = r.Key.Regex,
                },
            });
        }

        foreach (var r in pack.ExpectRules)
        {
            var o = new JsonObject
            {
                ["type"] = "expect", ["id"] = r.Id, ["enabled"] = r.Enabled,
                ["when"] = Matcher(r.When),
                ["every"] = WriteDuration(r.Every),
                ["grace"] = WriteDuration(r.Grace),
                ["onAbsence"] = Alert(r.OnAbsence),
            };
            if (r.OnRecovery is { } rec) o["onRecovery"] = Alert(rec);
            rules.Add(o);
        }

        var root = new JsonObject { ["name"] = pack.Name, ["enabled"] = pack.Enabled, ["rules"] = rules };
        return root.ToJsonString(Indented);
    }

    private static JsonObject Matcher(Matcher m)
    {
        var o = new JsonObject();
        if (m.Topic is not null) o["topic"] = m.Topic;
        if (m.MinPriority is { } p) o["minPriority"] = p.ToString().ToLowerInvariant();
        if (m.TitleRegex is not null) o["titleRegex"] = m.TitleRegex;
        if (m.BodyRegex is not null) o["bodyRegex"] = m.BodyRegex;
        if (m.Tag is not null) o["tag"] = m.Tag;
        return o;
    }

    private static JsonObject Alert(AlertSpec a)
    {
        var o = new JsonObject { ["priority"] = a.Priority.ToString().ToLowerInvariant(), ["title"] = a.Title };
        if (a.Message is not null) o["message"] = a.Message;
        return o;
    }

    // Whole units where possible (26h, 90m, 2d, 45s), else fall back to seconds.
    private static string WriteDuration(TimeSpan t)
    {
        if (t.TotalSeconds <= 0) return "0s";
        if (t.TotalDays == Math.Floor(t.TotalDays)) return $"{(int)t.TotalDays}d";
        if (t.TotalHours == Math.Floor(t.TotalHours)) return $"{(int)t.TotalHours}h";
        if (t.TotalMinutes == Math.Floor(t.TotalMinutes)) return $"{(int)t.TotalMinutes}m";
        return $"{(int)t.TotalSeconds}s";
    }
}
```

> `MinPriority`/`OnAbsence.Priority` use `Priority.ToString().ToLowerInvariant()` — `PackParser.ParsePriority` accepts `"min"/"low"/"default"/"high"/"urgent"`. Confirm the `Priority` enum names match those words; `Urgent` parses back via the `"urgent"` case.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackWriterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/PackWriter.cs NtfyDesktop.Tests/Rules/PackWriterTests.cs
git commit -m "feat(rules): PackWriter canonical serialiser with parse round-trip"
```

---

### Task 4: History simulator (preview core) + engine refactor

**Files:**
- Modify: `Features/Rules/RuleEngine.cs` (extract `static EvaluateAgainst`)
- Create: `Features/Rules/InMemoryIncidentStore.cs`
- Create: `Features/Rules/PackHistorySimulator.cs`
- Test: `NtfyDesktop.Tests/Rules/PackHistorySimulatorTests.cs`

**Interfaces:**
- Consumes: `RuleEngine`, `IIncidentStore`, `RuleVerdict`, `NtfyMessage`, `HistoryMessage`, `Matcher`, `ExpectRule`.
- Produces:
  - `static RuleVerdict RuleEngine.EvaluateAgainst(NtfyMessage message, IReadOnlyList<RulePack> packs, IIncidentStore incidents)` — the existing match+correlate loop, **no `RulesEnabled` gate**.
  - `sealed class InMemoryIncidentStore : IIncidentStore`.
  - `record SimResult(HistoryMessage Message, bool Hidden, IReadOnlyList<string> Tags, string? DismissMessageId, bool OpensIncident)`.
  - `record AbsenceWindow(string RuleTitle, DateTimeOffset Start, DateTimeOffset End, TimeSpan Gap)`.
  - `record SimReport(IReadOnlyList<SimResult> Results, IReadOnlyList<AbsenceWindow> Absences)`.
  - `static SimReport PackHistorySimulator.Simulate(RulePack pack, IReadOnlyList<HistoryMessage> oldestFirst)`.
  - `static NtfyMessage PackHistorySimulator.ToNtfyMessage(HistoryMessage m)`.

- [ ] **Step 1: Refactor `RuleEngine` (behaviour-preserving)**

In `Features/Rules/RuleEngine.cs`, move the body of `Evaluate` (everything after the `RulesEnabled` check) into a new `public static RuleVerdict EvaluateAgainst(NtfyMessage message, IReadOnlyList<RulePack> packs, IIncidentStore incidents)`, replacing `_packsProvider()` with `packs` and `_incidents` with `incidents`. Then:

```csharp
public RuleVerdict Evaluate(NtfyMessage message)
{
    if (!_settings.RulesEnabled) return RuleVerdict.PassThrough;
    return EvaluateAgainst(message, _packsProvider(), _incidents);
}
```

Make `ApplyActions` `static` if it isn't already (it is). Run the existing engine tests to confirm no behaviour change:

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter "RuleEngineMatchTests|RuleEngineCorrelateTests"`
Expected: PASS (unchanged).

- [ ] **Step 2: Write the failing simulator test**

Create `NtfyDesktop.Tests/Rules/PackHistorySimulatorTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackHistorySimulatorTests
{
    private static HistoryMessage Msg(string id, string topic, string? title, string? body, long time) => new()
    {
        MessageId = id, Topic = topic, Title = title, Body = body,
        Priority = Priority.Default, Timestamp = DateTimeOffset.FromUnixTimeSeconds(time),
    };

    [Fact]
    public void Match_SuppressToast_MarksHidden()
    {
        var pack = new RulePack("p",
            [new MatchRule(new Matcher { TitleRegex = "succeeded" },
                [new RuleAction(RuleActionKind.SuppressToast)]) { Id = "m" }], [], []);

        var report = PackHistorySimulator.Simulate(pack,
            [Msg("1", "backups", "Backup succeeded", null, 100),
             Msg("2", "backups", "Backup FAILED", null, 200)]);

        Assert.True(report.Results.Single(r => r.Message.MessageId == "1").Hidden);
        Assert.False(report.Results.Single(r => r.Message.MessageId == "2").Hidden);
    }

    [Fact]
    public void Correlate_CloseAfterOpen_FoldsBoth()
    {
        var pack = new RulePack("z", [],
            [new CorrelateRule("c",
                new Matcher { TitleRegex = "^PROBLEM" },
                new Matcher { TitleRegex = "^RESOLVED" },
                new KeySelector { From = KeyField.Body, Regex = @"ID:(?<key>\d+)" })], []);

        var report = PackHistorySimulator.Simulate(pack,
            [Msg("p1", "z", "PROBLEM cpu", "ID:7", 100),
             Msg("r1", "z", "RESOLVED cpu", "ID:7", 200)]);

        var close = report.Results.Single(r => r.Message.MessageId == "r1");
        Assert.True(close.Hidden);                       // resolution folds from feed
        Assert.Equal("p1", close.DismissMessageId);      // and dismisses its problem
        Assert.True(report.Results.Single(r => r.Message.MessageId == "p1").OpensIncident);
    }

    [Fact]
    public void Expect_GapBeyondInterval_ReportsAbsence()
    {
        var pack = new RulePack("hb", [], [],
            [new ExpectRule("e", new Matcher { TitleRegex = "succeeded" },
                Every: TimeSpan.FromHours(1), Grace: TimeSpan.Zero,
                OnAbsence: new AlertSpec(Priority.High, "missed", null), OnRecovery: null)]);

        // Two successes 5h apart → one >1h gap.
        var report = PackHistorySimulator.Simulate(pack,
            [Msg("a", "x", "succeeded", null, 0),
             Msg("b", "x", "succeeded", null, 5 * 3600)]);

        var w = Assert.Single(report.Absences);
        Assert.Equal("missed", w.RuleTitle);
        Assert.True(w.Gap >= TimeSpan.FromHours(1));
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackHistorySimulatorTests`
Expected: FAIL — `PackHistorySimulator`/`InMemoryIncidentStore` don't exist.

- [ ] **Step 4: Implement `InMemoryIncidentStore` + simulator**

Create `Features/Rules/InMemoryIncidentStore.cs`:

```csharp
namespace NtfyDesktop.Features.Rules;

/// <summary>A non-persistent incident store for previewing/simulating correlation
/// without touching the real (SQLite) incident state.</summary>
public sealed class InMemoryIncidentStore : IIncidentStore
{
    private readonly Dictionary<(string, string), Incident> _open = new();

    public Incident? FindOpen(string ruleId, string key) => _open.GetValueOrDefault((ruleId, key));
    public void Open(string ruleId, string key, string messageId, long openedAt) =>
        _open[(ruleId, key)] = new Incident(ruleId, key, messageId, openedAt);
    public void Resolve(string ruleId, string key) => _open.Remove((ruleId, key));
}
```

Create `Features/Rules/PackHistorySimulator.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

public sealed record SimResult(
    HistoryMessage Message, bool Hidden, IReadOnlyList<string> Tags,
    string? DismissMessageId, bool OpensIncident);

public sealed record AbsenceWindow(string RuleTitle, DateTimeOffset Start, DateTimeOffset End, TimeSpan Gap);

public sealed record SimReport(IReadOnlyList<SimResult> Results, IReadOnlyList<AbsenceWindow> Absences);

/// <summary>Runs one pack over an ordered slice of stored history to show what it WOULD do.
/// Pure and read-only; uses its own <see cref="InMemoryIncidentStore"/>.</summary>
public static class PackHistorySimulator
{
    public static NtfyMessage ToNtfyMessage(HistoryMessage m) => new()
    {
        Id = m.MessageId,
        Time = m.Timestamp.ToUnixTimeSeconds(),
        Topic = m.Topic,
        Title = m.Title,
        Message = m.Body,
        Priority = m.Priority,
        Tags = string.IsNullOrEmpty(m.Tags) ? null : m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
    };

    public static SimReport Simulate(RulePack pack, IReadOnlyList<HistoryMessage> oldestFirst)
    {
        var incidents = new InMemoryIncidentStore();
        var single = new[] { pack };
        var results = new List<SimResult>();

        foreach (var hm in oldestFirst)
        {
            var v = RuleEngine.EvaluateAgainst(ToNtfyMessage(hm), single, incidents);
            // Apply pending incident writes to our in-memory store so later closes can pair.
            if (v.OpenIncident is { } o) incidents.Open(o.RuleId, o.Key, o.MessageId, o.OpenedAt);
            if (v.CloseIncident is { } c) incidents.Resolve(c.RuleId, c.Key);

            results.Add(new SimResult(hm, v.HideFromFeed, v.Tags, v.DismissMessageId, v.OpenIncident is not null));
        }

        return new SimReport(results, DetectAbsences(pack, oldestFirst));
    }

    private static List<AbsenceWindow> DetectAbsences(RulePack pack, IReadOnlyList<HistoryMessage> oldestFirst)
    {
        var windows = new List<AbsenceWindow>();
        foreach (var rule in pack.ExpectRules)
        {
            var threshold = rule.Every + rule.Grace;
            DateTimeOffset? prev = null;
            foreach (var hm in oldestFirst)
            {
                if (!rule.When.Matches(hm.Topic, hm.Title, hm.Body, hm.Priority,
                        string.IsNullOrEmpty(hm.Tags) ? null : hm.Tags.Split(','))) continue;

                if (prev is { } p && hm.Timestamp - p > threshold)
                    windows.Add(new AbsenceWindow(rule.OnAbsence.Title, p, hm.Timestamp, hm.Timestamp - p));
                prev = hm.Timestamp;
            }
        }
        return windows;
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter "PackHistorySimulatorTests|RuleEngineMatchTests|RuleEngineCorrelateTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Features/Rules/RuleEngine.cs Features/Rules/InMemoryIncidentStore.cs Features/Rules/PackHistorySimulator.cs NtfyDesktop.Tests/Rules/PackHistorySimulatorTests.cs
git commit -m "feat(rules): history simulator for rule preview + static EvaluateAgainst"
```

---

### Task 5: Apply-to-history service (additive backfill)

**Files:**
- Create: `Features/Rules/RulePackHistoryService.cs`
- Modify: `Features/Rules/RulesFeature.cs` (register the service)

**Interfaces:**
- Consumes: `HistoryRepository` (`Query(Guid? topicId, …, int limit, bool includeSuppressed)`, `SuppressMessage(string)`), `IIncidentStore`, `RuleEngine.EvaluateAgainst`, `PackHistorySimulator`.
- Produces:
  - `sealed class RulePackHistoryService(HistoryRepository history, IIncidentStore incidents)`
  - `SimReport Preview(RulePack pack, Guid? topicId, int limit)` — fetch `limit` recent messages for the scope, sort oldest-first, delegate to `PackHistorySimulator.Simulate`.
  - `ApplyOutcome Apply(RulePack pack, Guid? topicId, int limit)` — re-evaluate over the scope with the **real** incident store and call `SuppressMessage` for each hidden/dismissed message; returns `record ApplyOutcome(int HiddenCount, int FoldedCount)`.

> No new unit tests: the decision logic is the simulator (Task 4, fully tested); this service is the thin history-I/O wrapper (consistent with `HistoryRepository` itself not being unit-tested). Verified manually in Task 8.

- [ ] **Step 1: Implement the service**

Create `Features/Rules/RulePackHistoryService.cs`:

```csharp
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

public sealed record ApplyOutcome(int HiddenCount, int FoldedCount);

/// <summary>Previews a pack against stored history (read-only) and applies it as an
/// additive backfill (sets the Suppressed flag on already-stored rows). Apply never
/// clears suppression and never touches toasts; expect rules are skipped.</summary>
public sealed class RulePackHistoryService(HistoryRepository history, IIncidentStore incidents)
{
    public SimReport Preview(RulePack pack, Guid? topicId, int limit)
    {
        var msgs = Fetch(topicId, limit);
        return PackHistorySimulator.Simulate(pack, msgs);
    }

    public ApplyOutcome Apply(RulePack pack, Guid? topicId, int limit)
    {
        var msgs = Fetch(topicId, limit);
        var single = new[] { pack };
        int hidden = 0, folded = 0;

        foreach (var hm in msgs)
        {
            var v = RuleEngine.EvaluateAgainst(PackHistorySimulator.ToNtfyMessage(hm), single, incidents);
            // Persist incident pairing so future live messages correlate against it.
            if (v.OpenIncident is { } o) incidents.Open(o.RuleId, o.Key, o.MessageId, o.OpenedAt);
            if (v.CloseIncident is { } c) incidents.Resolve(c.RuleId, c.Key);

            if (v.HideFromFeed) { history.SuppressMessage(hm.MessageId); hidden++; }
            if (v.DismissMessageId is { } d) { history.SuppressMessage(d); folded++; }
        }
        return new ApplyOutcome(hidden, folded);
    }

    // Query returns newest-first; correlation/absence need oldest-first.
    private List<HistoryMessage> Fetch(Guid? topicId, int limit)
    {
        var msgs = history.Query(topicId: topicId, limit: limit, includeSuppressed: true);
        msgs.Reverse();
        return msgs;
    }
}
```

> Confirm the exact parameter names of `HistoryRepository.Query` (`topicId`, `limit`, `includeSuppressed`) against `Features/History/HistoryRepository.cs:330` and adjust the call if the signature differs (it also takes optional `from`/`to` which we leave defaulted).

- [ ] **Step 2: Register in DI**

In `Features/Rules/RulesFeature.cs`, inside `AddRules()`, add:

```csharp
services.AddSingleton<RulePackHistoryService>();
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build NtfyDesktop.csproj`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Features/Rules/RulePackHistoryService.cs Features/Rules/RulesFeature.cs
git commit -m "feat(rules): preview + additive apply-to-history service"
```

---

### Task 6: Editor view-models (matcher, rules, pack) + validation

**Files:**
- Create: `Features/Rules/Editor/MatcherViewModel.cs`
- Create: `Features/Rules/Editor/RuleViewModels.cs` (`RuleViewModel` base + `MatchRuleViewModel`/`CorrelateRuleViewModel`/`ExpectRuleViewModel`)
- Create: `Features/Rules/Editor/PackViewModel.cs`
- Test: `NtfyDesktop.Tests/Rules/EditorViewModelTests.cs`

**Interfaces:**
- Consumes: `Matcher`, `MatchRule`, `CorrelateRule`, `ExpectRule`, `RuleAction`/`Kind`, `KeySelector`/`KeyField`, `AlertSpec`, `Priority`, `Duration`, `RuleId`, `PackWriter`, `EditablePack`.
- Produces:
  - `MatcherViewModel` — observable `Topic`/`MinPriority`/`TitleRegex`/`BodyRegex`/`Tag`; `Matcher ToModel()`; `static MatcherViewModel FromModel(Matcher)`; `bool TryValidate(out string error)`.
  - `abstract RuleViewModel` — `string Id`, `bool Enabled`, `string Kind`, `string Summary`, `abstract bool TryValidate(out string error)`.
  - `MatchRuleViewModel`/`CorrelateRuleViewModel`/`ExpectRuleViewModel` with `FromModel`/`ToModel`.
  - `PackViewModel` — `string Name`, `bool Enabled`, `ObservableCollection<RuleViewModel> Rules`, `string? FilePath`; `static PackViewModel FromEditable(EditablePack)`; `RulePack ToModel()`; `string ToJson()`; `bool TryValidate(out string error)`.

- [ ] **Step 1: Write the failing tests**

Create `NtfyDesktop.Tests/Rules/EditorViewModelTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Editor;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class EditorViewModelTests
{
    [Fact]
    public void Matcher_BadRegex_FailsValidation()
    {
        var vm = new MatcherViewModel { TitleRegex = "(" };
        Assert.False(vm.TryValidate(out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void MatchRule_ToModel_BuildsActions()
    {
        var vm = new MatchRuleViewModel { Id = "m" };
        vm.When.Topic = "backups";
        vm.SuppressToast = true;
        vm.TagValue = "noise";

        var model = vm.ToModel();
        Assert.Equal("backups", model.When.Topic);
        Assert.Contains(model.Actions, a => a.Kind == RuleActionKind.SuppressToast);
        Assert.Contains(model.Actions, a => a.Kind == RuleActionKind.Tag && a.Value == "noise");
    }

    [Fact]
    public void ExpectRule_MissingTitle_FailsValidation()
    {
        var vm = new ExpectRuleViewModel { Id = "e", Every = "26h", AbsenceTitle = "" };
        Assert.False(vm.TryValidate(out _));
    }

    [Fact]
    public void ExpectRule_BadDuration_FailsValidation()
    {
        var vm = new ExpectRuleViewModel { Id = "e", Every = "nope", AbsenceTitle = "x" };
        Assert.False(vm.TryValidate(out _));
    }

    [Fact]
    public void Pack_ToJson_RoundTripsThroughParser()
    {
        var pack = new PackViewModel { Name = "Backups", Enabled = true };
        var rule = new MatchRuleViewModel { Id = "m", Enabled = true };
        rule.When.TitleRegex = "succeeded";
        rule.SuppressToast = true;
        pack.Rules.Add(rule);

        var parsed = PackParser.Parse(pack.ToJson());
        Assert.Equal("Backups", parsed.Name);
        var m = Assert.Single(parsed.MatchRules);
        Assert.Equal("m", m.Id);
        Assert.Equal("succeeded", m.When.TitleRegex);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter EditorViewModelTests`
Expected: FAIL — editor view-models don't exist.

- [ ] **Step 3: Implement `MatcherViewModel`**

Create `Features/Rules/Editor/MatcherViewModel.cs`:

```csharp
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class MatcherViewModel : ObservableObject
{
    [ObservableProperty] private string _topic = "";
    [ObservableProperty] private Priority? _minPriority;
    [ObservableProperty] private string _titleRegex = "";
    [ObservableProperty] private string _bodyRegex = "";
    [ObservableProperty] private string _tag = "";

    public static MatcherViewModel FromModel(Matcher m) => new()
    {
        Topic = m.Topic ?? "", MinPriority = m.MinPriority,
        TitleRegex = m.TitleRegex ?? "", BodyRegex = m.BodyRegex ?? "", Tag = m.Tag ?? "",
    };

    public Matcher ToModel() => new()
    {
        Topic = Nullify(Topic), MinPriority = MinPriority,
        TitleRegex = Nullify(TitleRegex), BodyRegex = Nullify(BodyRegex), Tag = Nullify(Tag),
    };

    public bool TryValidate(out string error)
    {
        foreach (var (label, pattern) in new[] { ("Title", TitleRegex), ("Body", BodyRegex) })
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            try { _ = Regex.Match("", pattern); }
            catch (Exception ex) { error = $"{label} regex is invalid: {ex.Message}"; return false; }
        }
        error = ""; return true;
    }

    private static string? Nullify(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
```

- [ ] **Step 4: Implement the rule view-models**

Create `Features/Rules/Editor/RuleViewModels.cs`:

```csharp
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Ai;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public abstract partial class RuleViewModel : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private bool _enabled = true;
    public abstract string Kind { get; }
    public abstract string Summary { get; }
    public abstract bool TryValidate(out string error);
}

public sealed partial class MatchRuleViewModel : RuleViewModel
{
    public override string Kind => "Match";
    public MatcherViewModel When { get; init; } = new();
    [ObservableProperty] private bool _suppressToast = true;
    [ObservableProperty] private string _tagValue = "";

    public static MatchRuleViewModel FromModel(MatchRule m)
    {
        var vm = new MatchRuleViewModel
        {
            Id = m.Id, Enabled = m.Enabled, When = MatcherViewModel.FromModel(m.When),
            SuppressToast = m.Actions.Any(a => a.Kind == RuleActionKind.SuppressToast),
            TagValue = m.Actions.FirstOrDefault(a => a.Kind == RuleActionKind.Tag)?.Value ?? "",
        };
        return vm;
    }

    public MatchRule ToModel()
    {
        var actions = new List<RuleAction>();
        if (SuppressToast) actions.Add(new RuleAction(RuleActionKind.SuppressToast));
        if (!string.IsNullOrWhiteSpace(TagValue)) actions.Add(new RuleAction(RuleActionKind.Tag, TagValue.Trim()));
        return new MatchRule(When.ToModel(), actions) { Id = Id, Enabled = Enabled };
    }

    public override bool TryValidate(out string error)
    {
        if (!When.TryValidate(out error)) return false;
        if (!SuppressToast && string.IsNullOrWhiteSpace(TagValue))
        { error = "A match rule must suppress the toast or add a tag."; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [ToModelSafe()], [], [])).FirstOrDefault() ?? "Match rule";

    private MatchRule ToModelSafe() => new(When.ToModel(),
        SuppressToast ? [new RuleAction(RuleActionKind.SuppressToast)] : []) { Id = Id, Enabled = Enabled };
}

public sealed partial class CorrelateRuleViewModel : RuleViewModel
{
    public override string Kind => "Correlate";
    public MatcherViewModel Open { get; init; } = new();
    public MatcherViewModel Close { get; init; } = new();
    [ObservableProperty] private KeyField _keyFrom = KeyField.Body;
    [ObservableProperty] private string _keyRegex = "";

    public static CorrelateRuleViewModel FromModel(CorrelateRule c) => new()
    {
        Id = c.Id, Enabled = c.Enabled,
        Open = MatcherViewModel.FromModel(c.Open), Close = MatcherViewModel.FromModel(c.Close),
        KeyFrom = c.Key.From, KeyRegex = c.Key.Regex,
    };

    public CorrelateRule ToModel() => new(
        Id, Open.ToModel(), Close.ToModel(),
        new KeySelector { From = KeyFrom, Regex = KeyRegex.Trim() }) { Enabled = Enabled };

    public override bool TryValidate(out string error)
    {
        if (!Open.TryValidate(out error) || !Close.TryValidate(out error)) return false;
        if (string.IsNullOrWhiteSpace(KeyRegex))
        { error = "A correlate rule needs a key regex (e.g. ID: (?<key>\\d+))."; return false; }
        try { _ = new System.Text.RegularExpressions.Regex(KeyRegex); }
        catch (Exception ex) { error = $"Key regex is invalid: {ex.Message}"; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [], [ToModel()], [])).FirstOrDefault() ?? "Correlate rule";
}

public sealed partial class ExpectRuleViewModel : RuleViewModel
{
    public override string Kind => "Expect";
    public MatcherViewModel When { get; init; } = new();
    [ObservableProperty] private string _every = "24h";
    [ObservableProperty] private string _grace = "1h";
    [ObservableProperty] private Priority _absencePriority = Priority.High;
    [ObservableProperty] private string _absenceTitle = "";
    [ObservableProperty] private string _absenceMessage = "";
    [ObservableProperty] private bool _hasRecovery;
    [ObservableProperty] private Priority _recoveryPriority = Priority.Default;
    [ObservableProperty] private string _recoveryTitle = "";

    public static ExpectRuleViewModel FromModel(ExpectRule e) => new()
    {
        Id = e.Id, Enabled = e.Enabled, When = MatcherViewModel.FromModel(e.When),
        Every = Compact(e.Every), Grace = Compact(e.Grace),
        AbsencePriority = e.OnAbsence.Priority, AbsenceTitle = e.OnAbsence.Title, AbsenceMessage = e.OnAbsence.Message ?? "",
        HasRecovery = e.OnRecovery is not null,
        RecoveryPriority = e.OnRecovery?.Priority ?? Priority.Default, RecoveryTitle = e.OnRecovery?.Title ?? "",
    };

    public ExpectRule ToModel()
    {
        Duration.TryParse(Every, out var every);
        Duration.TryParse(Grace, out var grace);
        var recovery = HasRecovery && !string.IsNullOrWhiteSpace(RecoveryTitle)
            ? new AlertSpec(RecoveryPriority, RecoveryTitle.Trim(), null) : null;
        return new ExpectRule(Id, When.ToModel(), every, grace,
            new AlertSpec(AbsencePriority, AbsenceTitle.Trim(), string.IsNullOrWhiteSpace(AbsenceMessage) ? null : AbsenceMessage.Trim()),
            recovery) { Enabled = Enabled };
    }

    public override bool TryValidate(out string error)
    {
        if (!When.TryValidate(out error)) return false;
        if (!Duration.TryParse(Every, out _)) { error = "‘Every’ must be a duration like 26h, 90m, 2d."; return false; }
        if (!string.IsNullOrWhiteSpace(Grace) && !Duration.TryParse(Grace, out _)) { error = "‘Grace’ must be a duration like 1h."; return false; }
        if (string.IsNullOrWhiteSpace(AbsenceTitle)) { error = "An expect rule needs an absence-alert title."; return false; }
        error = ""; return true;
    }

    public override string Summary =>
        PackSummarizer.Summarize(new RulePack("", [], [], [ToModel()])).FirstOrDefault() ?? "Expect rule";

    private static string Compact(TimeSpan t) =>
        t.TotalDays == Math.Floor(t.TotalDays) && t.TotalDays >= 1 ? $"{(int)t.TotalDays}d"
        : t.TotalHours == Math.Floor(t.TotalHours) && t.TotalHours >= 1 ? $"{(int)t.TotalHours}h"
        : $"{(int)t.TotalMinutes}m";
}
```

> `ExpectRuleViewModel.ToModel` is only called after `TryValidate` succeeds, so the `Duration.TryParse` discards are safe. `MatchRuleViewModel.Summary` uses a suppress-only `ToModelSafe` to avoid the summariser flagging an empty matcher mid-edit; the real `ToModel` is used for save.

- [ ] **Step 5: Implement `PackViewModel`**

Create `Features/Rules/Editor/PackViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class PackViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "New pack";
    [ObservableProperty] private bool _enabled = true;
    public ObservableCollection<RuleViewModel> Rules { get; } = [];

    /// <summary>The file this pack was loaded from; null for a not-yet-saved pack.</summary>
    public string? FilePath { get; set; }

    public static PackViewModel FromEditable(EditablePack e)
    {
        var vm = new PackViewModel { Name = e.Pack.Name, Enabled = e.Pack.Enabled, FilePath = e.Path };
        foreach (var m in e.Pack.MatchRules) vm.Rules.Add(MatchRuleViewModel.FromModel(m));
        foreach (var c in e.Pack.CorrelateRules) vm.Rules.Add(CorrelateRuleViewModel.FromModel(c));
        foreach (var x in e.Pack.ExpectRules) vm.Rules.Add(ExpectRuleViewModel.FromModel(x));
        return vm;
    }

    public RulePack ToModel() => new(
        Name.Trim(),
        Rules.OfType<MatchRuleViewModel>().Select(r => r.ToModel()).ToList(),
        Rules.OfType<CorrelateRuleViewModel>().Select(r => r.ToModel()).ToList(),
        Rules.OfType<ExpectRuleViewModel>().Select(r => r.ToModel()).ToList()) { Enabled = Enabled };

    public string ToJson() => PackWriter.Write(ToModel());

    public bool TryValidate(out string error)
    {
        if (string.IsNullOrWhiteSpace(Name)) { error = "The pack needs a name."; return false; }
        foreach (var r in Rules)
            if (!r.TryValidate(out error)) { error = $"{r.Kind} rule: {error}"; return false; }
        error = ""; return true;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter EditorViewModelTests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add Features/Rules/Editor/ NtfyDesktop.Tests/Rules/EditorViewModelTests.cs
git commit -m "feat(rules): editor view-models for matcher/match/correlate/expect/pack with validation"
```

---

### Task 7: `RulePackManagerViewModel` (window orchestration)

**Files:**
- Create: `Features/Rules/Editor/RulePackManagerViewModel.cs`
- Modify: `Features/Rules/RulesFeature.cs` (register transient)
- Test: `NtfyDesktop.Tests/Rules/RulePackManagerViewModelTests.cs`

**Interfaces:**
- Consumes: `PackStore` (`GetEditablePacks`/`Save`/`Overwrite`/`Delete`), `RulePackHistoryService`, `AppSettings` (topic list for the preview scope), `RuleId`, `PackViewModel`/`RuleViewModel` subtypes.
- Produces (members the window binds to / calls):
  - `ObservableCollection<PackViewModel> Packs`, `PackViewModel? SelectedPack`, `RuleViewModel? SelectedRule`, `string ErrorText`.
  - `void Reload()` — reload `Packs` from `PackStore.GetEditablePacks()`.
  - `void NewBlankPack()`, `void DeleteSelectedPack()`, `void AddRule(string kind)`, `void DeleteSelectedRule()`.
  - `bool Save()` — validate + persist all packs; returns success (sets `ErrorText` on failure).
  - `SimReport Preview()`, `ApplyOutcome Apply()` — over `SelectedPack` with the chosen scope.
  - scope: `IReadOnlyList<int> ScopeCounts` (`[50,200,1000]`), `int ScopeCount`, topic picker `Topics`/`SelectedScopeTopic`.

- [ ] **Step 1: Write the failing tests**

Create `NtfyDesktop.Tests/Rules/RulePackManagerViewModelTests.cs`. Construct the VM with a temp-dir `PackStore`; pass `null` for the history service and settings where a test path doesn't touch them (the persistence tests only exercise pack CRUD).

```csharp
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Editor;

namespace NtfyDesktop.Tests.Rules;

public class RulePackManagerViewModelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ntfymgr_" + Guid.NewGuid().ToString("N"));

    public RulePackManagerViewModelTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private RulePackManagerViewModel NewVm() =>
        new(new PackStore(_dir), historyService: null!, topicNames: () => []);

    [Fact]
    public void AddRule_MintsId_AndAddsToSelectedPack()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.AddRule("Match");

        var rule = Assert.Single(vm.SelectedPack!.Rules);
        Assert.False(string.IsNullOrEmpty(rule.Id));
    }

    [Fact]
    public void Save_NewPack_WritesFile_AndReloadsIntoStore()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.SelectedPack!.Name = "Backups";
        vm.AddRule("Match");
        ((MatchRuleViewModel)vm.SelectedPack.Rules[0]).When.TitleRegex = "succeeded";

        Assert.True(vm.Save());
        Assert.Single(Directory.GetFiles(_dir, "*.json"));

        // A fresh store sees the persisted, enabled pack.
        Assert.Equal("Backups", Assert.Single(new PackStore(_dir).Packs).Name);
    }

    [Fact]
    public void DisablePack_ThenSave_RemovesItFromEngineView()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.SelectedPack!.Name = "Off";
        vm.AddRule("Match");
        ((MatchRuleViewModel)vm.SelectedPack.Rules[0]).When.Topic = "x";
        vm.SelectedPack.Enabled = false;

        Assert.True(vm.Save());
        var store = new PackStore(_dir);
        Assert.Empty(store.Packs);                  // engine ignores disabled pack
        Assert.Single(store.GetEditablePacks());    // still editable
    }

    [Fact]
    public void DeleteSelectedPack_RemovesSavedFile()
    {
        var vm = NewVm();
        vm.NewBlankPack();
        vm.SelectedPack!.Name = "Doomed";
        vm.AddRule("Match");
        ((MatchRuleViewModel)vm.SelectedPack.Rules[0]).When.Topic = "x";
        Assert.True(vm.Save());

        vm.SelectedPack = vm.Packs[0];
        vm.DeleteSelectedPack();
        Assert.Empty(Directory.GetFiles(_dir, "*.json"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter RulePackManagerViewModelTests`
Expected: FAIL — `RulePackManagerViewModel` does not exist.

- [ ] **Step 3: Implement the manager VM**

Create `Features/Rules/Editor/RulePackManagerViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules.Editor;

public sealed partial class RulePackManagerViewModel : ObservableObject
{
    private readonly PackStore _store;
    private readonly RulePackHistoryService _history;
    private readonly Func<IReadOnlyList<(Guid Id, string Name)>> _topicNames;

    public RulePackManagerViewModel(
        PackStore store, RulePackHistoryService historyService,
        Func<IReadOnlyList<(Guid, string)>> topicNames)
    {
        _store = store;
        _history = historyService;
        _topicNames = topicNames;
        Reload();
    }

    public ObservableCollection<PackViewModel> Packs { get; } = [];

    [ObservableProperty] private PackViewModel? _selectedPack;
    [ObservableProperty] private RuleViewModel? _selectedRule;
    [ObservableProperty] private string _errorText = "";

    public IReadOnlyList<int> ScopeCounts { get; } = [50, 200, 1000];
    [ObservableProperty] private int _scopeCount = 200;

    public ObservableCollection<TopicScope> Topics { get; } = [];
    [ObservableProperty] private TopicScope? _selectedScopeTopic;

    // Bindable preview output (populated by Preview()).
    public ObservableCollection<SimResult> PreviewResults { get; } = [];
    public ObservableCollection<AbsenceWindow> PreviewAbsences { get; } = [];
    [ObservableProperty] private string _previewSummary = "";

    public void Reload()
    {
        Packs.Clear();
        foreach (var e in _store.GetEditablePacks()) Packs.Add(PackViewModel.FromEditable(e));
        SelectedPack = Packs.FirstOrDefault();

        Topics.Clear();
        Topics.Add(new TopicScope(null, "All topics"));
        foreach (var (id, name) in _topicNames()) Topics.Add(new TopicScope(id, name));
        SelectedScopeTopic = Topics[0];
    }

    public void NewBlankPack()
    {
        var pack = new PackViewModel { Name = "New pack", FilePath = null };
        Packs.Add(pack);
        SelectedPack = pack;
    }

    public void DeleteSelectedPack()
    {
        if (SelectedPack is not { } p) return;
        if (p.FilePath is { } path) _store.Delete(path);
        Packs.Remove(p);
        SelectedPack = Packs.FirstOrDefault();
    }

    public void AddRule(string kind)
    {
        if (SelectedPack is not { } p) return;
        RuleViewModel rule = kind switch
        {
            "Correlate" => new CorrelateRuleViewModel { Id = RuleId.NewId() },
            "Expect" => new ExpectRuleViewModel { Id = RuleId.NewId() },
            _ => new MatchRuleViewModel { Id = RuleId.NewId() },
        };
        p.Rules.Add(rule);
        SelectedRule = rule;
    }

    public void DeleteSelectedRule()
    {
        if (SelectedPack is { } p && SelectedRule is { } r) { p.Rules.Remove(r); SelectedRule = null; }
    }

    public bool Save()
    {
        foreach (var p in Packs)
            if (!p.TryValidate(out var err)) { ErrorText = $"“{p.Name}”: {err}"; return false; }

        foreach (var p in Packs)
        {
            var json = p.ToJson();
            if (p.FilePath is { } path) _store.Overwrite(path, json);
            else p.FilePath = _store.Save(p.Name, json);
        }
        ErrorText = "";
        return true;
    }

    public SimReport? Preview()
    {
        if (SelectedPack is not { } p) return null;
        if (!p.TryValidate(out var err)) { ErrorText = err; return null; }
        ErrorText = "";

        var report = _history.Preview(p.ToModel(), SelectedScopeTopic?.Id, ScopeCount);

        PreviewResults.Clear();
        foreach (var r in report.Results.Where(r => r.Hidden || r.OpensIncident || r.Tags.Count > 0))
            PreviewResults.Add(r);
        PreviewAbsences.Clear();
        foreach (var a in report.Absences) PreviewAbsences.Add(a);

        var hidden = report.Results.Count(r => r.Hidden);
        PreviewSummary = $"{hidden} hidden, {report.Absences.Count} absence window(s) over {report.Results.Count} message(s).";
        return report;
    }

    public ApplyOutcome? Apply()
    {
        if (SelectedPack is not { } p) return null;
        if (!p.TryValidate(out var err)) { ErrorText = err; return null; }
        ErrorText = "";
        return _history.Apply(p.ToModel(), SelectedScopeTopic?.Id, ScopeCount);
    }
}

public sealed record TopicScope(Guid? Id, string Name);
```

> The unit tests pass `historyService: null!` and only call CRUD/Save paths (never `Preview`/`Apply`), so the null service is never dereferenced. The window (Task 8) supplies the real service.

- [ ] **Step 4: Register in DI**

In `Features/Rules/RulesFeature.cs`, inside `AddRules()`, add a transient registration that wires the topic-name accessor from `AppSettings`:

```csharp
services.AddTransient<RulePackManagerViewModel>(sp =>
{
    var settings = sp.GetRequiredService<AppSettings>();
    return new RulePackManagerViewModel(
        sp.GetRequiredService<PackStore>(),
        sp.GetRequiredService<RulePackHistoryService>(),
        () => settings.Topics.Select(t => (t.Id, t.EffectiveDisplayName)).ToList());
});
```

(Confirm `TopicSettings.EffectiveDisplayName` / `Id` names against `Features/Rules/Ai/DraftRulesViewModel.cs:76` which already uses `t.Id` and `t.EffectiveDisplayName`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter RulePackManagerViewModelTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Features/Rules/Editor/RulePackManagerViewModel.cs Features/Rules/RulesFeature.cs NtfyDesktop.Tests/Rules/RulePackManagerViewModelTests.cs
git commit -m "feat(rules): rule-pack manager view-model (CRUD, save, preview, apply orchestration)"
```

---

### Task 8: Manager window UI + Settings entry point (manual-verified)

**Files:**
- Create: `Features/Rules/Editor/RulePackManagerWindow.xaml`
- Create: `Features/Rules/Editor/RulePackManagerWindow.xaml.cs`
- Modify: `Features/Settings/SettingsPage.xaml` (swap the "Draft rules with AI…" button row for "Manage rule packs…")
- Modify: `Features/Settings/SettingsPage.xaml.cs` (`OnManagePacksClicked`)

**Interfaces:**
- Consumes: `RulePackManagerViewModel` (via `App.Services.GetRequiredService<…>()`), `DraftRulesViewModel`/`DraftRulesDialog` (AI path), `SimReport`/`ApplyOutcome`.

This task is UI; it is verified by running the app (the app's XAML is not unit-tested). Follow the existing `DraftRulesDialog.xaml` for window chrome (`ui:FluentWindow`, `ui:TitleBar`, `WindowBackdropType="None"`, `ShowInTaskbar="False"`) and `SettingsPage.xaml.cs` for how dialogs are opened.

- [ ] **Step 1: Build the window XAML**

Create `Features/Rules/Editor/RulePackManagerWindow.xaml` — a `ui:FluentWindow` (Title "Manage rule packs", ~900×640) with a two-column grid:

- **Left column (~260px):** header "Packs"; a `ListBox` bound to `Packs` (item template: a `CheckBox` bound to `Enabled` + a `TextBlock` bound to `Name` + a faded rule-count); `SelectedItem` two-way to `SelectedPack`. Below it: a **New pack** split/menu button (`MenuItem`s "Blank" → `OnNewBlank`, "Draft with AI…" → `OnNewWithAi`) and a **Delete pack** button → `OnDeletePack`.
- **Right column:** when `SelectedPack` is set — a `TextBox` bound to `SelectedPack.Name`, a `ui:ToggleSwitch` bound to `SelectedPack.Enabled`; a `ListBox` bound to `SelectedPack.Rules` (item template: enable `CheckBox`, a type chip bound to `Kind`, a `TextBlock` bound to `Summary`), `SelectedItem` two-way to `SelectedRule`; an **Add rule** menu button (Match/Correlate/Expect → `OnAddRule` with the kind in `Tag`) and **Delete rule** → `OnDeleteRule`; and a **rule-form region** that swaps on rule type using `DataTemplate`s in `Window.Resources` keyed by `DataType` (`MatchRuleViewModel`/`CorrelateRuleViewModel`/`ExpectRuleViewModel`) hosted in a `ContentControl Content="{Binding SelectedRule}"`.
  - *Match form:* matcher fields (Topic, MinPriority `ComboBox`, TitleRegex, BodyRegex, Tag) + `CheckBox` SuppressToast + `TextBox` TagValue.
  - *Correlate form:* Open matcher, Close matcher, KeyFrom `ComboBox` (Title/Body), KeyRegex.
  - *Expect form:* When matcher, Every, Grace, AbsencePriority `ComboBox`, AbsenceTitle, AbsenceMessage, HasRecovery `CheckBox`, recovery fields.
  - For `MinPriority`/priority combos, bind `ItemsSource` to the `Priority` enum values (an `ObjectDataProvider` over `Enum.GetValues`, as is conventional in WPF) — or expose a static `Priority[]` on the VM. Keep it simple: add `public static Array Priorities => Enum.GetValues(typeof(Priority));` to `MatcherViewModel`/expect VM and bind to it.
- **Preview region** (below the rule form, or a collapsible panel): a `TextBlock` bound to `PreviewSummary`, a `ListView`/`ItemsControl` bound to `PreviewResults` (show each affected message's title + a badge from `Hidden`/`OpensIncident`/`Tags`), and a small list bound to `PreviewAbsences` (gap windows). Empty until **Preview** is clicked.
- **Footer:** a scope row (topic `ComboBox` bound to `Topics`/`SelectedScopeTopic`, count `ComboBox` bound to `ScopeCounts`/`ScopeCount`), an error `TextBlock` (red, bound to `ErrorText`), and buttons **Preview**, **Apply**, **Save**, **Cancel**.

- [ ] **Step 2: Build the code-behind**

Create `Features/Rules/Editor/RulePackManagerWindow.xaml.cs`. Constructor takes the VM and sets `DataContext`. Handlers:

```csharp
private void OnNewBlank(object s, RoutedEventArgs e) => _vm.NewBlankPack();
private void OnDeletePack(object s, RoutedEventArgs e) => _vm.DeleteSelectedPack();
private void OnAddRule(object s, RoutedEventArgs e) => _vm.AddRule((string)((FrameworkElement)s).Tag);
private void OnDeleteRule(object s, RoutedEventArgs e) => _vm.DeleteSelectedRule();
private void OnCancel(object s, RoutedEventArgs e) => Close();

private void OnNewWithAi(object s, RoutedEventArgs e)
{
    var vm = App.Services.GetRequiredService<DraftRulesViewModel>();
    var dlg = new DraftRulesDialog(vm, topicId: null) { Owner = this };
    dlg.ShowDialog();
    _vm.Reload();                       // pick up an AI-saved pack
}

private void OnSave(object s, RoutedEventArgs e)
{
    if (_vm.Save()) Close();
}

private void OnPreview(object s, RoutedEventArgs e) => _vm.Preview();
// Rendering is via binding: PreviewSummary (TextBlock), PreviewResults (ListView of
// affected messages), PreviewAbsences (ListView of gap windows). Preview() populates them.

private async void OnApply(object s, RoutedEventArgs e)
{
    var preview = _vm.Preview();
    if (preview is null) return;
    var hidden = preview.Results.Count(r => r.Hidden);
    var confirm = new Wpf.Ui.Controls.MessageBox
    {
        Title = "Apply to history",
        Content = $"This will hide {hidden} message(s) from the feed for “{_vm.SelectedPack!.Name}”. " +
                  "This can’t be automatically undone. Apply?",
        PrimaryButtonText = "Apply", CloseButtonText = "Cancel",
    };
    if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
    _vm.Apply();
}
```

(Use the same `using` aliases as `SettingsPage.xaml.cs`: `Microsoft.Extensions.DependencyInjection`, `NtfyDesktop.Features.Rules.Ai`.)

- [ ] **Step 3: Swap the Settings entry point**

In `Features/Settings/SettingsPage.xaml`, replace the "Draft rules with AI" row (the `Border` around lines ~345–358 containing `Content="Draft rules with AI…"` / `Click="OnDraftRulesClicked"`) with a "Manage rule packs" row:

```xml
<ui:TextBlock Style="{StaticResource RowTitle}" Text="Rule packs" />
<ui:TextBlock Style="{StaticResource RowDescription}"
              Text="Browse, create, edit, enable/disable, preview and apply notification rule packs." />
…
<ui:Button Grid.Column="1" VerticalAlignment="Center"
           Icon="{ui:SymbolIcon Filter24}"
           Content="Manage rule packs…"
           Click="OnManagePacksClicked" />
```

In `Features/Settings/SettingsPage.xaml.cs`, replace `OnDraftRulesClicked` with:

```csharp
private void OnManagePacksClicked(object sender, RoutedEventArgs e)
{
    try
    {
        var vm = App.Services.GetRequiredService<Features.Rules.Editor.RulePackManagerViewModel>();
        var window = new Features.Rules.Editor.RulePackManagerWindow(vm) { Owner = Window.GetWindow(this) };
        window.ShowDialog();
    }
    catch (Exception ex)
    {
        MessageBox.Show("Unexpected error: " + ex.Message);
    }
}
```

(The per-topic rail "Draft rules from this topic…" entry and `DraftRulesDialog` are unchanged; only the Settings button moves into the manager.)

- [ ] **Step 4: Build**

Run: `dotnet build NtfyDesktop.csproj`
Expected: Build succeeds.

- [ ] **Step 5: Manual verification (run the app)**

Confirm each, then stop for the maintainer to test:
1. Settings → Rules → **Manage rule packs…** opens the window; existing packs (from `App.DataPath\rules`) appear in the left list.
2. **New pack → Blank**, set a name, **Add rule → Match**, fill a title regex + suppress-toast, **Save** → a JSON file appears in the rules folder and the rule takes effect on the next matching message.
3. Toggle a pack's enable off, **Save**, send a matching message → it is no longer suppressed (engine ignores the disabled pack); toggle a single rule off → only that rule stops.
4. **New pack → Draft with AI…** opens the existing draft dialog; saving from it adds a pack that appears in the manager list after it closes.
5. Select a pack, pick a topic + count, **Preview** → shows the would-hide/fold/absence summary; **Apply** → confirm dialog, then matching history rows disappear from the feed (visible again via "Show suppressed") and the unread badge drops.
6. **Delete pack** removes its file; **Cancel** discards unsaved edits.

- [ ] **Step 6: Commit**

```bash
git add Features/Rules/Editor/RulePackManagerWindow.xaml Features/Rules/Editor/RulePackManagerWindow.xaml.cs Features/Settings/SettingsPage.xaml Features/Settings/SettingsPage.xaml.cs
git commit -m "feat(rules): in-app rule-pack manager window + Settings entry point"
```

---

## Final wiring & docs

- [ ] **Update `README.md` roadmap:** tick the 0.75 "Phase 2 — in-app rule-pack management UI" checkbox.
- [ ] **Update `ARCHITECTURE.md`:** add a short "Rules → Pack manager (Phase 2)" note covering the `enabled`/`id` JSON fields, `PackStore` enabled-filtering vs `GetEditablePacks`, `PackWriter`, the history simulator/preview, and additive apply.
- [ ] **Run the full suite once more:** `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj` → all green.
- [ ] **Commit** the docs.

```bash
git add README.md ARCHITECTURE.md
git commit -m "docs: mark 0.75 Phase 2 (rule-pack manager) complete; architecture notes"
```
