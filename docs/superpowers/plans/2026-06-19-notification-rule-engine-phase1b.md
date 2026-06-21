# Notification Rule Engine — Phase 1b Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Detect when *expected* recurring messages stop arriving (the dead-man's-switch: "alert me when the backup-success messages stop") and raise a synthetic high-priority alert.

**Architecture:** A new `expect` rule type in the pack format (`when` matcher + `every`/`grace` durations + `onAbsence`/optional `onRecovery` alert specs). A `BackgroundService` (`ExpectationMonitor`) tracks per-rule last-seen times in an encrypted SQLite table, updates them from every newly-stored message (live or backfill, via the `MessageInserted` bus event), and on a timer raises an alert for any rule overdue beyond `every + grace`. The alert is a synthetic message stored in history (feed + unread) and pushed through the normal toast pipeline, under the watched topic. De-dupes (one alert per outage); re-arms when a matching message resumes, optionally firing a "recovered" notice.

**Tech Stack:** .NET 10, `Microsoft.Data.Sqlite.Core` + `SQLite3MC`, `Microsoft.Extensions.Hosting` `BackgroundService`, xUnit.

## Scope

Phase 1b only. Builds on the Phase 1a engine (same branch `feature/notification-rule-engine`). The deterministic, timing-independent pieces (duration parsing, expect-rule parsing, matcher reuse, the store, the overdue check) are TDD'd; the `ExpectationMonitor` wiring (timer + bus + synthetic-message injection) is build- + running-app-verified, consistent with how 1a's pipeline glue was handled.

**Deferred:** Phase 1c (AI authoring), Phase 2 (UI), stateful "open incidents" view.

## Design decisions (confirmed with maintainer)

- **Alert surfaces as toast + feed entry**, stored as a synthetic message so it's browsable and bumps unread.
- **Alert belongs to the watched topic** (the topic the matching messages came from).
- **Recovery is configurable *per rule*** via an optional `onRecovery` alert spec in the pack (present → "resumed" notice; omitted → silent re-arm). This keeps the choice in the declarative pack rather than a global app toggle — flag for confirmation; trivial to move to a global setting if preferred.

## Global Constraints

(Same as Phase 1a.) `net10.0-windows10.0.17763.0`, `Nullable`/`ImplicitUsings` enabled. Feature-isolated under `Features/Rules/`. Event bus: publish concrete types; `BackgroundService` registered via `services.AddHostedService<T>()`. New SQLite data encrypted via `PRAGMA key` (first statement per connection), key from `AppSettings.GetOrCreateHistoryKey()`. Engine **fails open**. Build: `dotnet build NtfyDesktop.csproj`. Test: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`. One conventional-commit line per task. Green build is not the gate — pipeline/UI changes verified in the running app (maintainer runs it from VS/Rider; **don't commit those until confirmed**).

## File Structure

**New:**
- `Features/Rules/Duration.cs` — parse `"26h"`/`"90m"`/`"2d"`/`"30s"` → `TimeSpan`.
- `Features/Rules/Model/Expect.cs` — `AlertSpec`, `ExpectRule` records.
- `Features/Rules/ExpectationStore.cs` — encrypted per-rule last-seen / alerted state (second table in `rules.db`).
- `Features/Rules/ExpectationEvaluator.cs` — pure "is overdue" check.
- `Features/Rules/ExpectationMonitor.cs` — `BackgroundService`: timer + `MessageInserted` subscription + synthetic alert injection.
- Test files under `NtfyDesktop.Tests/Rules/`.

**Modified:**
- `Features/Rules/Model/Matcher.cs` — add a primitive-field `Matches(...)` overload (so a `HistoryMessage` can be matched, not just an `NtfyMessage`).
- `Features/Rules/Model/Rules.cs` — `RulePack` gains `ExpectRules`.
- `Features/Rules/PackParser.cs` — parse `expect` rules.
- `Features/Rules/PackStore.cs` — (no change; already loads whatever the parser returns).
- `Features/Rules/RulesFeature.cs` — register `ExpectationStore` + the hosted `ExpectationMonitor`.

---

## Task B1: Duration parsing

**Files:** Create `Features/Rules/Duration.cs`; Test `NtfyDesktop.Tests/Rules/DurationTests.cs`.

**Interfaces:** Produces `static class Duration { static bool TryParse(string? text, out TimeSpan value); }`. Accepts `<number><unit>` with unit `s|m|h|d` (case-insensitive), e.g. `"26h"`, `"90m"`, `"2d"`, `"45s"`. Returns false on null/empty/garbage.

- [ ] **Step 1: Write the failing tests**

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class DurationTests
{
    [Theory]
    [InlineData("45s", 45)]
    [InlineData("90m", 90 * 60)]
    [InlineData("26h", 26 * 3600)]
    [InlineData("2d", 2 * 86400)]
    [InlineData("1H", 3600)]
    public void TryParse_Valid(string text, int expectedSeconds)
    {
        Assert.True(Duration.TryParse(text, out var value));
        Assert.Equal(expectedSeconds, (int)value.TotalSeconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("10")]
    [InlineData("10x")]
    [InlineData("-5h")]
    public void TryParse_Invalid(string? text)
    {
        Assert.False(Duration.TryParse(text, out _));
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (`Duration` missing).
Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter DurationTests`

- [ ] **Step 3: Implement**

```csharp
namespace NtfyDesktop.Features.Rules;

/// <summary>Parses compact durations like "26h", "90m", "2d", "45s" into a TimeSpan.</summary>
public static class Duration
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return false;

        var unit = char.ToLowerInvariant(text[^1]);
        if (!int.TryParse(text[..^1], out var n) || n < 0) return false;

        value = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => TimeSpan.Zero,
        };
        return value > TimeSpan.Zero || (n == 0 && "smhd".Contains(unit));
    }
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add compact duration parser"`

---

## Task B2: Expect rule model + parsing

**Files:** Create `Features/Rules/Model/Expect.cs`; Modify `Features/Rules/Model/Rules.cs`, `Features/Rules/PackParser.cs`; Test `NtfyDesktop.Tests/Rules/PackParserExpectTests.cs`.

**Interfaces:**
- `AlertSpec(Priority Priority, string Title, string? Message)`.
- `ExpectRule(string Id, Matcher When, TimeSpan Every, TimeSpan Grace, AlertSpec OnAbsence, AlertSpec? OnRecovery)`.
- `RulePack` gains `IReadOnlyList<ExpectRule> ExpectRules`.
- Parser: `type: "expect"` → builds an `ExpectRule`. `every` required (skip rule if missing/invalid — fail open). `grace` optional (default 0). `onAbsence` required (skip rule if missing). `onRecovery` optional. `Id = "<packName>#<index>"`.

Pack JSON:
```jsonc
{ "type": "expect",
  "when":      { "topic": "backups", "titleRegex": "succeeded" },
  "every":     "26h",
  "grace":     "1h",
  "onAbsence": { "priority": "urgent", "title": "Backup heartbeat missed",
                 "message": "No success on 'backups' in over 26h" },
  "onRecovery":{ "priority": "default", "title": "Backups resumed" } }
```

- [ ] **Step 1: Write the failing tests**

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackParserExpectTests
{
    [Fact]
    public void Parse_ExpectRule_Full()
    {
        const string json = """
            { "name": "Backups", "rules": [
              { "type": "expect",
                "when": { "topic": "backups", "titleRegex": "succeeded" },
                "every": "26h", "grace": "1h",
                "onAbsence": { "priority": "urgent", "title": "Backup missed", "message": "none in 26h" },
                "onRecovery": { "priority": "default", "title": "Backups resumed" } } ] }
            """;

        var rule = Assert.Single(PackParser.Parse(json).ExpectRules);
        Assert.Equal("Backups#0", rule.Id);
        Assert.Equal("backups", rule.When.Topic);
        Assert.Equal(TimeSpan.FromHours(26), rule.Every);
        Assert.Equal(TimeSpan.FromHours(1), rule.Grace);
        Assert.Equal(Priority.Urgent, rule.OnAbsence.Priority);
        Assert.Equal("Backup missed", rule.OnAbsence.Title);
        Assert.NotNull(rule.OnRecovery);
        Assert.Equal("Backups resumed", rule.OnRecovery!.Title);
    }

    [Fact]
    public void Parse_ExpectRule_MinimalDefaults()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "expect", "when": { "topic": "x" }, "every": "1h",
                "onAbsence": { "title": "gone" } } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).ExpectRules);
        Assert.Equal(TimeSpan.Zero, rule.Grace);
        Assert.Equal(Priority.High, rule.OnAbsence.Priority); // default
        Assert.Null(rule.OnRecovery);
    }

    [Fact]
    public void Parse_ExpectRule_InvalidEvery_Skipped()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "expect", "when": { "topic": "x" }, "every": "soon",
                "onAbsence": { "title": "gone" } } ] }
            """;
        Assert.Empty(PackParser.Parse(json).ExpectRules);
    }

    [Fact]
    public void Parse_ExpectRule_MissingOnAbsence_Skipped()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "expect", "when": { "topic": "x" }, "every": "1h" } ] }
            """;
        Assert.Empty(PackParser.Parse(json).ExpectRules);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (`ExpectRules` / `ExpectRule` missing).

- [ ] **Step 3: Create `Features/Rules/Model/Expect.cs`**

```csharp
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Rules.Model;

/// <summary>A notification the engine raises itself (absence or recovery).</summary>
public sealed record AlertSpec(Priority Priority, string Title, string? Message);

/// <summary>
/// "I expect a message matching <see cref="When"/> at least every <see cref="Every"/>
/// (plus <see cref="Grace"/>); alert via <see cref="OnAbsence"/> when overdue. If
/// <see cref="OnRecovery"/> is set, also notify when matching messages resume after
/// an alert. <see cref="Id"/> namespaces the rule's saved state (pack name + index).
/// </summary>
public sealed record ExpectRule(
    string Id,
    Matcher When,
    TimeSpan Every,
    TimeSpan Grace,
    AlertSpec OnAbsence,
    AlertSpec? OnRecovery);
```

- [ ] **Step 4: Add `ExpectRules` to `RulePack`** in `Features/Rules/Model/Rules.cs`

```csharp
public sealed record RulePack(
    string Name,
    IReadOnlyList<MatchRule> MatchRules,
    IReadOnlyList<CorrelateRule> CorrelateRules,
    IReadOnlyList<ExpectRule> ExpectRules);
```

- [ ] **Step 5: Update `PackParser`** — add the expect collection, the `expect` case, and an `AlertSpec` parser. In `Parse`, add `var expectRules = new List<ExpectRule>();`, add the case below, and pass `expectRules` to the `RulePack`:

```csharp
                    case "expect":
                        if (TryParseExpect(rule, name, index) is { } expect)
                            expectRules.Add(expect);
                        break;
```

Add these helpers to `PackParser`:

```csharp
    private static ExpectRule? TryParseExpect(JsonElement rule, string packName, int index)
    {
        // every is required and must be a valid duration; onAbsence is required.
        if (!Duration.TryParse(Str(rule, "every"), out var every)) return null;
        var onAbsence = ParseAlert(rule, "onAbsence");
        if (onAbsence is null) return null;

        Duration.TryParse(Str(rule, "grace"), out var grace); // absent/invalid → Zero

        return new ExpectRule(
            Id: $"{packName}#{index}",
            When: ParseMatcher(rule, "when"),
            Every: every,
            Grace: grace,
            OnAbsence: onAbsence,
            OnRecovery: ParseAlert(rule, "onRecovery"));
    }

    private static AlertSpec? ParseAlert(JsonElement rule, string property)
    {
        if (!rule.TryGetProperty(property, out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        var title = Str(a, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;
        return new AlertSpec(ParsePriority(Str(a, "priority")) ?? Priority.High, title, Str(a, "message"));
    }
```

Update the `RulePack` construction at the end of `Parse` to include `expectRules`. Update the three existing tests that build `RulePack` directly (`RuleEngineMatchTests.Pack`, `RuleEngineCorrelateTests`) — add `[]` as the fourth arg (`new RulePack(name, match, [], [])` / `new RulePack("zabbix", [], [rule], [])`).

- [ ] **Step 6: Run — expect PASS** (`PackParserExpectTests` + the whole suite green).

- [ ] **Step 7: Commit** — `git commit -m "feat(rules): parse expect (heartbeat) rules"`

---

## Task B3: Matcher overload for stored messages

The monitor matches `HistoryMessage`s (from `MessageInserted`), which have a different shape than `NtfyMessage` (tags are a comma string). Add a primitive-field overload.

**Files:** Modify `Features/Rules/Model/Matcher.cs`; Test `NtfyDesktop.Tests/Rules/MatcherFieldsTests.cs`.

**Interfaces:** `bool Matches(string topic, string? title, string? body, Priority priority, IReadOnlyList<string>? tags)`; the existing `Matches(NtfyMessage)` delegates to it.

- [ ] **Step 1: Write the failing test**

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class MatcherFieldsTests
{
    [Fact]
    public void Matches_PrimitiveFields()
    {
        var m = new Matcher { Topic = "backups", TitleRegex = "succeeded", Tag = "ok" };
        Assert.True(m.Matches("backups", "Backup succeeded", body: null, Priority.Default, ["ok"]));
        Assert.False(m.Matches("alerts", "Backup succeeded", null, Priority.Default, ["ok"]));
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (no such overload).

- [ ] **Step 3: Refactor `Matcher.Matches`** so the logic lives in the primitive overload and the `NtfyMessage` one delegates:

```csharp
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
```

- [ ] **Step 4: Run — expect PASS** (`MatcherFieldsTests` + the existing `MatcherTests` still green).
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add primitive-field Matcher overload"`

---

## Task B4: ExpectationStore (SQLite, encrypted)

Per-rule last-seen + alerted state, in a second table inside the existing `rules.db`.

**Files:** Create `Features/Rules/ExpectationStore.cs`; Test `NtfyDesktop.Tests/Rules/ExpectationStoreTests.cs`.

**Interfaces:**
- `record ExpectState(string RuleId, long LastSeenAt, Guid TopicId, bool Alerted)`.
- `ExpectationStore(string dbPath, string password)`.
- `ExpectState? Get(string ruleId)`.
- `void Seed(string ruleId, long lastSeenAt)` — INSERT-OR-IGNORE initial state (`alerted=0`, topic empty).
- `bool RecordSeen(string ruleId, long lastSeenAt, Guid topicId)` — upsert: advance `last_seen_at` forward-only, set `topic_id`, clear `alerted`; **returns the previous `alerted` value** (so the caller can fire a recovery notice).
- `void MarkAlerted(string ruleId)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class ExpectationStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ExpectationStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfyexp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "rules.db");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ExpectationStore New() => new(_dbPath, "k");

    [Fact]
    public void Get_Null_WhenEmpty() => Assert.Null(New().Get("r"));

    [Fact]
    public void Seed_ThenGet()
    {
        var s = New();
        s.Seed("r", 1000);
        var state = s.Get("r");
        Assert.NotNull(state);
        Assert.Equal(1000, state!.LastSeenAt);
        Assert.False(state.Alerted);
    }

    [Fact]
    public void Seed_DoesNotOverwriteExisting()
    {
        var s = New();
        s.Seed("r", 1000);
        s.Seed("r", 5000);
        Assert.Equal(1000, s.Get("r")!.LastSeenAt);
    }

    [Fact]
    public void RecordSeen_AdvancesAndClearsAlerted_ReturnsPrevAlerted()
    {
        var s = New();
        s.Seed("r", 1000);
        s.MarkAlerted("r");
        Assert.True(s.Get("r")!.Alerted);

        var wasAlerted = s.RecordSeen("r", 2000, Guid.Parse("11111111-1111-1111-1111-111111111111"));
        Assert.True(wasAlerted);
        var state = s.Get("r")!;
        Assert.Equal(2000, state.LastSeenAt);
        Assert.False(state.Alerted);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), state.TopicId);
    }

    [Fact]
    public void RecordSeen_ForwardOnly()
    {
        var s = New();
        s.RecordSeen("r", 5000, Guid.Empty);
        s.RecordSeen("r", 2000, Guid.Empty);
        Assert.Equal(5000, s.Get("r")!.LastSeenAt);
    }
}
```

- [ ] **Step 2: Run — expect FAIL** (`ExpectationStore` missing).

- [ ] **Step 3: Implement `Features/Rules/ExpectationStore.cs`** (mirrors `IncidentStore`'s encryption):

```csharp
using System.IO;
using Microsoft.Data.Sqlite;

namespace NtfyDesktop.Features.Rules;

public sealed record ExpectState(string RuleId, long LastSeenAt, Guid TopicId, bool Alerted);

/// <summary>
/// Per-expect-rule heartbeat state (last-seen time, the topic it was seen on, and whether
/// an absence alert is currently outstanding). A second table in the encrypted rules.db.
/// </summary>
public sealed class ExpectationStore : IIncidentStore // marker not needed; see note
{
    // NOTE: does not implement IIncidentStore — remove that base. (Left here only to flag:
    // this is a sibling store, independent of IncidentStore.)
}
```

> Implementation note for the engineer: the class is standalone (no interface). Use:

```csharp
using System.IO;
using Microsoft.Data.Sqlite;

namespace NtfyDesktop.Features.Rules;

public sealed record ExpectState(string RuleId, long LastSeenAt, Guid TopicId, bool Alerted);

/// <summary>
/// Per-expect-rule heartbeat state (last-seen time, the topic last seen on, and whether an
/// absence alert is outstanding). A second table in the encrypted rules.db.
/// </summary>
public sealed class ExpectationStore
{
    private readonly string _dbPath;
    private readonly string _password;

    public ExpectationStore(string dbPath, string password)
    {
        _dbPath = dbPath;
        _password = password;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS expectations (
                rule_id      TEXT    PRIMARY KEY,
                last_seen_at INTEGER NOT NULL,
                topic_id     TEXT    NOT NULL DEFAULT '',
                alerted      INTEGER NOT NULL DEFAULT 0
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public ExpectState? Get(string ruleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_seen_at, topic_id, alerted FROM expectations WHERE rule_id = @r LIMIT 1";
        cmd.Parameters.AddWithValue("@r", ruleId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var topicId = Guid.TryParse(reader.GetString(1), out var g) ? g : Guid.Empty;
        return new ExpectState(ruleId, reader.GetInt64(0), topicId, reader.GetInt64(2) != 0);
    }

    public void Seed(string ruleId, long lastSeenAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expectations (rule_id, last_seen_at, topic_id, alerted)
            VALUES (@r, @t, '', 0) ON CONFLICT(rule_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@r", ruleId);
        cmd.Parameters.AddWithValue("@t", lastSeenAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Upsert last-seen forward-only, set topic, clear alerted. Returns the
    /// previous alerted value so the caller can fire a recovery notice.</summary>
    public bool RecordSeen(string ruleId, long lastSeenAt, Guid topicId)
    {
        using var conn = Open();

        bool wasAlerted;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "SELECT alerted FROM expectations WHERE rule_id = @r LIMIT 1";
            read.Parameters.AddWithValue("@r", ruleId);
            var res = read.ExecuteScalar();
            wasAlerted = res is not (null or DBNull) && Convert.ToInt64(res) != 0;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO expectations (rule_id, last_seen_at, topic_id, alerted)
            VALUES (@r, @t, @tid, 0)
            ON CONFLICT(rule_id) DO UPDATE SET
                last_seen_at = MAX(last_seen_at, excluded.last_seen_at),
                topic_id     = excluded.topic_id,
                alerted      = 0
            """;
        cmd.Parameters.AddWithValue("@r", ruleId);
        cmd.Parameters.AddWithValue("@t", lastSeenAt);
        cmd.Parameters.AddWithValue("@tid", topicId.ToString());
        cmd.ExecuteNonQuery();
        return wasAlerted;
    }

    public void MarkAlerted(string ruleId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE expectations SET alerted = 1 WHERE rule_id = @r";
        cmd.Parameters.AddWithValue("@r", ruleId);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA key = '" + _password.Replace("'", "''") + "'";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
```

(Delete the placeholder class shown above the note; keep only this standalone version.)

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add encrypted ExpectationStore"`

---

## Task B5: Overdue evaluator (pure)

**Files:** Create `Features/Rules/ExpectationEvaluator.cs`; Test `NtfyDesktop.Tests/Rules/ExpectationEvaluatorTests.cs`.

**Interfaces:** `static bool IsOverdue(long lastSeenAtUnix, TimeSpan every, TimeSpan grace, DateTimeOffset now)`.

- [ ] **Step 1: Write the failing tests**

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class ExpectationEvaluatorTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    [Fact]
    public void NotOverdue_WithinWindow()
    {
        // last seen at Base; every 1h, grace 10m; now = Base + 50m → not overdue
        Assert.False(ExpectationEvaluator.IsOverdue(
            Base.ToUnixTimeSeconds(), TimeSpan.FromHours(1), TimeSpan.FromMinutes(10),
            Base.AddMinutes(50)));
    }

    [Fact]
    public void Overdue_PastWindowPlusGrace()
    {
        Assert.True(ExpectationEvaluator.IsOverdue(
            Base.ToUnixTimeSeconds(), TimeSpan.FromHours(1), TimeSpan.FromMinutes(10),
            Base.AddMinutes(71)));
    }

    [Fact]
    public void NotOverdue_ExactlyAtBoundary()
    {
        Assert.False(ExpectationEvaluator.IsOverdue(
            Base.ToUnixTimeSeconds(), TimeSpan.FromHours(1), TimeSpan.Zero, Base.AddHours(1)));
    }
}
```

- [ ] **Step 2: Run — expect FAIL.**

- [ ] **Step 3: Implement**

```csharp
namespace NtfyDesktop.Features.Rules;

/// <summary>Pure timing check for expect rules — kept separate so it's unit-testable
/// with an injected "now" (no real waiting).</summary>
public static class ExpectationEvaluator
{
    public static bool IsOverdue(long lastSeenAtUnix, TimeSpan every, TimeSpan grace, DateTimeOffset now)
    {
        var deadline = DateTimeOffset.FromUnixTimeSeconds(lastSeenAtUnix) + every + grace;
        return now > deadline;
    }
}
```

- [ ] **Step 4: Run — expect PASS.**
- [ ] **Step 5: Commit** — `git commit -m "feat(rules): add overdue evaluator"`

---

## Task B6: ExpectationMonitor (BackgroundService)

The timing + delivery glue. **Not unit-tested** (timer + bus + history I/O); verified by build + the running-app test in Task B8.

**Files:** Create `Features/Rules/ExpectationMonitor.cs`.

**Behaviour:**
- Subscribes to `MessageInserted`. For each loaded expect rule whose `When` matches the stored message, `RecordSeen(rule.Id, time, topicId)`; if it had been alerted and the rule has `OnRecovery`, fire the recovery notice.
- `ExecuteAsync`: record `_monitorStart = now`; seed any rule lacking state with `last_seen = now`; then a `PeriodicTimer` (60s) scans. A rule fires its absence alert when: rules enabled, past the startup grace (`now > _monitorStart + StartupGrace`, 2 min — lets catch-up settle), not already alerted, and `IsOverdue`. After firing, `MarkAlerted`.
- An alert is delivered by constructing a synthetic `NtfyMessage`, `HistoryRepository.Insert`-ing it (→ feed + unread via `MessageInserted`), then publishing `NtfyMessageReceived` (→ toast via `ShowToastNotification`, respecting pause/active-hours). Topic = the watched topic (from saved state's `TopicId`, else resolved from the rule's `when.topic` name, else `Guid.Empty` = All-topics).

- [ ] **Step 1: Implement `Features/Rules/ExpectationMonitor.cs`**

```csharp
using Microsoft.Extensions.Hosting;
using NtfyDesktop.Core.Messaging;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Connections;
using NtfyDesktop.Features.History;
using NtfyDesktop.Features.History.Events;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Heartbeat / dead-man's-switch monitor. Tracks when each expect rule last saw a matching
/// message and raises a synthetic alert when one is overdue. Updates last-seen from every
/// newly-stored message (live or backfill) via MessageInserted, so a reconnect's catch-up
/// counts; a startup grace keeps it from false-firing before catch-up settles.
/// </summary>
public sealed class ExpectationMonitor : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);

    private readonly PackStore _packs;
    private readonly ExpectationStore _store;
    private readonly HistoryRepository _history;
    private readonly AppSettings _settings;
    private DateTimeOffset _monitorStart;

    public ExpectationMonitor(PackStore packs, ExpectationStore store,
        HistoryRepository history, AppSettings settings, EventBus bus)
    {
        _packs = packs;
        _store = store;
        _history = history;
        _settings = settings;
        bus.Subscribe<MessageInserted>(this, e => OnMessageInserted(e.Message));
    }

    private IEnumerable<ExpectRule> Rules() => _packs.Packs.SelectMany(p => p.ExpectRules);

    private void OnMessageInserted(HistoryMessage m)
    {
        if (!_settings.RulesEnabled) return;

        var tags = string.IsNullOrEmpty(m.Tags) ? null
            : m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rule in Rules())
        {
            try
            {
                if (!rule.When.Matches(m.Topic, m.Title, m.Body, m.Priority, tags)) continue;

                var wasAlerted = _store.RecordSeen(rule.Id, m.Timestamp.ToUnixTimeSeconds(), m.TopicId);
                if (wasAlerted && rule.OnRecovery is { } recovery)
                    RaiseAlert(recovery, m.TopicId);
            }
            catch { /* fail open */ }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _monitorStart = DateTimeOffset.UtcNow;

        // Seed any rule without state so a brand-new rule doesn't instantly fire.
        foreach (var rule in Rules())
            if (_store.Get(rule.Id) is null)
                _store.Seed(rule.Id, _monitorStart.ToUnixTimeSeconds());

        var timer = new PeriodicTimer(ScanInterval);
        try
        {
            do { Scan(); } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void Scan()
    {
        if (!_settings.RulesEnabled) return;

        var now = DateTimeOffset.UtcNow;
        if (now < _monitorStart + StartupGrace) return; // let catch-up settle

        foreach (var rule in Rules())
        {
            try
            {
                var state = _store.Get(rule.Id);
                if (state is null) { _store.Seed(rule.Id, now.ToUnixTimeSeconds()); continue; }
                if (state.Alerted) continue;
                if (!ExpectationEvaluator.IsOverdue(state.LastSeenAt, rule.Every, rule.Grace, now)) continue;

                RaiseAlert(rule.OnAbsence, state.TopicId);
                _store.MarkAlerted(rule.Id);
            }
            catch { /* fail open */ }
        }
    }

    // Stores a synthetic message (feed + unread) and pushes it through the toast pipeline.
    private void RaiseAlert(AlertSpec spec, Guid topicId)
    {
        var topic = _settings.GetTopicById(topicId);
        var topicName = topic?.Name ?? "rules";
        var serverId = topic?.ServerId ?? Guid.Empty;

        var synthetic = new NtfyMessage
        {
            Id = $"rule-alert-{Guid.NewGuid():N}",
            Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Topic = topicName,
            Priority = spec.Priority,
            Title = spec.Title,
            Message = spec.Message,
        };

        // Insert → MessageInserted (feed + unread). Then NtfyMessageReceived → toast
        // (honours pause / active-hours like any message). Not suppressed.
        if (_history.Insert(synthetic, topicId, serverId))
            _ = new NtfyMessageReceived(synthetic, topicId).PublishAsync();
    }
}
```

- [ ] **Step 2: Build** — `dotnet build NtfyDesktop.csproj` → succeeds. (Registration is Task B7; the type just needs to compile.)
- [ ] **Step 3: Commit** — `git commit -m "feat(rules): add ExpectationMonitor heartbeat service"`

---

## Task B7: Register the monitor + store in DI

**Files:** Modify `Features/Rules/RulesFeature.cs`.

- [ ] **Step 1: Add registrations** inside `AddRules()`:

```csharp
            services.AddSingleton<ExpectationStore>(sp => new ExpectationStore(
                Path.Combine(App.DataPath, "rules.db"),
                sp.GetRequiredService<AppSettings>().GetOrCreateHistoryKey()));

            services.AddHostedService<ExpectationMonitor>();
```

- [ ] **Step 2: Build** — `dotnet build NtfyDesktop.csproj` → succeeds.
- [ ] **Step 3: Run the full test suite** — `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj` → all green.
- [ ] **Step 4: Commit** — `git commit -m "feat(rules): register ExpectationStore and ExpectationMonitor"`

---

## Task B8: Running-app verification

The real gate (maintainer runs it). **Hold commits for the integration tasks (B6–B7) until confirmed**, per protocol.

- [ ] **Step 1:** Add an expect rule to `%AppData%\NtfyDesktop\rules\test.json`, with a **short** window for testing:

```json
{
  "name": "Heartbeat",
  "rules": [
    {
      "type": "expect",
      "when": { "topic": "<your-test-topic>", "titleRegex": "tick" },
      "every": "2m",
      "grace": "30s",
      "onAbsence": { "priority": "urgent", "title": "Heartbeat stopped", "message": "No 'tick' in over 2m" },
      "onRecovery": { "priority": "default", "title": "Heartbeat resumed" }
    }
  ]
}
```

- [ ] **Step 2:** Restart the app (packs load at startup). Publish a `tick` message to the topic to establish last-seen.
- [ ] **Step 3:** Wait > ~2m30s + the 2-minute startup grace **without** sending `tick`. Expect: an **"Heartbeat stopped"** toast fires once (not repeatedly), and a matching entry appears in the feed + unread badge, under the watched topic.
- [ ] **Step 4:** Publish another `tick`. Expect: a **"Heartbeat resumed"** toast (because `onRecovery` is set), and the monitor re-arms.
- [ ] **Step 5:** Confirm it does **not** re-fire every scan while overdue (only once per outage), and that with `RulesEnabled` off nothing fires.
- [ ] **Step 6:** On confirmation, commit any held integration changes.

---

## Self-Review

**Spec coverage (Phase 1b):** expect rule format → B2; duration parsing → B1; matcher reuse for stored messages → B3; per-rule last-seen state, encrypted, surviving restart → B4; overdue detection → B5; synthetic toast+feed alert under the watched topic, backfill-fed last-seen, startup grace, de-dupe/re-arm, optional recovery → B6/B7; verification → B8.

**Placeholder scan:** none — Task B4 contains a deliberately-flagged placeholder/“use this instead” pair; the engineer keeps only the standalone `ExpectationStore` version.

**Type consistency:** `ExpectRule`, `AlertSpec`, `RulePack.ExpectRules`, `ExpectationStore.{Get,Seed,RecordSeen,MarkAlerted}`, `ExpectState`, `ExpectationEvaluator.IsOverdue`, and `Matcher.Matches(primitive…)` are used consistently across B1–B7. `RulePack`'s new fourth constructor arg requires updating the three existing direct constructions (noted in B2 Step 5).
