# Notification Rule Engine — Design

**Date:** 2026-06-19
**Status:** Approved design, pending implementation plan
**Feature area:** `Features/Rules/` (new)

## Problem

Users who point ntfy-desktop at IT-monitoring tools receive a high volume of
notifications, most of which are noise. The noise is not separable by priority
alone, because two patterns defeat a simple threshold filter:

- **Success-heartbeat noise.** A database-backup tool notifies on every
  *successful* backup. Each individual success is noise — but the *absence* of
  the expected success is the real problem (the backup service is down or can no
  longer notify). A filter that just drops the successes would also hide the one
  signal that matters.
- **Paired problem/resolved events.** Zabbix sends a notification when a problem
  opens and another when it resolves, at the *same* priority. The resolved event
  is noise relative to its problem, but nothing in its priority distinguishes it.

The feature must be **generic**: these examples are specific to this user's
tools, but other users run completely different tools. The engine must handle
previously-unknown tools and let tool-specific behaviour be added without code
changes.

## Guiding principles

1. **Deterministic core, AI on the side.** Every runtime alerting decision is
   made by inspectable, testable rules. AI is used only to *author* rules, never
   to decide at runtime whether the user sees an alert.
2. **Never lose a message.** Rules affect *toasting and feed/unread routing
   only* — the history insert is untouched. This mirrors the existing
   `ConnectionManager` ↔ `NotificationGate` separation: like pause, the engine
   never blocks the socket or the history write.
3. **Generic engine, tool knowledge as data.** No Zabbix/backup logic in code —
   only matcher patterns and key-extractors, which live in declarative packs
   (data, not compiled plugins; loading untrusted assemblies on a desktop app is
   a security liability we explicitly avoid).
4. **Fail open.** A malformed pack or a rule exception is logged and skipped. A
   message is never silently lost because a rule misbehaved.

## Where AI fits (and where it does not)

AI is an **authoring assistant**, not a runtime gatekeeper:

- **Good fit:** drafting a starter pack from pasted sample messages of an unknown
  tool — guessing which messages are noise, proposing a correlation key,
  suggesting a heartbeat interval. Runs on-demand, off the live hot-path, and the
  user reviews the deterministic rules it produces before they take effect.
- **Deliberately *not* used for:** runtime suppression decisions (a
  non-deterministic model could silently eat a real alert), and absence detection
  (pure timing logic — a model adds nothing and could miss a missing backup, the
  worst outcome).

This split yields a fully-working noise filter with zero AI configured, and makes
AI the thing that removes the pain of authoring rules for unfamiliar tools.

## Architecture

### New feature: `Features/Rules/`

Follows the established per-feature convention: its own folder, an `AddRules()`
DI extension registered in `AppCompositionExtensions`, and its own `Events/`.

### The pack format (the foundation)

A **pack** is a JSON file under `App.DataPath\rules\*.json` — metadata plus a
list of rules. This format is the API: the phase-2 rule-builder UI and the
future community library both read and write the same files.

Three rule types:

```jsonc
{
  "name": "Zabbix",
  "rules": [
    // CORRELATION — pairs problem/resolved, suppresses the resolved
    { "type": "correlate",
      "open":  { "titleRegex": "^PROBLEM" },
      "close": { "titleRegex": "^RESOLVED" },
      "key":   { "from": "body", "regex": "Event ID: (?<key>\\d+)" },
      "onClose": ["suppressToast", "dismissOriginal"] },

    // MATCH — straight suppression
    { "type": "match",
      "when": { "topic": "backups", "titleRegex": "succeeded" },
      "do":   ["suppressToast"] },

    // EXPECT — heartbeat / dead-man's-switch
    { "type": "expect",
      "when":      { "topic": "backups", "titleRegex": "succeeded" },
      "every":     "26h",
      "grace":     "1h",
      "onAbsence": { "priority": "max", "title": "Backup heartbeat missed" } }
  ]
}
```

- **Matcher** (`when` / `open` / `close`): predicates over the message's
  structured fields — `topic`, `priority` (with comparison operators),
  `titleRegex`, `bodyRegex`, `tag`. Conditions within a matcher are combined with
  AND (keeps the format legible and the phase-2 UI straightforward).
- **Actions** (`do` / `onClose`): the phase-1 set is `suppressToast`, `digest`,
  `tag`, `dismissOriginal`. The action model is extensible — `downgrade` was
  considered and deliberately cut (see Decisions).
- **Key extractor** (`key`): a named-capture regex (`(?<key>…)`) over the title
  or body, producing the correlation key.

### Runtime components

- **`RuleEngine`** (singleton). Evaluates an incoming `NtfyMessage` against the
  loaded `match` and `correlate` rules and returns a `RuleVerdict`
  (`Suppress`, `DigestOnly`, `Tags`, `ClosesIncidentKey`). Pure and fast — the
  only I/O is a correlation-state lookup. Deterministic, so heavily
  unit-testable.
- **`IncidentStore`** (new SQLite table). An `open`-match records an open
  incident keyed by the extracted key; a `close`-match with the same key resolves
  it. **Correlation folds resolved incidents out of the feed, it does not silence
  them** (revised after Phase 1a testing — see Decisions): a problem and its
  resolution *both toast live* (both are things the user wants to know), but once
  paired, **both messages are hidden from the default feed** — the resolved row is
  stored hidden, and the original problem row is retroactively hidden via a
  `MessageSuppressed` signal. The feed is therefore a list of *open / unresolved*
  problems plus non-correlated messages; a still-open problem stays visible because
  no resolution arrived to fold it away. The table is the foundation for the later
  "open incidents" view.
- **`ExpectationMonitor`** (`BackgroundService`). Persists per-`expect`-rule
  last-seen timestamps in SQLite. A timer detects overdue expectations
  (interval + grace exceeded) and raises a **synthetic high-priority
  notification** for the absence. De-dupes: one alert per outage, re-arming when a
  matching message next arrives.
- **`PackStore`**. Loads and validates packs at startup, exposes them to the
  engine, reloads on change. A master on/off toggle lives in settings.
- **`PackDraftService`** (AI authoring — Phase 1c, design confirmed). Orchestrates:
  build request → call endpoint → parse → validate. The user never writes a raw
  prompt: the app owns a **built-in system prompt** (pack schema + the three rule
  types + the shared-key constraint + worked examples); the user supplies only
  **sample messages** (picked from the topic's stored history, or pasted) and an
  optional **one-line plain-English intent**. The model returns a draft pack;
  `PackDraftService` strips any markdown fences, parses with `PackParser`, and the
  dialog shows a **plain-English summary** (via `PackSummarizer`) plus the **editable
  JSON** before save. Saving writes a pack file and calls `PackStore.Reload()` so it
  takes effect without a restart. Calls the **OpenAI-compatible** endpoint via an
  `IChatClient` seam (HTTP impl isolated; orchestration tested against a fake). The
  API key is DPAPI-wrapped at rest like the access token (`TokenProtector`).
  - **Provider presets:** a bundled-but-**overridable** `providers.json` (in
    `App.DataPath`) lists known providers' base URLs + default model + auth — editable
    without an app release (base URLs rarely change). The **model list is fetched live**
    from the chosen provider's `GET /v1/models` (the provider maintains it, so models
    never go stale), with graceful fallback to the preset's default model or a manual
    field. A **"Custom"** option always allows a raw base URL + model.
  - **Entry points:** a per-topic "Draft rules from this topic…" rail-menu item
    (pre-loads that topic's recent messages) and a "Draft rules with AI…" button in
    Settings.
  - **Settings (Phase 1c):** AI endpoint config (provider/model/key) **and** the
    `RulesEnabled` master toggle (pulled forward from Phase 2 at the maintainer's
    request).

### Pipeline integration

One new stage in the existing toast-gating path; the rest of the pipeline is
unchanged.

```
ConnectionManager.OnMessageReceived
  └─ HistoryRepository.Insert  (unchanged — always stores;
       │                        feeds RuleEngine + ExpectationMonitor + IncidentStore)
       └─ MessageInserted → Feed / Unread  (now verdict-aware; see below)
  └─ if isNew → NtfyMessageReceived
       └─ ShowToastNotification
            ├─ RuleEngine.Evaluate → verdict          ← NEW
            │     suppress → no toast (still in history)
            │     digest   → folded into a periodic summary
            └─ NotificationGate (pause / priority / active-hours)  (unchanged)
```

The verdict is persisted on the message via a new `messages` column (added with
the existing `EnsureColumn` migration helper), so the feed can segregate
suppressed/digested rows and the digest can collect its members.

### Feed, unread, and digest behaviour

- **Suppressed messages are hidden from the feed by default.** The feed query
  filters them out; a **"Show suppressed"** toggle (on per-topic feeds and on
  All-topics) reveals them. This makes the feed itself a noise-reduction surface,
  not just the toasts.
- **Suppressed messages do not bump the unread badge.** `UnreadTracker` ignores
  suppressed rows — otherwise toast noise would be reduced but a red count would
  still nag. When a message is retroactively hidden (a problem folded by its
  resolution), `UnreadTracker` re-seeds so the now-hidden row drops out of the count.
- **Toast-suppression and feed-hiding are separate axes.** A `match` `suppressToast`
  rule hides from the feed *and* drops the toast (the backup-success case: pure
  noise). A correlated *resolution* does the opposite on the toast axis — it is
  *shown* live but *hidden* from the feed. The engine's verdict carries the two as
  distinct flags (`SuppressToast`, `HideFromFeed`).
- **Digest model:** the individual folded messages behave like suppressed ones
  (hidden by default, revealed by the toggle). The **periodic summary itself**
  (e.g. "overnight: 6 backups OK") surfaces as a **normal message in the feed**,
  reusing the same mechanism as the existing "N messages while you were away"
  backfill summary.

## Edge cases

- **Backfill feeds state but does not spam.** Catch-up (`?since=`) replayed
  messages update expectation last-seen and incident state, but
  suppressed/digested backfill folds into the existing `BackfillSummaryNotifier`
  rather than producing a flood.
- **No false absence alerts at startup.** `ExpectationMonitor` applies a startup
  grace and waits for catch-up to settle before it can trip — otherwise reopening
  the app after a quiet night would instantly false-fire a heartbeat alert.
- **Fail open** (restated from principles): a rule exception or bad pack is logged
  and skipped; the message still flows through the normal gate.

## Testing

The deterministic core carries the bulk of the value and is almost entirely pure,
so it is built test-first:

- Matcher predicates (each field type, operators, AND combination).
- Correlation: key extraction, open/close pairing, suppress + dismiss verdict.
- `ExpectationMonitor` timing with an **injected clock** (no real waiting),
  including the startup-grace and re-arm behaviour.
- `IPackDrafter` is mocked; the live endpoint call is isolated behind the seam.

## Decisions log

- **`downgrade` action cut from phase 1.** Its use case ("see it but quietly — no
  sound, doesn't break active-hours") is a narrow middle ground already covered by
  suppress (hidden feed) + digest. The action model stays extensible, so it can be
  added later with no rework. (YAGNI.)
- **Output behaviour:** suppress is the least-destructive default (no toast, kept
  in history, now also hidden from feed + excluded from unread); digest and the
  absence alert escalate from there.
- **AI runtime:** bring-your-own OpenAI-compatible endpoint — defers the
  privacy/cost choice to the user and works with Anthropic-compat, OpenAI, Ollama,
  or a self-hosted model.
- **Correlation behaviour — revised after Phase 1a testing.** The original design
  *suppressed the resolved toast*. In practice that's backwards: the maintainer
  wants to be told both when a problem opens **and** when it closes (the resolution
  is the "all good" signal, not noise). So correlation now **shows both toasts** and
  folds only at the **feed** level — once a resolution pairs with its problem, *both*
  the problem and the resolution are hidden from the default feed, leaving the feed
  as a list of still-open problems. A problem with no resolution stays visible (the
  actionable case). This also realises the deferred `dismissOriginal` idea (the
  problem row is retroactively hidden) and requires splitting the verdict's single
  "suppress" into `SuppressToast` vs `HideFromFeed`.
- **Correlation scope:** noise-reduction (pairing + feed-folding) now; stateful
  "open incidents" view deferred. The `IncidentStore` table is the foundation for
  it.
- **Tool knowledge is data, not code:** declarative shareable packs, not loadable
  plugin assemblies (avoids untrusted-code execution on the desktop).
- **AI authoring — app owns the prompt (Phase 1c).** The user never engineers a
  prompt; they pick sample messages (from history) + an optional one-line intent, and
  the app supplies the schema-aware system prompt. Samples come from stored history
  (with a paste fallback); review shows a plain-English summary + editable JSON.
- **Provider presets are data + live model fetch.** Base URLs live in an overridable
  `providers.json` (no app release to change); models are fetched live from the
  provider's `/v1/models` so the volatile list never needs maintaining. Two staleness
  problems, solved separately.
- **`RulesEnabled` toggle pulled into Phase 1c** (was Phase 2) so the master switch
  ships with the engine's first user-facing surface.

## Build order

1. **Phase 1a:** pack format + `RuleEngine` (match + correlate) + `IncidentStore`
   + pipeline integration + verdict persistence + feed/unread behaviour.
2. **Phase 1b:** `ExpectationMonitor` (heartbeat / absence).
3. **Phase 1c:** AI authoring (`PackDraftService`, `IChatClient`, `PackSummarizer`,
   provider presets + live models, the draft dialog) + endpoint settings + the
   `RulesEnabled` toggle.
4. **Phase 2:** in-app rule-builder UI over the same pack format.
5. **Future:** stateful "open incidents" view; community pack library.
