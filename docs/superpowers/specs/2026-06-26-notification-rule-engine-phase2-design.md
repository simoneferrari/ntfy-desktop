# Notification Rule Engine — Phase 2 Design (in-app rule-pack manager)

**Date:** 2026-06-26
**Status:** Approved design, pending implementation plan
**Feature area:** `Features/Rules/` (extends the Phase 1 engine)
**Builds on:** [2026-06-19-notification-rule-engine-design.md](2026-06-19-notification-rule-engine-design.md)

## Problem

Phase 1 shipped the deterministic engine, the pack format, and AI-assisted authoring.
But the only way to *manage* rules is to hand-edit JSON files under
`App.DataPath\rules\*.json` (or re-run the AI draft dialog). There is no in-app way to:

- **browse** existing packs and the rules inside them,
- **create** a pack/rule from scratch without writing JSON,
- **edit** a rule's fields,
- **enable/disable** a pack or an individual rule,
- **delete** a pack or a rule,
- **see what a pack would actually do** to your messages before trusting it, or
- **clean up history** that arrived before the rule existed.

Phase 2 delivers that management surface over the **same pack format** — the format
remains the API that the AI dialog and a future community library also read and write.

## Scope decisions (settled in brainstorming)

1. **Full structured forms** for all three rule types (`match`, `correlate`, `expect`).
   No raw-JSON editing in the manager — the roadmap's "without hand-editing JSON" is met
   literally.
2. **A dedicated master–detail window** (`FluentWindow`, like `DraftRulesDialog`),
   launched from Settings → Rules. Not a Settings sub-page, not a rail item.
3. **Enable/disable state lives in the pack JSON** (`enabled` field on the pack and on each
   rule; absent ⇒ `true`, so every existing Phase-1 pack keeps working). A *future* export
   feature (the 0.9 import/export item) will write `enabled: false` throughout the exported
   file so an importer opts in explicitly — that export behaviour is noted here but is **not**
   built in Phase 2.
4. **The AI draft dialog is integrated** as a creation path: inside the manager,
   *New pack → Draft with AI…* reuses the existing `DraftRulesDialog`. The standalone
   "Draft rules with AI…" button in Settings → Rules is replaced by "Manage rule packs…".
   The per-topic rail entry ("Draft rules from this topic…") is unchanged.
5. **Preview/test against history** — a read-only dry-run of the *selected pack* against
   stored messages.
6. **Apply against history** — an additive, one-shot backfill of the *selected pack* onto
   already-stored rows. No automatic undo; a full reset/recompute is **deferred** to a later
   phase.
7. **Stable per-rule ids** replace array-position keying (`pack#index`) so editing/reordering
   in the manager no longer orphans heartbeat/incident state.

## Persistence reality this design builds on

Phase 1 persists exactly **one** verdict axis on a stored message: a `Suppressed` boolean
column on `messages` meaning *hidden from the feed by default and excluded from the unread
count*. It is:

- written at insert from `verdict.HideFromFeed`
  (`HistoryRepository.Insert(message, topicId, serverId, suppressed)`), and
- set *retroactively* via `HistoryRepository.SuppressMessage(messageId)`, which publishes
  `MessageSuppressed(topicId, messageId)` so the feed drops the row and `UnreadTracker`
  re-seeds. (This is how a correlated resolution already folds its problem away today.)

`SuppressToast` is **not** persisted — a toast is a one-time live event, so there is no
notion of retroactively (un)toasting a stored message. Phase 2's "apply" therefore operates
purely on the `Suppressed` axis plus incident folding; it never touches toasts.

## Architecture

### 1. Stable rule ids (model + format change)

Each rule gains an `id`: a short, stable token (e.g. an 8-char base32 of a GUID) generated
when a rule is first created and written into the pack JSON:

```jsonc
{ "type": "expect", "id": "k3p9x2qa", "enabled": true, "when": { … }, "every": "26h", … }
```

- **`PackParser`** reads `id` if present; **back-compat:** when a rule has no `id` (every
  existing Phase-1 pack), the parser synthesises the legacy `"{packName}#{index}"` value so
  state keyed under the old scheme is preserved on first load. The manager writes a real `id`
  the next time that pack is saved (a one-way, lazy migration — no separate migration step).
- The `Id` on `CorrelateRule` / `ExpectRule` (already used by `IncidentStore` /
  `ExpectationStore`) now comes from this field rather than the array index. **No store schema
  change** — the stores already key on an opaque string id; only the *source* of that string
  changes.
- `MatchRule` gains an `Id` too (for enable/disable bookkeeping and preview attribution),
  even though match rules are stateless in the engine.

### 2. `enabled` flag (model + format)

- `RulePack` gains `bool Enabled` (default `true`); each rule record
  (`MatchRule` / `CorrelateRule` / `ExpectRule`) gains `bool Enabled` (default `true`).
- `PackParser` reads `"enabled"` on the pack object and on each rule (absent ⇒ `true`).
- **The engine stays oblivious.** `PackStore.Packs` — the property `RuleEngine` and
  `ExpectationMonitor` consume — is filtered to *enabled packs, and within them, enabled rules
  only*. A disabled pack or rule is simply absent from the list the engine sees, so **no engine
  code changes**.
- The manager needs to see *everything* (including disabled), so `PackStore` exposes a separate
  editing view (below).

### 3. `PackWriter` (new — inverse of `PackParser`)

Serializes the editor model back to canonical pack JSON, including `id` and `enabled`. Built
**test-first** against a round-trip invariant: `Parse(Write(pack))` is structurally equal to
`pack` over the supported schema.

- The canonical output covers the full *currently-supported* schema: `match`
  (matcher + `suppressToast` / `tag:` actions), `correlate` (open/close/key), `expect`
  (when/every/grace/onAbsence/onRecovery).
- **Behaviourally-inert forward-compat fields and comments are not preserved.** Saving a pack
  through the manager rewrites canonical JSON, dropping any `digest` / `dismissOriginal` /
  `onClose` / jsonc comments the engine already ignores. This is an accepted trade for a
  structured editor (the engine's behaviour is unchanged because it never acted on them).

### 4. `PackStore` editing API (extends the existing store)

The current store loads `*.json` into `Packs` and has a `Save(name, json)` that always creates
a *new* uniquely-named file. Phase 2 adds:

- `IReadOnlyList<EditablePack> GetEditablePacks()` — every pack file with its **file path**,
  parsed content, and `enabled` flags (disabled ones included). This is the manager's source.
- `void Overwrite(string path, string json)` — rewrite a specific existing file in place, then
  `Reload()` (so edits take effect live, mirroring how the AI dialog's `Save` reloads).
- `void Delete(string path)` — delete a pack file, then `Reload()`.
- The existing `Save(name, json)` stays for **new** packs and the AI dialog (unique-filename
  create).

`Packs` (engine-facing) becomes the enabled-filtered projection described in §2. Renaming a
pack's display `Name` does **not** rename its file (avoids churn / breaking the path handle);
only the JSON `name` field changes.

### 5. Editor view-models (mutable; distinct from the immutable engine records)

The engine records are immutable and shared on the hot path; the editor needs observable,
mutable state, so it uses its own VMs that load from the parsed model and serialize via
`PackWriter`.

- `RulePackManagerViewModel` — the window VM: `ObservableCollection<PackVm>`, `SelectedPack`,
  `SelectedRule`; commands `NewBlankPack`, `NewPackWithAI`, `DeletePack`, `AddRule(type)`,
  `DeleteRule`, `Save`, `Cancel`, `Preview`, `Apply`.
- `PackVm` — `Name`, `Enabled`, `ObservableCollection<RuleVm>`, source file path, dirty flag.
- `RuleVm` (abstract base: `Id`, `Enabled`, `Kind`, one-line `Summary`) with
  `MatchRuleVm` / `CorrelateRuleVm` / `ExpectRuleVm`, all reusing a shared `MatcherVm`
  (topic, min-priority, title regex, body regex, tag).
- **Validation before save** (inline errors block Save): regexes compile, durations parse
  (`Duration.TryParse`), required fields present — `expect` needs `every` + an on-absence
  title; `correlate` needs a key regex. The editor never writes a pack the engine would choke
  on. (The engine's runtime fail-open is unchanged; this is belt-and-suspenders at author time.)

### 6. The window (`RulePackManagerWindow`, a `FluentWindow`)

Master–detail:

- **Left pane — pack list:** each row shows name · enable checkbox · rule count. Buttons:
  **New pack ▾** (Blank / Draft with AI…) and **Delete pack**.
- **Right pane — selected pack:** editable pack name, pack enable toggle, then a **rule list**
  (per rule: a type chip · enable checkbox · one-line summary via the existing
  `PackSummarizer` · delete), an **Add rule ▾** (Match / Correlate / Expect), and the
  **selected rule's form**:
  - *Match* — matcher fields + actions (suppress-toast checkbox; add-tag value). Only the two
    actions the engine actually supports today are offered (no dead `digest`/`dismissOriginal`).
  - *Correlate* — open matcher, close matcher, key (title/body dropdown + regex).
  - *Expect* — when-matcher, every + grace durations, on-absence (priority/title/message),
    optional on-recovery (priority/title/message).
- **Footer:** **Preview**, **Apply**, **Save**, **Cancel**.
  - *Save* writes changed pack files (`Overwrite` for existing, `Save` for new) and lets
    `PackStore.Reload()` make them live; *Cancel* discards in-memory edits.

`PackSummarizer` is reused for the rule summaries shown in the list (it already turns a parsed
pack into plain-English lines for the AI review step).

### 7. Preview & test against history (read-only)

A dry-run of the **selected pack** (its current in-editor state) so the user can validate
regexes/matchers before trusting them.

- **Scope picker:** a topic (or All topics) + a window (e.g. last *N* messages, or all stored
  history), reusing `HistoryRepository.Query(includeSuppressed: true)` so already-hidden rows
  are visible to the simulation.
- **Engine reuse:** instantiate a `RuleEngine` over an in-memory packs provider returning *just
  the selected pack* and a **throwaway in-memory `IIncidentStore`** (so preview never touches
  real incident state), then feed the scoped messages **in timestamp order** (correlation is
  stateful and order-dependent).
- **Output — a results table:** each message badged *Hidden* / *Tagged: X* / *Opens incident* /
  *Folds incident* / *No effect*, plus a summary line ("38 of 200 hidden, 4 incidents folded").
- **`expect` rules in preview:** rather than a per-message verdict, preview reports the
  **absence windows** it detects over the scoped timeline ("expected every 26h; a 50h gap on
  2026-06-10 would have alerted"), computed from message timestamps via the same overdue logic
  `ExpectationEvaluator` uses.
- **Zero writes** anywhere.

The preview computation is a pure function `(pack, messages) → results` so it is unit-testable
without the UI and is shared with Apply (Apply commits exactly what Preview computed for the
`match`/`correlate` rows).

### 8. Apply against history (additive backfill)

"Catch existing history up to this pack" — does to already-stored rows what the live engine
would have done had the pack existed when they arrived.

- Operates on the **selected pack** (matching the user's mental model: preview a pack, like it,
  apply *that* pack).
- Re-runs the pack's `match` + `correlate` rules over the chosen history scope in timestamp
  order, and **OR-s** the resulting hide/fold into the stored `Suppressed` flag — i.e. it only
  ever *adds* hiding, never clears it. This composes correctly with the live engine, which also
  ORs `HideFromFeed` at insert.
- Reuses `HistoryRepository.SuppressMessage` (which already publishes `MessageSuppressed`, so
  the feed drops rows and `UnreadTracker` re-seeds) for both folded resolutions and their
  problems. Correlate folding rebuilds against the **real** `IncidentStore` for the scope.
- **Toasts untouched. `expect` rules skipped** (raising historical absence alerts is
  meaningless; their only backward effect — seeding last-seen — the live monitor already does
  via backfill).
- **Additive, no auto-undo (accepted limitation):** the `Suppressed` bool has no per-rule
  provenance, so disabling/deleting a pack later does **not** retroactively un-hide rows a
  previous apply hid. Those rows stay hidden but remain visible via the existing
  **"Show suppressed"** feed toggle; future messages simply stop being hidden. A full
  reset/recompute that rebuilds all flags from the enabled ruleset is **deferred** to a later
  phase.
- UX: a confirmation before committing, showing the diff count ("this will hide 38 messages and
  fold 4 incidents — apply?"). Preview is the natural precursor (same scope/result), but Apply
  does not strictly require a preview first.

### 9. Entry point & DI

- Settings → Rules: replace the standalone "Draft rules with AI…" button with **"Manage rule
  packs…"** (opens `RulePackManagerWindow`). AI drafting is reachable from inside the manager.
- `RulesFeature.AddRules()` registers `PackWriter` (or it's static like `PackParser`),
  `RulePackManagerViewModel` (transient), and the window. The history-scoped preview/apply
  helper takes `HistoryRepository` + `IIncidentStore` + `RuleEngine` building blocks.

## Edge cases

- **A pack with zero enabled rules** (everything toggled off, or the pack itself disabled) is a
  no-op for the engine but still visible/editable in the manager.
- **Renaming a pack** changes only the JSON `name`; the file path and rule `id`s are stable, so
  heartbeat/incident state survives a rename.
- **First save of a legacy pack re-keys its state once.** A Phase-1 pack has no `id`s, so it
  loads with synthesised `"{packName}#{index}"` ids (preserving existing state). The first time
  it is saved through the manager, real stable `id`s are written, which changes the state key
  once — heartbeats re-arm and any in-flight incident pairings drop (self-healing, same minor
  effect as a hand-edit). After that first save, ids are stable forever and editing is
  state-safe.
- **Apply scope vs. retention:** Apply only sees rows still in history; retention-pruned
  messages are simply not there to hide (consistent — they're gone from the feed already).
- **Preview/Apply ordering:** both must process messages oldest-first so a `correlate` close
  can find its open. `HistoryRepository.Query` returns newest-first for the feed, so the
  preview/apply helper sorts ascending before simulating.
- **Concurrent live messages during Apply:** Apply is a short, synchronous backfill over stored
  rows; a message arriving mid-apply is handled by the live pipeline as usual. Worst case a row
  is evaluated by both — harmless because the operation is an idempotent OR into `Suppressed`.

## Testing

Deterministic, UI-free units carry the value:

- **`PackWriter` round-trip:** `Parse(Write(pack))` equals `pack` across all three rule types,
  including `id` and `enabled`; and legacy packs with no `id`/`enabled` parse with the
  synthesised id and `enabled = true`.
- **`PackStore` enabled-filtering:** a disabled pack and a disabled rule are absent from
  `Packs` (engine view) but present in `GetEditablePacks()`; `Overwrite`/`Delete` mutate the
  right file and reload.
- **Preview/apply helper:** the pure `(pack, messages) → results` function — match suppression,
  correlate open/close/fold over an ordered sequence, and `expect` absence-window detection —
  with a fake incident store; Apply ORs into `Suppressed` and leaves unrelated rows untouched.
- **Editor-VM validation:** bad regex / bad duration / missing required field blocks Save.
- The XAML window itself is not unit-tested (consistent with the rest of the app — manual
  verification in the running app).

## Non-goals (YAGNI)

- **Drag-reordering rules** within a pack (order doesn't affect the engine — match effects OR
  together, correlate/expect are independent).
- **A full reset/recompute** of suppressed state (deferred; see §8).
- **Import/export** of packs — that's the separate 0.9 roadmap item (which will define the
  "export disables everything" behaviour noted in scope decision §3).
- **Preserving non-canonical JSON** (comments, forward-compat fields) through an edit (§3).
- **Per-rule provenance** on the `Suppressed` flag (would enable per-pack un-apply; not worth
  the schema cost now).
- **A community pack library** (future, unchanged from Phase 1's build order).

## Build order (suggested for the implementation plan)

1. **Model + format:** add `Id` + `Enabled` to the records; `PackParser` reads them with
   back-compat id synthesis; `PackStore.Packs` enabled-filtering; `GetEditablePacks` /
   `Overwrite` / `Delete`. (Engine untouched; covered by store/parser tests.)
2. **`PackWriter`** (test-first round-trip).
3. **Preview/apply helper** (pure function + the `Apply` write path; test-first).
4. **Editor VMs** (`PackVm` / `RuleVm` subtypes / `MatcherVm`) + validation (test-first where
   pure).
5. **The window** (`RulePackManagerWindow` + master–detail XAML, forms, preview/apply UI) and
   the Settings → Rules entry-point swap; integrate the existing `DraftRulesDialog` as a
   creation path.
