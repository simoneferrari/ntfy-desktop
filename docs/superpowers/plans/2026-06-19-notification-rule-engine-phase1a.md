# Notification Rule Engine — Phase 1a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the deterministic core of the notification rule engine — declarative JSON rule packs, a pure match + correlate engine, and pipeline integration that suppresses noise toasts and hides suppressed messages from the feed and unread count.

**Architecture:** A new isolated `Features/Rules/` feature. Incoming messages are evaluated by a pure `RuleEngine` (match rules → suppress/tag; correlate rules → suppress a "resolved" only when its matching "problem" incident is open). The verdict is persisted on the message row (`suppressed` column) and threaded through the existing pipeline so the toast is dropped, the feed hides it by default, and the unread badge ignores it. Tool knowledge lives entirely in JSON packs (data, not code). The engine never blocks the socket or the history write — it only affects toasting/routing, and it fails open.

**Tech Stack:** .NET 10 (`net10.0-windows10.0.17763.0`), C# with `Nullable` enabled, `System.Text.Json` for pack parsing, `Microsoft.Data.Sqlite.Core` + `SQLite3MC` for the encrypted incident store, xUnit for tests (new test project).

## Scope

This plan covers **Phase 1a only** (per the design doc
`docs/superpowers/specs/2026-06-19-notification-rule-engine-design.md`): match +
correlate engine, suppression verdict, pipeline integration, feed/unread hiding.

**Explicitly deferred to later plans:**
- **Phase 1b** — `expect`/heartbeat absence detection (`ExpectationMonitor`).
- **Phase 1c** — AI authoring (`PackDraftService`, OpenAI-compatible endpoint settings).
- **Phase 2** — in-app rule-builder UI.
- The `digest` action and the `dismissOriginal` action are **out of scope for 1a**.
  The pack parser tolerates unknown action strings (fail-open), so packs that use
  them won't break — the actions simply have no effect yet.

## Global Constraints

Copied verbatim from the codebase conventions; every task implicitly includes these.

- **TFM:** `net10.0-windows10.0.17763.0`. `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- **Feature isolation:** all new code lives under `Features/Rules/`; the feature registers its own services via an `AddRules()` extension on `IServiceCollection`, wired in `Features/AppCompositionExtensions.cs`. `Domain/` is for cross-feature types only.
- **Event bus:** cross-component signals implement `IEvent` and are published with `new SomeEvent(...).PublishAsync()`. Always publish the **concrete** type (publishing through an `IEvent`-typed variable is a silent no-op). UI subscribers use `bus.Subscribe<TEvent>(this, handler, ThreadOption.UIThread)`.
- **DI-resolved handlers:** a class implementing `IEventHandler<TEvent>` is auto-registered as transient (assembly scan in `Core/Messaging/DependencyInjection.cs`).
- **Encryption at rest:** any new SQLite database storing message-derived data is encrypted with SQLite3MC via `PRAGMA key` (NOT the `Password` connection-string keyword — it doesn't round-trip with the default ChaCha20-Poly1305 cipher). `PRAGMA key` must be the first statement on every connection. Key source: `AppSettings.GetOrCreateHistoryKey()`.
- **Build:** `dotnet build NtfyDesktop.csproj`. **Test:** `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`.
- **Green build is not the gate** — changes touching the UI/pipeline must be verified in the running app.
- **Commits:** one short conventional-commit line per task via `git commit -m "..."`. (Multi-line messages must use the `-F <file>` method per the maintainer's global convention; single lines below are fine inline.)

## File Structure

**New files (all testable-pure unless noted):**
- `NtfyDesktop.Tests/NtfyDesktop.Tests.csproj` — xUnit test project (references the main project).
- `Features/Rules/Model/Matcher.cs` — predicate over an `NtfyMessage` (topic, min priority, title/body regex, tag).
- `Features/Rules/Model/KeySelector.cs` — extracts a correlation key (named-capture regex over title/body).
- `Features/Rules/Model/RuleAction.cs` — `RuleAction` record + `RuleActionKind` enum.
- `Features/Rules/Model/Rules.cs` — `MatchRule`, `CorrelateRule`, `RulePack` records.
- `Features/Rules/Model/RuleVerdict.cs` — `RuleVerdict` + `IncidentOpen` records.
- `Features/Rules/PackParser.cs` — pure JSON → `RulePack` parsing (fail-open per rule).
- `Features/Rules/IIncidentStore.cs` — incident store interface + `Incident` record.
- `Features/Rules/IncidentStore.cs` — SQLite (encrypted) implementation; path + password injected (test-friendly).
- `Features/Rules/PackStore.cs` — loads `*.json` packs from a directory (path injected).
- `Features/Rules/RuleEngine.cs` — pure evaluation; reads packs + incident store.
- `Features/Rules/RulesFeature.cs` — `AddRules()` DI extension.

**Modified files:**
- `Features/Settings/AppSettings.cs` — add `RulesEnabled` (master toggle).
- `Features/History/HistoryRepository.cs` — `suppressed` column; `Insert` gains a `suppressed` param; `Query` gains `includeSuppressed`; `GetUnreadCounts` excludes suppressed; row mapping.
- `Features/History/HistoryMessage.cs` — add `Suppressed`.
- `Features/Connections/NtfyMessageReceived.cs` — add `Suppressed`.
- `Features/Connections/ConnectionManager.cs` — evaluate the engine, persist verdict, apply incident side-effects, carry `Suppressed`.
- `Features/Notifications/ShowToastNotification.cs` — drop suppressed messages.
- `Features/Unread/UnreadTracker.cs` — ignore suppressed in the incremental count.
- `Features/Feed/FeedViewModel.cs` — `ShowSuppressed` property + query wiring.
- `Features/Feed/FeedPage.xaml` — "Show suppressed" toggle.
- `Features/AppCompositionExtensions.cs` — call `AddRules()`.

---

## Task 1: Bootstrap the test project

There is no test project yet. This task creates one so every later task can be TDD'd.

**Files:**
- Create: `NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`
- Create: `NtfyDesktop.Tests/SanityTest.cs`

**Interfaces:**
- Produces: a runnable `dotnet test` target referencing the `NtfyDesktop` project, so later tasks can `using NtfyDesktop.Features.Rules;`.

- [ ] **Step 1: Create the test project file**

Create `NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- Must match the referenced project's TFM: it targets net10.0-windows, and a
         plain net10.0 test project can't reference a net10.0-windows assembly. -->
    <TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\NtfyDesktop.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write a sanity test**

Create `NtfyDesktop.Tests/SanityTest.cs`:

```csharp
namespace NtfyDesktop.Tests;

public class SanityTest
{
    [Fact]
    public void TestProjectRuns()
    {
        Assert.True(true);
    }
}
```

- [ ] **Step 3: Run the test to verify the harness works**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`
Expected: PASS — `Passed!  - Failed: 0, Passed: 1`.

If it fails to restore/build, confirm the package versions resolve against the
installed .NET 10 SDK and adjust to the latest available patch versions.

- [ ] **Step 4: Commit**

```bash
git add NtfyDesktop.Tests/
git commit -m "test: bootstrap xUnit test project"
```

---

## Task 2: Matcher model and matching logic

**Files:**
- Create: `Features/Rules/Model/Matcher.cs`
- Test: `NtfyDesktop.Tests/Rules/MatcherTests.cs`

**Interfaces:**
- Produces: `NtfyDesktop.Features.Rules.Model.Matcher` — a record with init properties `string? Topic`, `Priority? MinPriority`, `string? TitleRegex`, `string? BodyRegex`, `string? Tag`, and method `bool Matches(NtfyMessage message)`. An all-null matcher matches everything. Multiple set fields are ANDed. Regex is case-insensitive and compiled once (cached).

- [ ] **Step 1: Write the failing tests**

Create `NtfyDesktop.Tests/Rules/MatcherTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class MatcherTests
{
    private static NtfyMessage Msg(string topic = "backups", string? title = null,
        string? body = null, Priority priority = Priority.Default, params string[] tags) =>
        new()
        {
            Id = "m1",
            Topic = topic,
            Title = title,
            Message = body,
            Priority = priority,
            Tags = tags.Length > 0 ? tags.ToList() : null,
        };

    [Fact]
    public void EmptyMatcher_MatchesAnything()
    {
        Assert.True(new Matcher().Matches(Msg()));
    }

    [Fact]
    public void Topic_MatchesExactly_CaseInsensitive()
    {
        Assert.True(new Matcher { Topic = "Backups" }.Matches(Msg(topic: "backups")));
        Assert.False(new Matcher { Topic = "alerts" }.Matches(Msg(topic: "backups")));
    }

    [Fact]
    public void TitleRegex_Matches_Substring()
    {
        Assert.True(new Matcher { TitleRegex = "succeeded" }.Matches(Msg(title: "Backup succeeded")));
        Assert.False(new Matcher { TitleRegex = "^PROBLEM" }.Matches(Msg(title: "Backup succeeded")));
    }

    [Fact]
    public void BodyRegex_Matches_NullBody_IsFalse()
    {
        Assert.False(new Matcher { BodyRegex = "x" }.Matches(Msg(body: null)));
    }

    [Fact]
    public void MinPriority_MatchesAtOrAbove()
    {
        Assert.True(new Matcher { MinPriority = Priority.High }.Matches(Msg(priority: Priority.Urgent)));
        Assert.True(new Matcher { MinPriority = Priority.High }.Matches(Msg(priority: Priority.High)));
        Assert.False(new Matcher { MinPriority = Priority.High }.Matches(Msg(priority: Priority.Default)));
    }

    [Fact]
    public void Tag_MatchesWhenPresent_CaseInsensitive()
    {
        Assert.True(new Matcher { Tag = "Warning" }.Matches(Msg(tags: ["warning", "disk"])));
        Assert.False(new Matcher { Tag = "warning" }.Matches(Msg(tags: ["disk"])));
    }

    [Fact]
    public void MultipleConditions_AreAnded()
    {
        var m = new Matcher { Topic = "backups", TitleRegex = "succeeded" };
        Assert.True(m.Matches(Msg(topic: "backups", title: "Backup succeeded")));
        Assert.False(m.Matches(Msg(topic: "backups", title: "Backup FAILED")));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter MatcherTests`
Expected: FAIL — `Matcher` does not exist.

- [ ] **Step 3: Implement Matcher**

Create `Features/Rules/Model/Matcher.cs`:

```csharp
using System.Text.RegularExpressions;
using NtfyDesktop.Domain;

namespace NtfyDesktop.Features.Rules.Model;

/// <summary>
/// A predicate over a received message. All set fields are ANDed; an all-null
/// matcher matches every message. Regex fields are case-insensitive substring
/// searches (use ^/$ to anchor). Compiled regexes are cached per instance.
/// </summary>
public sealed record Matcher
{
    public string? Topic { get; init; }
    public Priority? MinPriority { get; init; }
    public string? TitleRegex { get; init; }
    public string? BodyRegex { get; init; }
    public string? Tag { get; init; }

    private Regex? _titleRe;
    private Regex? _bodyRe;

    public bool Matches(NtfyMessage message)
    {
        if (Topic is not null &&
            !string.Equals(Topic, message.Topic, StringComparison.OrdinalIgnoreCase))
            return false;

        if (MinPriority is { } min && message.Priority < min)
            return false;

        if (TitleRegex is not null)
        {
            _titleRe ??= Compile(TitleRegex);
            if (message.Title is null || !_titleRe.IsMatch(message.Title)) return false;
        }

        if (BodyRegex is not null)
        {
            _bodyRe ??= Compile(BodyRegex);
            if (message.Message is null || !_bodyRe.IsMatch(message.Message)) return false;
        }

        if (Tag is not null &&
            (message.Tags is null ||
             !message.Tags.Any(t => string.Equals(t, Tag, StringComparison.OrdinalIgnoreCase))))
            return false;

        return true;
    }

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter MatcherTests`
Expected: PASS — all 7 tests green.

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/Model/Matcher.cs NtfyDesktop.Tests/Rules/MatcherTests.cs
git commit -m "feat(rules): add Matcher predicate"
```

---

## Task 3: KeySelector (correlation key extraction)

**Files:**
- Create: `Features/Rules/Model/KeySelector.cs`
- Test: `NtfyDesktop.Tests/Rules/KeySelectorTests.cs`

**Interfaces:**
- Produces: `KeyField` enum (`Title`, `Body`) and `KeySelector` record with `KeyField From`, `string Regex`, and `string? Extract(NtfyMessage message)`. Returns the named group `key` if present, else group 1, else null. Returns null when the source field is null or no match.

- [ ] **Step 1: Write the failing tests**

Create `NtfyDesktop.Tests/Rules/KeySelectorTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class KeySelectorTests
{
    private static NtfyMessage Msg(string? title = null, string? body = null) =>
        new() { Id = "m1", Topic = "t", Title = title, Message = body };

    [Fact]
    public void Extract_UsesNamedGroup_FromBody()
    {
        var sel = new KeySelector { From = KeyField.Body, Regex = @"Event ID: (?<key>\d+)" };
        Assert.Equal("4242", sel.Extract(Msg(body: "Disk full. Event ID: 4242 on host db1")));
    }

    [Fact]
    public void Extract_FallsBackToGroupOne_WhenNoNamedGroup()
    {
        var sel = new KeySelector { From = KeyField.Title, Regex = @"#(\d+)" };
        Assert.Equal("7", sel.Extract(Msg(title: "PROBLEM #7")));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenNoMatch()
    {
        var sel = new KeySelector { From = KeyField.Body, Regex = @"Event ID: (?<key>\d+)" };
        Assert.Null(sel.Extract(Msg(body: "nothing here")));
    }

    [Fact]
    public void Extract_ReturnsNull_WhenSourceFieldNull()
    {
        var sel = new KeySelector { From = KeyField.Body, Regex = @"(?<key>\d+)" };
        Assert.Null(sel.Extract(Msg(body: null)));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter KeySelectorTests`
Expected: FAIL — `KeySelector` does not exist.

- [ ] **Step 3: Implement KeySelector**

Create `Features/Rules/Model/KeySelector.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter KeySelectorTests`
Expected: PASS — all 4 tests green.

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/Model/KeySelector.cs NtfyDesktop.Tests/Rules/KeySelectorTests.cs
git commit -m "feat(rules): add KeySelector for correlation keys"
```

---

## Task 4: Rule, action, pack, and verdict models

Pure data records used by the engine and parser. No tests of their own (they're
plain records exercised by Task 5/6/7 tests); this task just defines the types.

**Files:**
- Create: `Features/Rules/Model/RuleAction.cs`
- Create: `Features/Rules/Model/Rules.cs`
- Create: `Features/Rules/Model/RuleVerdict.cs`

**Interfaces:**
- Produces:
  - `RuleActionKind` enum: `SuppressToast`, `Tag`.
  - `RuleAction(RuleActionKind Kind, string? Value)` — `Value` carries the tag text for `Tag`.
  - `MatchRule(Matcher When, IReadOnlyList<RuleAction> Actions)`.
  - `CorrelateRule(string Id, Matcher Open, Matcher Close, KeySelector Key, IReadOnlyList<RuleAction> OnClose)`.
  - `RulePack(string Name, IReadOnlyList<MatchRule> MatchRules, IReadOnlyList<CorrelateRule> CorrelateRules)`.
  - `IncidentOpen(string RuleId, string Key, string MessageId, long OpenedAt)`.
  - `RuleVerdict` with `bool Suppress`, `IReadOnlyList<string> Tags`, `IncidentOpen? OpenIncident`, `(string RuleId, string Key)? CloseIncident`, plus a static `RuleVerdict.PassThrough` (no-op).

- [ ] **Step 1: Create RuleAction.cs**

```csharp
namespace NtfyDesktop.Features.Rules.Model;

public enum RuleActionKind
{
    SuppressToast,
    Tag,
}

/// <summary>An action a matched rule applies. <see cref="Value"/> holds the tag
/// text for <see cref="RuleActionKind.Tag"/>; null otherwise.</summary>
public sealed record RuleAction(RuleActionKind Kind, string? Value = null);
```

- [ ] **Step 2: Create Rules.cs**

```csharp
namespace NtfyDesktop.Features.Rules.Model;

/// <summary>A straight pattern → actions rule.</summary>
public sealed record MatchRule(Matcher When, IReadOnlyList<RuleAction> Actions);

/// <summary>
/// Pairs an opening message with its resolving message via an extracted key.
/// A close message's actions only fire when a matching open incident exists.
/// <see cref="Id"/> namespaces incidents in the store (pack name + index).
/// </summary>
public sealed record CorrelateRule(
    string Id,
    Matcher Open,
    Matcher Close,
    KeySelector Key,
    IReadOnlyList<RuleAction> OnClose);

public sealed record RulePack(
    string Name,
    IReadOnlyList<MatchRule> MatchRules,
    IReadOnlyList<CorrelateRule> CorrelateRules);
```

- [ ] **Step 3: Create RuleVerdict.cs**

```csharp
namespace NtfyDesktop.Features.Rules.Model;

/// <summary>An incident to record as open (pending side-effect after the row is
/// confirmed new).</summary>
public sealed record IncidentOpen(string RuleId, string Key, string MessageId, long OpenedAt);

/// <summary>
/// The engine's decision for one message. <see cref="Suppress"/> drops the toast,
/// hides the row from the feed by default, and excludes it from the unread count.
/// <see cref="OpenIncident"/> / <see cref="CloseIncident"/> are incident-store
/// writes the caller applies only once the message is confirmed new.
/// </summary>
public sealed record RuleVerdict
{
    public bool Suppress { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IncidentOpen? OpenIncident { get; init; }
    public (string RuleId, string Key)? CloseIncident { get; init; }

    public static readonly RuleVerdict PassThrough = new();
}
```

- [ ] **Step 4: Verify it builds**

Run: `dotnet build NtfyDesktop.csproj`
Expected: build succeeds (these are unreferenced records — they just need to compile).

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/Model/RuleAction.cs Features/Rules/Model/Rules.cs Features/Rules/Model/RuleVerdict.cs
git commit -m "feat(rules): add rule, action, pack and verdict models"
```

---

## Task 5: IncidentStore interface + RuleEngine match-rule evaluation

This task builds the engine's match-rule half plus the incident-store seam (with a
fake for tests). Correlation comes in Task 6.

**Files:**
- Create: `Features/Rules/IIncidentStore.cs`
- Create: `Features/Rules/RuleEngine.cs`
- Test: `NtfyDesktop.Tests/Rules/RuleEngineMatchTests.cs`
- Test helper: `NtfyDesktop.Tests/Rules/FakeIncidentStore.cs`

**Interfaces:**
- Consumes: `Matcher`, `MatchRule`, `CorrelateRule`, `RulePack`, `RuleVerdict`, `RuleAction` (Task 2/4); `AppSettings` (existing).
- Produces:
  - `Incident(string RuleId, string Key, string OpenMessageId, long OpenedAt)` record.
  - `IIncidentStore` with `Incident? FindOpen(string ruleId, string key)`, `void Open(string ruleId, string key, string messageId, long openedAt)`, `void Resolve(string ruleId, string key)`.
  - `RuleEngine(AppSettings settings, PackStore packs, IIncidentStore incidents)` — **but `PackStore` doesn't exist until Task 8**, so this task introduces the engine taking `IReadOnlyList<RulePack>` via a `Func<IReadOnlyList<RulePack>>` provider instead. Signature: `RuleEngine(AppSettings settings, Func<IReadOnlyList<RulePack>> packsProvider, IIncidentStore incidents)`. Method `RuleVerdict Evaluate(NtfyMessage message)`. When `settings.RulesEnabled` is false, returns `RuleVerdict.PassThrough`.
- Note: `AppSettings.RulesEnabled` is added in Task 9; for this task's tests, construct `AppSettings` and set the property — so **add the property as part of this task** (Step 3a) to keep the engine testable. Task 9 only wires DI, not the property.

- [ ] **Step 1: Write the failing tests + fake**

Create `NtfyDesktop.Tests/Rules/FakeIncidentStore.cs`:

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

/// <summary>In-memory IIncidentStore for engine tests.</summary>
public sealed class FakeIncidentStore : IIncidentStore
{
    private readonly Dictionary<(string, string), Incident> _open = new();

    public Incident? FindOpen(string ruleId, string key) =>
        _open.GetValueOrDefault((ruleId, key));

    public void Open(string ruleId, string key, string messageId, long openedAt) =>
        _open[(ruleId, key)] = new Incident(ruleId, key, messageId, openedAt);

    public void Resolve(string ruleId, string key) => _open.Remove((ruleId, key));

    public bool HasOpen(string ruleId, string key) => _open.ContainsKey((ruleId, key));
}
```

Create `NtfyDesktop.Tests/Rules/RuleEngineMatchTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Tests.Rules;

public class RuleEngineMatchTests
{
    private static NtfyMessage Msg(string topic = "backups", string? title = null) =>
        new() { Id = "m1", Topic = topic, Title = title };

    private static RuleEngine Engine(IReadOnlyList<RulePack> packs, bool enabled = true)
    {
        var settings = new AppSettings { RulesEnabled = enabled };
        return new RuleEngine(settings, () => packs, new FakeIncidentStore());
    }

    private static RulePack Pack(params MatchRule[] match) =>
        new("test", match, []);

    [Fact]
    public void NoRules_PassesThrough()
    {
        var v = Engine([]).Evaluate(Msg());
        Assert.False(v.Suppress);
        Assert.Empty(v.Tags);
    }

    [Fact]
    public void MatchingSuppressRule_SetsSuppress()
    {
        var rule = new MatchRule(
            new Matcher { Topic = "backups", TitleRegex = "succeeded" },
            [new RuleAction(RuleActionKind.SuppressToast)]);

        var v = Engine([Pack(rule)]).Evaluate(Msg(title: "Backup succeeded"));
        Assert.True(v.Suppress);
    }

    [Fact]
    public void NonMatchingRule_DoesNotSuppress()
    {
        var rule = new MatchRule(
            new Matcher { TitleRegex = "succeeded" },
            [new RuleAction(RuleActionKind.SuppressToast)]);

        var v = Engine([Pack(rule)]).Evaluate(Msg(title: "Backup FAILED"));
        Assert.False(v.Suppress);
    }

    [Fact]
    public void TagAction_CollectsTagValue()
    {
        var rule = new MatchRule(
            new Matcher { Topic = "backups" },
            [new RuleAction(RuleActionKind.Tag, "noise")]);

        var v = Engine([Pack(rule)]).Evaluate(Msg());
        Assert.Contains("noise", v.Tags);
    }

    [Fact]
    public void Disabled_PassesThroughEvenWithMatchingRule()
    {
        var rule = new MatchRule(
            new Matcher { Topic = "backups" },
            [new RuleAction(RuleActionKind.SuppressToast)]);

        var v = Engine([Pack(rule)], enabled: false).Evaluate(Msg());
        Assert.False(v.Suppress);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter RuleEngineMatchTests`
Expected: FAIL — `IIncidentStore`, `RuleEngine`, and `AppSettings.RulesEnabled` don't exist.

- [ ] **Step 3a: Add the master toggle to AppSettings**

In `Features/Settings/AppSettings.cs`, add an auto-property alongside the other
top-level settings (e.g. near `IsPaused`). Match the file's existing property style:

```csharp
/// <summary>Master switch for the notification rule engine. When false, the engine
/// passes every message through unchanged.</summary>
public bool RulesEnabled { get; set; } = true;
```

- [ ] **Step 3b: Create IIncidentStore.cs**

```csharp
namespace NtfyDesktop.Features.Rules;

/// <summary>An open (unresolved) correlated incident.</summary>
public sealed record Incident(string RuleId, string Key, string OpenMessageId, long OpenedAt);

/// <summary>
/// Tracks open correlated incidents so a "resolved" message can be paired with the
/// "problem" that opened it. Keyed by (rule id, extracted key).
/// </summary>
public interface IIncidentStore
{
    Incident? FindOpen(string ruleId, string key);
    void Open(string ruleId, string key, string messageId, long openedAt);
    void Resolve(string ruleId, string key);
}
```

- [ ] **Step 3c: Create RuleEngine.cs (match half only)**

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Evaluates a received message against the loaded rule packs and returns a verdict.
/// Pure with respect to match rules; correlation reads the incident store. Incident
/// *writes* are deferred to <see cref="ApplyIncidentSideEffects"/> so the caller can
/// apply them only once the message is confirmed new (a since= catch-up re-delivers
/// the boundary message, which must not re-open/re-resolve an incident).
///
/// Fails open: a rule that throws is skipped, never silently dropping a message.
/// </summary>
public sealed class RuleEngine(
    AppSettings settings,
    Func<IReadOnlyList<RulePack>> packsProvider,
    IIncidentStore incidents)
{
    public RuleVerdict Evaluate(NtfyMessage message)
    {
        if (!settings.RulesEnabled) return RuleVerdict.PassThrough;

        var suppress = false;
        var tags = new List<string>();

        foreach (var pack in packsProvider())
        {
            foreach (var rule in pack.MatchRules)
            {
                try
                {
                    if (!rule.When.Matches(message)) continue;
                    ApplyActions(rule.Actions, ref suppress, tags);
                }
                catch
                {
                    // Fail open: a malformed regex / rule never drops a message.
                }
            }
        }

        return new RuleVerdict { Suppress = suppress, Tags = tags };
    }

    private static void ApplyActions(IReadOnlyList<RuleAction> actions, ref bool suppress, List<string> tags)
    {
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case RuleActionKind.SuppressToast:
                    suppress = true;
                    break;
                case RuleActionKind.Tag when !string.IsNullOrEmpty(action.Value):
                    if (!tags.Contains(action.Value)) tags.Add(action.Value);
                    break;
            }
        }
    }

    /// <summary>Applies the verdict's pending incident-store writes. Call only after
    /// the message is confirmed new.</summary>
    public void ApplyIncidentSideEffects(RuleVerdict verdict)
    {
        if (verdict.OpenIncident is { } open)
            incidents.Open(open.RuleId, open.Key, open.MessageId, open.OpenedAt);
        if (verdict.CloseIncident is { } close)
            incidents.Resolve(close.RuleId, close.Key);
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter RuleEngineMatchTests`
Expected: PASS — all 5 tests green.

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/IIncidentStore.cs Features/Rules/RuleEngine.cs Features/Settings/AppSettings.cs NtfyDesktop.Tests/Rules/
git commit -m "feat(rules): add RuleEngine match evaluation + incident store seam"
```

---

## Task 6: RuleEngine correlate evaluation

Adds problem/resolved pairing to the engine. A close message is suppressed only when
a matching open incident exists in the store.

**Files:**
- Modify: `Features/Rules/RuleEngine.cs`
- Test: `NtfyDesktop.Tests/Rules/RuleEngineCorrelateTests.cs`

**Interfaces:**
- Consumes: `CorrelateRule`, `KeySelector`, `IncidentOpen`, `FakeIncidentStore` (Task 5).
- Produces: `RuleEngine.Evaluate` now also returns `OpenIncident` (for an open-match with an extractable key) and `Suppress` + `CloseIncident` (for a close-match whose key has an open incident). `ApplyIncidentSideEffects` (Task 5) persists them.

- [ ] **Step 1: Write the failing tests**

Create `NtfyDesktop.Tests/Rules/RuleEngineCorrelateTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Tests.Rules;

public class RuleEngineCorrelateTests
{
    private static NtfyMessage Msg(string id, string title, string body) =>
        new() { Id = id, Topic = "zabbix", Title = title, Message = body, Time = 1000 };

    private static CorrelateRule ZabbixRule() => new(
        Id: "zabbix#0",
        Open: new Matcher { TitleRegex = "^PROBLEM" },
        Close: new Matcher { TitleRegex = "^RESOLVED" },
        Key: new KeySelector { From = KeyField.Body, Regex = @"Event ID: (?<key>\d+)" },
        OnClose: [new RuleAction(RuleActionKind.SuppressToast)]);

    private static (RuleEngine engine, FakeIncidentStore store) Engine(CorrelateRule rule)
    {
        var store = new FakeIncidentStore();
        var pack = new RulePack("zabbix", [], [rule]);
        var engine = new RuleEngine(new AppSettings { RulesEnabled = true }, () => [pack], store);
        return (engine, store);
    }

    [Fact]
    public void OpenMessage_ProducesOpenIncident_NotSuppressed()
    {
        var (engine, _) = Engine(ZabbixRule());
        var v = engine.Evaluate(Msg("p1", "PROBLEM: disk", "Event ID: 42"));

        Assert.False(v.Suppress);
        Assert.NotNull(v.OpenIncident);
        Assert.Equal("zabbix#0", v.OpenIncident!.RuleId);
        Assert.Equal("42", v.OpenIncident.Key);
        Assert.Equal("p1", v.OpenIncident.MessageId);
    }

    [Fact]
    public void CloseMessage_WithOpenIncident_IsSuppressed_AndResolves()
    {
        var (engine, store) = Engine(ZabbixRule());

        // Open it first and apply the side effect (simulating ConnectionManager).
        var open = engine.Evaluate(Msg("p1", "PROBLEM: disk", "Event ID: 42"));
        engine.ApplyIncidentSideEffects(open);
        Assert.True(store.HasOpen("zabbix#0", "42"));

        var close = engine.Evaluate(Msg("r1", "RESOLVED: disk", "Event ID: 42"));
        Assert.True(close.Suppress);
        Assert.Equal(("zabbix#0", "42"), close.CloseIncident);

        engine.ApplyIncidentSideEffects(close);
        Assert.False(store.HasOpen("zabbix#0", "42"));
    }

    [Fact]
    public void CloseMessage_WithoutOpenIncident_IsNotSuppressed()
    {
        var (engine, _) = Engine(ZabbixRule());
        // A stray RESOLVED with no preceding PROBLEM — surface it.
        var v = engine.Evaluate(Msg("r1", "RESOLVED: disk", "Event ID: 99"));
        Assert.False(v.Suppress);
        Assert.Null(v.CloseIncident);
    }

    [Fact]
    public void OpenMessage_WithoutExtractableKey_ProducesNoIncident()
    {
        var (engine, _) = Engine(ZabbixRule());
        var v = engine.Evaluate(Msg("p1", "PROBLEM: disk", "no event id here"));
        Assert.Null(v.OpenIncident);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter RuleEngineCorrelateTests`
Expected: FAIL — `Evaluate` doesn't yet handle correlate rules (OpenIncident/CloseIncident always null).

- [ ] **Step 3: Add correlate handling to RuleEngine.Evaluate**

In `Features/Rules/RuleEngine.cs`, replace the body of `Evaluate` so it also walks
correlate rules. The full method becomes:

```csharp
    public RuleVerdict Evaluate(NtfyMessage message)
    {
        if (!settings.RulesEnabled) return RuleVerdict.PassThrough;

        var suppress = false;
        var tags = new List<string>();
        IncidentOpen? openIncident = null;
        (string RuleId, string Key)? closeIncident = null;

        foreach (var pack in packsProvider())
        {
            foreach (var rule in pack.MatchRules)
            {
                try
                {
                    if (!rule.When.Matches(message)) continue;
                    ApplyActions(rule.Actions, ref suppress, tags);
                }
                catch { /* fail open */ }
            }

            foreach (var rule in pack.CorrelateRules)
            {
                try
                {
                    if (rule.Open.Matches(message))
                    {
                        var key = rule.Key.Extract(message);
                        if (key is not null)
                            openIncident = new IncidentOpen(rule.Id, key, message.Id, message.Time);
                    }
                    else if (rule.Close.Matches(message))
                    {
                        var key = rule.Key.Extract(message);
                        if (key is not null && incidents.FindOpen(rule.Id, key) is not null)
                        {
                            ApplyActions(rule.OnClose, ref suppress, tags);
                            closeIncident = (rule.Id, key);
                        }
                    }
                }
                catch { /* fail open */ }
            }
        }

        return new RuleVerdict
        {
            Suppress = suppress,
            Tags = tags,
            OpenIncident = openIncident,
            CloseIncident = closeIncident,
        };
    }
```

(Leave `ApplyActions` and `ApplyIncidentSideEffects` from Task 5 unchanged.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter RuleEngineCorrelateTests`
Expected: PASS — all 4 tests green. Also run the full suite to confirm no regression:
`dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj` → all green.

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/RuleEngine.cs NtfyDesktop.Tests/Rules/RuleEngineCorrelateTests.cs
git commit -m "feat(rules): correlate problem/resolved pairs in RuleEngine"
```

---

## Task 7: IncidentStore SQLite implementation

The real, encrypted-at-rest store behind `IIncidentStore`. Path + password are
constructor parameters so tests use a temp file.

**Files:**
- Create: `Features/Rules/IncidentStore.cs`
- Test: `NtfyDesktop.Tests/Rules/IncidentStoreTests.cs`

**Interfaces:**
- Consumes: `IIncidentStore`, `Incident` (Task 5).
- Produces: `IncidentStore(string dbPath, string password) : IIncidentStore`. Creates the `incidents` table on construction. `Open` upserts (re-opening an existing key refreshes its message id + time); `Resolve` deletes the row; `FindOpen` returns the row or null.

- [ ] **Step 1: Write the failing tests**

Create `NtfyDesktop.Tests/Rules/IncidentStoreTests.cs`:

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class IncidentStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public IncidentStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfytests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "rules.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private IncidentStore NewStore() => new(_dbPath, "test-key-1234");

    [Fact]
    public void FindOpen_ReturnsNull_WhenEmpty()
    {
        Assert.Null(NewStore().FindOpen("r", "k"));
    }

    [Fact]
    public void Open_ThenFindOpen_ReturnsIncident()
    {
        var store = NewStore();
        store.Open("zabbix#0", "42", "p1", 1000);

        var found = store.FindOpen("zabbix#0", "42");
        Assert.NotNull(found);
        Assert.Equal("p1", found!.OpenMessageId);
        Assert.Equal(1000, found.OpenedAt);
    }

    [Fact]
    public void Resolve_RemovesIncident()
    {
        var store = NewStore();
        store.Open("zabbix#0", "42", "p1", 1000);
        store.Resolve("zabbix#0", "42");
        Assert.Null(store.FindOpen("zabbix#0", "42"));
    }

    [Fact]
    public void Open_IsScopedByRuleId()
    {
        var store = NewStore();
        store.Open("ruleA", "42", "p1", 1000);
        Assert.Null(store.FindOpen("ruleB", "42"));
    }

    [Fact]
    public void State_PersistsAcrossInstances()
    {
        NewStore().Open("zabbix#0", "42", "p1", 1000);
        // A second instance reopening the same encrypted file must see the row.
        Assert.NotNull(NewStore().FindOpen("zabbix#0", "42"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter IncidentStoreTests`
Expected: FAIL — `IncidentStore` does not exist.

- [ ] **Step 3: Implement IncidentStore**

Create `Features/Rules/IncidentStore.cs` (mirrors `HistoryRepository`'s `PRAGMA key`
encryption approach):

```csharp
using System.IO;
using Microsoft.Data.Sqlite;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// SQLite-backed incident store, encrypted at rest with SQLite3MC (same cipher and
/// key approach as the history DB). One row per open incident, keyed by
/// (rule_id, key). Path + password are injected so it's test-friendly.
/// </summary>
public sealed class IncidentStore : IIncidentStore
{
    private readonly string _dbPath;
    private readonly string _password;

    public IncidentStore(string dbPath, string password)
    {
        _dbPath = dbPath;
        _password = password;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        Initialize();
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS incidents (
                rule_id         TEXT    NOT NULL,
                key             TEXT    NOT NULL,
                open_message_id TEXT    NOT NULL,
                opened_at       INTEGER NOT NULL,
                PRIMARY KEY (rule_id, key)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public Incident? FindOpen(string ruleId, string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT open_message_id, opened_at FROM incidents
             WHERE rule_id = @rid AND key = @key LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@rid", ruleId);
        cmd.Parameters.AddWithValue("@key", key);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new Incident(ruleId, key, reader.GetString(0), reader.GetInt64(1));
    }

    public void Open(string ruleId, string key, string messageId, long openedAt)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO incidents (rule_id, key, open_message_id, opened_at)
            VALUES (@rid, @key, @mid, @at)
            ON CONFLICT(rule_id, key) DO UPDATE SET
                open_message_id = excluded.open_message_id,
                opened_at       = excluded.opened_at
            """;
        cmd.Parameters.AddWithValue("@rid", ruleId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@mid", messageId);
        cmd.Parameters.AddWithValue("@at", openedAt);
        cmd.ExecuteNonQuery();
    }

    public void Resolve(string ruleId, string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM incidents WHERE rule_id = @rid AND key = @key";
        cmd.Parameters.AddWithValue("@rid", ruleId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
        }.ToString());
        conn.Open();
        // PRAGMA key must be the first statement on the connection (see HistoryRepository).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA key = '" + _password.Replace("'", "''") + "'";
        cmd.ExecuteNonQuery();
        return conn;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter IncidentStoreTests`
Expected: PASS — all 5 tests green.

- [ ] **Step 5: Commit**

```bash
git add Features/Rules/IncidentStore.cs NtfyDesktop.Tests/Rules/IncidentStoreTests.cs
git commit -m "feat(rules): add encrypted SQLite IncidentStore"
```

---

## Task 8: PackParser and PackStore

Turns pack JSON files into `RulePack` models. Parsing is pure (testable from a
string); `PackStore` loads a directory of `*.json` files.

**Files:**
- Create: `Features/Rules/PackParser.cs`
- Create: `Features/Rules/PackStore.cs`
- Test: `NtfyDesktop.Tests/Rules/PackParserTests.cs`
- Test: `NtfyDesktop.Tests/Rules/PackStoreTests.cs`

**Interfaces:**
- Consumes: all model types (Tasks 2–4).
- Produces:
  - `PackParser.Parse(string json)` → `RulePack` (throws on JSON that isn't an object/array; tolerates unknown action strings and unknown rule `type`s by skipping them — fail open).
  - `PackStore(string directory)` with `IReadOnlyList<RulePack> Packs { get; }` and `void Reload()`. Missing directory → empty list. A file that fails to parse is skipped (logged via `System.Diagnostics.Debug.WriteLine`), not fatal.

**Pack JSON format (the contract):**
```jsonc
{
  "name": "Zabbix",
  "rules": [
    { "type": "match",
      "when": { "topic": "backups", "titleRegex": "succeeded" },
      "do": ["suppressToast", "tag:noise"] },
    { "type": "correlate",
      "open":  { "titleRegex": "^PROBLEM" },
      "close": { "titleRegex": "^RESOLVED" },
      "key":   { "from": "body", "regex": "Event ID: (?<key>\\d+)" },
      "onClose": ["suppressToast"] }
  ]
}
```
Matcher fields: `topic`, `minPriority` (label: `min|low|default|high|urgent`),
`titleRegex`, `bodyRegex`, `tag`. Action strings: `suppressToast`, `tag:<value>`
(any other string is ignored). `correlate` rule id is `"<packName>#<index>"`.

- [ ] **Step 1: Write the failing parser tests**

Create `NtfyDesktop.Tests/Rules/PackParserTests.cs`:

```csharp
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Tests.Rules;

public class PackParserTests
{
    [Fact]
    public void Parse_MatchRule_WithActions()
    {
        const string json = """
            { "name": "Backups", "rules": [
              { "type": "match",
                "when": { "topic": "backups", "titleRegex": "succeeded" },
                "do": ["suppressToast", "tag:noise"] } ] }
            """;

        var pack = PackParser.Parse(json);
        Assert.Equal("Backups", pack.Name);
        var rule = Assert.Single(pack.MatchRules);
        Assert.Equal("backups", rule.When.Topic);
        Assert.Contains(rule.Actions, a => a.Kind == RuleActionKind.SuppressToast);
        Assert.Contains(rule.Actions, a => a.Kind == RuleActionKind.Tag && a.Value == "noise");
    }

    [Fact]
    public void Parse_MinPriority_Label()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "match", "when": { "minPriority": "high" }, "do": ["suppressToast"] } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).MatchRules);
        Assert.Equal(Priority.High, rule.When.MinPriority);
    }

    [Fact]
    public void Parse_CorrelateRule_WithGeneratedId()
    {
        const string json = """
            { "name": "Zabbix", "rules": [
              { "type": "correlate",
                "open":  { "titleRegex": "^PROBLEM" },
                "close": { "titleRegex": "^RESOLVED" },
                "key":   { "from": "body", "regex": "Event ID: (?<key>\\d+)" },
                "onClose": ["suppressToast"] } ] }
            """;

        var pack = PackParser.Parse(json);
        var rule = Assert.Single(pack.CorrelateRules);
        Assert.Equal("Zabbix#0", rule.Id);
        Assert.Equal(KeyField.Body, rule.Key.From);
        Assert.Equal("^RESOLVED", rule.Close.TitleRegex);
        Assert.Contains(rule.OnClose, a => a.Kind == RuleActionKind.SuppressToast);
    }

    [Fact]
    public void Parse_UnknownActionString_IsIgnored()
    {
        const string json = """
            { "name": "p", "rules": [
              { "type": "match", "when": { "topic": "x" }, "do": ["digest", "dismissOriginal"] } ] }
            """;
        var rule = Assert.Single(PackParser.Parse(json).MatchRules);
        Assert.Empty(rule.Actions); // both unknown in phase 1a → skipped, no crash
    }

    [Fact]
    public void Parse_UnknownRuleType_IsSkipped()
    {
        const string json = """
            { "name": "p", "rules": [ { "type": "expect", "when": { "topic": "x" } } ] }
            """;
        var pack = PackParser.Parse(json);
        Assert.Empty(pack.MatchRules);
        Assert.Empty(pack.CorrelateRules);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackParserTests`
Expected: FAIL — `PackParser` does not exist.

- [ ] **Step 3: Implement PackParser**

Create `Features/Rules/PackParser.cs`:

```csharp
using System.Text.Json;
using NtfyDesktop.Domain;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Parses a rule pack from JSON into the model. Unknown action strings and unknown
/// rule types are skipped (fail open) so a pack authored for a later phase doesn't
/// break loading. Throws only on JSON that isn't a valid pack object.
/// </summary>
public static class PackParser
{
    public static RulePack Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "pack" : "pack";

        var matchRules = new List<MatchRule>();
        var correlateRules = new List<CorrelateRule>();

        if (root.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var rule in rules.EnumerateArray())
            {
                var type = rule.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (type)
                {
                    case "match":
                        matchRules.Add(new MatchRule(
                            ParseMatcher(rule, "when"),
                            ParseActions(rule, "do")));
                        break;
                    case "correlate":
                        correlateRules.Add(new CorrelateRule(
                            Id: $"{name}#{index}",
                            Open: ParseMatcher(rule, "open"),
                            Close: ParseMatcher(rule, "close"),
                            Key: ParseKey(rule),
                            OnClose: ParseActions(rule, "onClose")));
                        break;
                    // unknown type (e.g. "expect", phase 1b) → skip
                }
                index++;
            }
        }

        return new RulePack(name, matchRules, correlateRules);
    }

    private static Matcher ParseMatcher(JsonElement rule, string property)
    {
        if (!rule.TryGetProperty(property, out var m) || m.ValueKind != JsonValueKind.Object)
            return new Matcher();

        return new Matcher
        {
            Topic = Str(m, "topic"),
            MinPriority = ParsePriority(Str(m, "minPriority")),
            TitleRegex = Str(m, "titleRegex"),
            BodyRegex = Str(m, "bodyRegex"),
            Tag = Str(m, "tag"),
        };
    }

    private static KeySelector ParseKey(JsonElement rule)
    {
        if (!rule.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.Object)
            return new KeySelector();

        var from = string.Equals(Str(k, "from"), "title", StringComparison.OrdinalIgnoreCase)
            ? KeyField.Title : KeyField.Body;
        return new KeySelector { From = from, Regex = Str(k, "regex") ?? string.Empty };
    }

    private static IReadOnlyList<RuleAction> ParseActions(JsonElement rule, string property)
    {
        var actions = new List<RuleAction>();
        if (!rule.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return actions;

        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrEmpty(s)) continue;

            if (string.Equals(s, "suppressToast", StringComparison.OrdinalIgnoreCase))
                actions.Add(new RuleAction(RuleActionKind.SuppressToast));
            else if (s.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                actions.Add(new RuleAction(RuleActionKind.Tag, s["tag:".Length..]));
            // unknown action (digest, dismissOriginal, …) → skipped (phase 1a)
        }
        return actions;
    }

    private static string? Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Priority? ParsePriority(string? label) => label?.ToLowerInvariant() switch
    {
        "min" => Priority.Min,
        "low" => Priority.Low,
        "default" => Priority.Default,
        "high" => Priority.High,
        "urgent" or "max" => Priority.Urgent,
        _ => null,
    };
}
```

- [ ] **Step 4: Run parser tests to verify they pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackParserTests`
Expected: PASS — all 5 tests green.

- [ ] **Step 5: Write the failing PackStore tests**

Create `NtfyDesktop.Tests/Rules/PackStoreTests.cs`:

```csharp
using NtfyDesktop.Features.Rules;

namespace NtfyDesktop.Tests.Rules;

public class PackStoreTests : IDisposable
{
    private readonly string _dir;

    public PackStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ntfypacks_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void MissingDirectory_YieldsEmpty()
    {
        Assert.Empty(new PackStore(_dir).Packs);
    }

    [Fact]
    public void LoadsValidPacks_AndSkipsInvalidFiles()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "good.json"),
            """{ "name": "Good", "rules": [ { "type": "match", "when": { "topic": "x" }, "do": ["suppressToast"] } ] }""");
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ this is not json");

        var store = new PackStore(_dir);
        var pack = Assert.Single(store.Packs);
        Assert.Equal("Good", pack.Name);
    }

    [Fact]
    public void Reload_PicksUpNewFiles()
    {
        Directory.CreateDirectory(_dir);
        var store = new PackStore(_dir);
        Assert.Empty(store.Packs);

        File.WriteAllText(Path.Combine(_dir, "new.json"),
            """{ "name": "New", "rules": [] }""");
        store.Reload();
        Assert.Single(store.Packs);
    }
}
```

- [ ] **Step 6: Run to verify it fails**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackStoreTests`
Expected: FAIL — `PackStore` does not exist.

- [ ] **Step 7: Implement PackStore**

Create `Features/Rules/PackStore.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using NtfyDesktop.Features.Rules.Model;

namespace NtfyDesktop.Features.Rules;

/// <summary>
/// Loads rule packs from a directory of *.json files. A file that fails to parse is
/// skipped (logged), never fatal — one bad pack can't disable the whole engine.
/// </summary>
public sealed class PackStore
{
    private readonly string _directory;

    public PackStore(string directory)
    {
        _directory = directory;
        Reload();
    }

    public IReadOnlyList<RulePack> Packs { get; private set; } = [];

    public void Reload()
    {
        if (!Directory.Exists(_directory))
        {
            Packs = [];
            return;
        }

        var packs = new List<RulePack>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try { packs.Add(PackParser.Parse(File.ReadAllText(file))); }
            catch (Exception ex) { Debug.WriteLine($"[Rules] skipped invalid pack {file}: {ex.Message}"); }
        }
        Packs = packs;
    }
}
```

- [ ] **Step 8: Run to verify everything passes**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj --filter PackStoreTests`
Expected: PASS — all 3 tests green. Then the full suite: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj` → all green.

- [ ] **Step 9: Commit**

```bash
git add Features/Rules/PackParser.cs Features/Rules/PackStore.cs NtfyDesktop.Tests/Rules/PackParserTests.cs NtfyDesktop.Tests/Rules/PackStoreTests.cs
git commit -m "feat(rules): add pack JSON parser and directory store"
```

---

## Task 9: Wire the Rules feature into DI

Registers the store, engine, and pack store, and changes the `RuleEngine`
constructor from the test-only `Func` provider to taking `PackStore` directly.

**Files:**
- Create: `Features/Rules/RulesFeature.cs`
- Modify: `Features/Rules/RuleEngine.cs` (constructor)
- Modify: `Features/AppCompositionExtensions.cs`
- Modify: `NtfyDesktop.Tests/Rules/RuleEngineMatchTests.cs` and `RuleEngineCorrelateTests.cs` (construct via a real `PackStore` over a temp dir, OR keep the `Func` overload — see Step 1)

**Interfaces:**
- Consumes: `RuleEngine`, `PackStore`, `IncidentStore`, `IIncidentStore`, `AppSettings`, `App.DataPath`.
- Produces: `AddRules()` extension registering `IIncidentStore` → `IncidentStore` (path `App.DataPath\rules.db`, password from settings), `PackStore` (dir `App.DataPath\rules`), and `RuleEngine` as singletons.

- [ ] **Step 1: Change RuleEngine to take PackStore (keep tests working)**

To avoid breaking the Task 5/6 tests that pass a `Func<IReadOnlyList<RulePack>>`,
give `RuleEngine` a primary constructor taking `PackStore` and a second constructor
for tests. In `Features/Rules/RuleEngine.cs`, replace the class declaration line and
add a test constructor. The class header becomes:

```csharp
public sealed class RuleEngine
{
    private readonly AppSettings _settings;
    private readonly Func<IReadOnlyList<RulePack>> _packsProvider;
    private readonly IIncidentStore _incidents;

    /// <summary>Production constructor: reads packs from the loaded PackStore.</summary>
    public RuleEngine(AppSettings settings, PackStore packs, IIncidentStore incidents)
        : this(settings, () => packs.Packs, incidents) { }

    /// <summary>Test constructor: packs supplied directly.</summary>
    public RuleEngine(AppSettings settings, Func<IReadOnlyList<RulePack>> packsProvider, IIncidentStore incidents)
    {
        _settings = settings;
        _packsProvider = packsProvider;
        _incidents = incidents;
    }
```

Then update the method bodies to use the `_settings`, `_packsProvider`, and
`_incidents` fields instead of the old primary-constructor parameter names
(`settings` → `_settings`, `packsProvider()` → `_packsProvider()`, `incidents` →
`_incidents`). The existing tests using the `Func` overload keep compiling unchanged.

- [ ] **Step 2: Run the full suite to confirm tests still pass**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`
Expected: PASS — all tests still green (constructor refactor only).

- [ ] **Step 3: Create RulesFeature.cs**

```csharp
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NtfyDesktop.Features.Settings;

namespace NtfyDesktop.Features.Rules;

public static class RulesFeature
{
    extension(IServiceCollection services)
    {
        public void AddRules()
        {
            services.AddSingleton<IIncidentStore>(sp => new IncidentStore(
                Path.Combine(App.DataPath, "rules.db"),
                sp.GetRequiredService<AppSettings>().GetOrCreateHistoryKey()));

            services.AddSingleton<PackStore>(_ => new PackStore(
                Path.Combine(App.DataPath, "rules")));

            services.AddSingleton<RuleEngine>();
        }
    }
}
```

- [ ] **Step 4: Wire it into composition**

In `Features/AppCompositionExtensions.cs`, add the `using` and the call. Add to the
`using` block:

```csharp
using NtfyDesktop.Features.Rules;
```

And inside `AddNtfyDesktop`, add alongside the other `services.Add*()` calls (place
it after `services.AddHistory();` since the engine's store depends on the same
settings/key infrastructure):

```csharp
            services.AddRules();
```

- [ ] **Step 5: Verify it builds**

Run: `dotnet build NtfyDesktop.csproj`
Expected: build succeeds. (`App.DataPath` and `AppSettings.GetOrCreateHistoryKey()`
already exist.)

- [ ] **Step 6: Commit**

```bash
git add Features/Rules/RulesFeature.cs Features/Rules/RuleEngine.cs Features/AppCompositionExtensions.cs
git commit -m "feat(rules): register Rules feature in DI"
```

---

## Task 10: Persist the suppressed flag in history

Adds the `suppressed` column and threads it through `Insert`, `Query`,
`GetUnreadCounts`, and the row model.

**Note on testing:** `HistoryRepository` is coupled to the static `App.DataPath` and
the DPAPI key, so it isn't unit-tested here (the codebase has no existing repo tests
and redirecting its path would be an out-of-scope refactor). These changes are
verified by `dotnet build` and the running-app verification in Task 13.

**Files:**
- Modify: `Features/History/HistoryRepository.cs`
- Modify: `Features/History/HistoryMessage.cs`

**Interfaces:**
- Produces:
  - `HistoryMessage.Suppressed` (`bool`).
  - `HistoryRepository.Insert(NtfyMessage message, Guid topicId, Guid serverId, bool suppressed = false)`.
  - `HistoryRepository.Query(..., bool includeSuppressed = false)` — excludes suppressed rows unless asked.
  - `GetUnreadCounts()` excludes suppressed rows.

- [ ] **Step 1: Add the column + index**

In `Features/History/HistoryRepository.cs`, inside `InitializeDatabase()`, after the
`content_type` `EnsureColumn` call (around line 70) add:

```csharp
        // Rule-engine verdict: 1 when the engine suppressed this message (no toast,
        // hidden from the feed by default, excluded from the unread count). Added via
        // the same EnsureColumn migration as the other forward-compatible columns.
        EnsureColumn(conn, "suppressed", "INTEGER NOT NULL DEFAULT 0");
```

- [ ] **Step 2: Add Suppressed to HistoryMessage**

In `Features/History/HistoryMessage.cs`, add alongside the other write-once
properties (e.g. after `public string? Click { get; set; }`):

```csharp
    /// <summary>True when the rule engine suppressed this message — hidden from the
    /// feed by default and excluded from the unread count.</summary>
    public bool Suppressed { get; set; }
```

- [ ] **Step 3: Thread suppressed through Insert**

In `HistoryRepository.Insert`, change the signature:

```csharp
    public bool Insert(NtfyMessage message, Guid topicId, Guid serverId, bool suppressed = false)
```

Add `suppressed` to the column list and VALUES in the INSERT command text (extend the
existing statement):

```csharp
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages
                (message_id, topic, topic_id, server_id, timestamp, priority, title, body, tags, click, attachment, actions, content_type, suppressed)
            VALUES
                (@mid, @topic, @topicId, @serverId, @ts, @priority, @title, @body, @tags, @click, @attachment, @actions, @contentType, @suppressed)
            """;
```

Add the parameter alongside the others (after the `@contentType` parameter):

```csharp
        cmd.Parameters.AddWithValue("@suppressed", suppressed ? 1 : 0);
```

And in `ToHistoryMessage`, set it so the `MessageInserted` event carries the flag.
Change the `ToHistoryMessage` call site in `Insert` to pass `suppressed`:

```csharp
        var histMsg = ToHistoryMessage(message, topicId, suppressed);
```

- [ ] **Step 4: Update ToHistoryMessage and ReadRow**

Change `ToHistoryMessage`'s signature and body:

```csharp
    private static HistoryMessage ToHistoryMessage(NtfyMessage m, Guid topicId, bool suppressed) => new()
    {
        MessageId = m.Id,
        Topic = m.Topic,
        TopicId = topicId,
        Timestamp = m.Timestamp,
        Priority = m.Priority,
        Title = m.Title,
        Body = m.Message,
        Tags = m.Tags?.Count > 0 ? string.Join(",", m.Tags) : null,
        Click = m.Click,
        Attachment = m.Attachment,
        Actions = m.Actions,
        ContentType = m.ContentType,
        Suppressed = suppressed,
    };
```

In `ReadRow`, read the column (it's an INTEGER with a default, so always present
after migration). Add to the returned object initializer:

```csharp
            Suppressed = !r.IsDBNull(Col("suppressed")) && r.GetInt64(Col("suppressed")) != 0,
```

- [ ] **Step 5: Filter suppressed out of Query and GetUnreadCounts**

Change the `Query` signature to add the opt-in:

```csharp
    public List<HistoryMessage> Query(
        Guid? topicId = null,
        Priority? minPriority = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 500,
        bool includeSuppressed = false)
```

Inside `Query`, after the other `conditions.Add(...)` lines, add:

```csharp
        if (!includeSuppressed) conditions.Add("suppressed = 0");
```

In `GetUnreadCounts`, change the command text to exclude suppressed rows:

```csharp
        cmd.CommandText = "SELECT topic_id, COUNT(*) FROM messages WHERE read = 0 AND suppressed = 0 GROUP BY topic_id";
```

- [ ] **Step 6: Verify it builds**

Run: `dotnet build NtfyDesktop.csproj`
Expected: build succeeds. (`ConnectionManager`'s existing `Insert` call still compiles
because `suppressed` has a default; it's updated in Task 11.)

- [ ] **Step 7: Commit**

```bash
git add Features/History/HistoryRepository.cs Features/History/HistoryMessage.cs
git commit -m "feat(history): persist and filter the suppressed flag"
```

---

## Task 11: Integrate the engine into the message pipeline

Evaluate the engine in `ConnectionManager`, persist the verdict, apply incident
side-effects only for new messages, and carry the suppressed flag to the toast path.

**Note on testing:** `ConnectionManager` and `ShowToastNotification` are pipeline
glue over sockets and the event bus; they're verified by build + the running-app
test in Task 13, not unit tests.

**Files:**
- Modify: `Features/Connections/NtfyMessageReceived.cs`
- Modify: `Features/Connections/ConnectionManager.cs`
- Modify: `Features/Notifications/ShowToastNotification.cs`

**Interfaces:**
- Consumes: `RuleEngine.Evaluate(NtfyMessage)` → `RuleVerdict`; `RuleEngine.ApplyIncidentSideEffects(RuleVerdict)`; `HistoryRepository.Insert(..., bool suppressed)`.
- Produces: `NtfyMessageReceived` gains `bool Suppressed = false`.

- [ ] **Step 1: Add Suppressed to the event**

In `Features/Connections/NtfyMessageReceived.cs`, extend the record:

```csharp
public record NtfyMessageReceived(NtfyMessage Message, Guid TopicId, bool IsBackfill = false, bool Suppressed = false) : IEvent;
```

- [ ] **Step 2: Inject RuleEngine into ConnectionManager**

In `Features/Connections/ConnectionManager.cs`, add the using and the field, and
extend the constructor. Add to the using block:

```csharp
using NtfyDesktop.Features.Rules;
```

Add a field next to the others:

```csharp
    private readonly RuleEngine _rules;
```

Change the constructor signature and body:

```csharp
    public ConnectionManager(AppSettings settings, HistoryRepository history, RuleEngine rules)
    {
        _settings = settings;
        _history = history;
        _rules = rules;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }
```

- [ ] **Step 3: Evaluate and thread the verdict in OnMessageReceived**

Replace the body of `OnMessageReceived` (currently lines ~273–289) with:

```csharp
    private void OnMessageReceived(object? sender, IncomingMessage incoming)
    {
        if (sender is not TopicConnection conn) return;

        var topic = _settings.GetTopicById(conn.TopicId);
        var serverId = topic?.ServerId ?? Guid.Empty;

        // Deterministic rule engine decides suppression + correlation. Pure read here;
        // incident-store writes are applied below only for genuinely-new messages.
        var verdict = _rules.Evaluate(incoming.Message);

        // Insert is INSERT-OR-IGNORE and reports novelty. A since=<time> catch-up is
        // inclusive of its boundary, so it re-delivers messages we already have — those
        // aren't new and must not reach the toast path or re-mutate incident state.
        var isNew = _history.Insert(incoming.Message, conn.TopicId, serverId, verdict.Suppress);
        if (!isNew) return;

        // Apply incident side-effects (open/resolve) once, for the new message only.
        _rules.ApplyIncidentSideEffects(verdict);

        new NtfyMessageReceived(incoming.Message, conn.TopicId, incoming.IsBackfill, verdict.Suppress)
            .PublishAsync();
    }
```

- [ ] **Step 4: Drop suppressed messages in ShowToastNotification**

In `Features/Notifications/ShowToastNotification.cs`, at the start of `HandleAsync`
(right after `var message = eventModel.Message;`), add:

```csharp
        // The rule engine suppressed this message: no toast, and don't count it in the
        // catch-up summary either. It's still in history (hidden from the feed by default).
        if (eventModel.Suppressed)
            return Task.CompletedTask;
```

- [ ] **Step 5: Verify it builds**

Run: `dotnet build NtfyDesktop.csproj`
Expected: build succeeds. (DI already provides `RuleEngine` via Task 9.)

- [ ] **Step 6: Commit**

```bash
git add Features/Connections/NtfyMessageReceived.cs Features/Connections/ConnectionManager.cs Features/Notifications/ShowToastNotification.cs
git commit -m "feat(rules): evaluate engine in pipeline and suppress toasts"
```

---

## Task 12: Exclude suppressed messages from the unread count

**Files:**
- Modify: `Features/Unread/UnreadTracker.cs`

**Interfaces:**
- Consumes: `HistoryMessage.Suppressed` (Task 10).

- [ ] **Step 1: Skip suppressed in the incremental path**

In `Features/Unread/UnreadTracker.cs`, at the top of `OnMessageInserted`, add an
early return (the DB-sourced `GetUnreadCounts` already excludes suppressed rows, so
the cache stays consistent):

```csharp
    private void OnMessageInserted(HistoryMessage m)
    {
        // Suppressed messages don't nag: they're excluded from the unread count
        // (GetUnreadCounts also filters them, so the seeded cache agrees).
        if (m.Suppressed) return;

        bool viewing;
        // ... rest unchanged ...
```

- [ ] **Step 2: Verify it builds**

Run: `dotnet build NtfyDesktop.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Features/Unread/UnreadTracker.cs
git commit -m "feat(unread): exclude suppressed messages from the badge"
```

---

## Task 13: Hide suppressed messages in the feed + "Show suppressed" toggle

Adds the feed default-hide behavior, the toggle property, and the UI control. Ends
with end-to-end verification in the running app.

**Files:**
- Modify: `Features/Feed/FeedViewModel.cs`
- Modify: `Features/Feed/FeedPage.xaml`

**Interfaces:**
- Consumes: `HistoryRepository.Query(..., bool includeSuppressed)` (Task 10); `HistoryMessage.Suppressed`.
- Produces: `FeedViewModel.ShowSuppressed` (`bool`, observable) driving reloads and live-insert filtering.

- [ ] **Step 1: Add the ShowSuppressed property + reload trigger**

In `Features/Feed/FeedViewModel.cs`, add an observable property next to the other
`[ObservableProperty]` fields (near `_minPriority`):

```csharp
    [ObservableProperty] private bool _showSuppressed;
```

Add a change handler next to the existing `OnSearchTextChanged` / `OnMinPriorityChanged`:

```csharp
    partial void OnShowSuppressedChanged(bool value) => _ = ReloadAsync();
```

- [ ] **Step 2: Pass includeSuppressed into the query**

In `ReloadAsync`, capture the flag and pass it to `Query`. Change the local capture
block (near the top of `ReloadAsync`) and the `_history.Query(...)` call:

```csharp
        var topicId = CurrentTopicId;
        var minP = MinPriority;
        var search = SearchText;
        var includeSuppressed = ShowSuppressed;

        var allTopics = topicId is null;

        var loaded = await Task.Run(() =>
        {
            var raw = _history.Query(topicId: topicId, minPriority: minP, limit: MAX_DISPLAYED,
                includeSuppressed: includeSuppressed);
            // ... rest unchanged ...
```

- [ ] **Step 3: Filter suppressed out of live inserts**

In `OnMessageInserted`, add a guard after the existing topic/priority/search guards:

```csharp
    private void OnMessageInserted(HistoryMessage m)
    {
        if (CurrentTopicId is { } id && m.TopicId != id) return;
        if (m.Priority < MinPriority) return;
        if (m.Suppressed && !ShowSuppressed) return;
        if (!string.IsNullOrWhiteSpace(SearchText) && !Matches(m, SearchText)) return;
        // ... rest unchanged ...
```

- [ ] **Step 4: Add the toggle to the feed header**

In `Features/Feed/FeedPage.xaml`, add a "Show suppressed" toggle to the header's
right-hand `StackPanel` (the one at `Grid.Column="1"`, currently holding Reconnect
and Clear, starting at line ~70). Insert before the `Reconnect` button:

```xml
                <ui:ToggleSwitch Content="Show suppressed"
                                 IsChecked="{Binding ShowSuppressed, Mode=TwoWay}"
                                 Margin="0,0,12,0"
                                 VerticalAlignment="Center" />
```

(If `ui:ToggleSwitch` doesn't render well inline, a standard WPF `CheckBox` bound the
same way is an acceptable fallback — the binding is what matters.)

- [ ] **Step 5: Verify the full suite still passes + build**

Run: `dotnet test NtfyDesktop.Tests/NtfyDesktop.Tests.csproj`
Expected: PASS — all engine/parser/store tests green.
Run: `dotnet build NtfyDesktop.csproj`
Expected: build succeeds.

- [ ] **Step 6: End-to-end verification in the running app**

This is the real gate (per DEVELOPMENT.md — a green build is not enough).

1. Create a pack file at `%AppData%\NtfyDesktop\rules\test.json`:

```json
{
  "name": "Test",
  "rules": [
    { "type": "match",
      "when": { "titleRegex": "succeeded" },
      "do": ["suppressToast"] },
    { "type": "correlate",
      "open":  { "titleRegex": "^PROBLEM" },
      "close": { "titleRegex": "^RESOLVED" },
      "key":   { "from": "body", "regex": "id=(?<key>\\d+)" },
      "onClose": ["suppressToast"] }
  ]
}
```

2. Run the app (`dotnet run --project NtfyDesktop.csproj` or via IDE). Subscribe a
   test topic on a reachable ntfy server.
3. Publish a message with title "Backup succeeded" → **expect: no toast**, no unread
   badge bump, and it does NOT appear in the feed until "Show suppressed" is toggled on.
4. Publish title "PROBLEM" body "id=7" → **expect: normal toast** (and feed row).
5. Publish title "RESOLVED" body "id=7" → **expect: no toast**, hidden from feed by
   default (the matching open incident existed).
6. Publish title "RESOLVED" body "id=999" (no prior PROBLEM) → **expect: normal toast**
   (no open incident to pair with).
7. Toggle "Show suppressed" on → the suppressed rows appear; toggle off → they hide.

Confirm each expectation before considering the task complete. If any differs,
debug before committing (use systematic-debugging).

- [ ] **Step 7: Commit**

```bash
git add Features/Feed/FeedViewModel.cs Features/Feed/FeedPage.xaml
git commit -m "feat(feed): hide suppressed messages with a Show suppressed toggle"
```

---

## Self-Review

**Spec coverage (Phase 1a sections of the design doc):**
- Generic engine (matchers/actions) → Tasks 2, 4, 5.
- Correlation (problem/resolved pairing, suppress on close) → Tasks 3, 6, 7.
- Declarative pack format + loader → Tasks 4, 8.
- Tool knowledge as data, not code → pack JSON (Task 8); no tool names in engine.
- Verdict persistence (suppressed column) → Task 10.
- Pipeline integration (toast suppression, fail-open, new-only side effects) → Tasks 9, 11.
- Suppressed hidden from feed by default + "Show suppressed" → Task 13.
- Suppressed excluded from unread → Task 12.
- Encryption at rest for the incident store → Task 7.
- Master toggle (`RulesEnabled`) → Task 5 (property) + Task 9 (DI).
- Deferred (1b heartbeat, 1c AI, digest, dismissOriginal, Phase 2 UI) → documented in Scope; parser tolerates the deferred action strings.

**Placeholder scan:** none — every code step has complete code and every test step has runnable assertions.

**Type consistency:** `RuleEngine.Evaluate`/`ApplyIncidentSideEffects`, `IIncidentStore.FindOpen/Open/Resolve`, `RuleVerdict.{Suppress,Tags,OpenIncident,CloseIncident}`, `Insert(..., bool suppressed)`, `Query(..., bool includeSuppressed)`, and `NtfyMessageReceived.Suppressed` are used consistently across Tasks 5–13. The `RuleEngine` two-constructor approach (Task 9) keeps the Task 5/6 `Func`-based tests valid.

---

## Addendum — Correlation correction (Tasks 14–15)

After Phase 1a was tested in the running app, the maintainer flagged that
*suppressing the resolved toast* is the wrong behaviour: a problem opening **and**
closing are both things to be told about live; the noise is only the feed clutter.
The behaviour was corrected (design doc Decisions log updated accordingly):

- **Verdict split into two axes.** `RuleVerdict.Suppress` → `SuppressToast` +
  `HideFromFeed` (+ `DismissMessageId`). A `match` `suppressToast` rule sets both
  (pure noise: no toast, no feed row). A correlated *resolution* sets only
  `HideFromFeed` — it still toasts.
- **Correlation folds, doesn't silence.** `CorrelateRule.OnClose` removed (folding is
  intrinsic). On a close that pairs with an open incident: the resolution toasts, is
  stored hidden from the feed, and the **original problem row is retroactively hidden**
  via `HistoryRepository.SuppressMessage` → new `MessageSuppressed` event. The feed
  becomes a list of still-open problems.
- **Reactions.** `FeedViewModel` drops the row on `MessageSuppressed` (unless "Show
  suppressed"); `UnreadTracker` re-seeds. `NtfyMessageReceived.Suppressed` →
  `SuppressToast`.
- **Tests updated:** `RuleEngineMatchTests`, `RuleEngineCorrelateTests`,
  `PackParserTests` assert the two-axis verdict and intrinsic folding (34 green).

### Updated manual-test expectations (supersedes Task 13 Step 6, steps 4–7)

4. Title **"PROBLEM"** body **"id=7"** → normal toast + feed row (an open issue).
5. Title **"RESOLVED"** body **"id=7"** → **toast still shown**, and **both** the
   problem and resolved rows disappear from the default feed (incident folded).
6. Title **"RESOLVED"** body **"id=999"** (no prior PROBLEM) → normal toast + feed row.
7. Toggle **"Show suppressed"** on → the folded problem + resolved rows reappear.
