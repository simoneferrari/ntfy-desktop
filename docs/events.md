# Event catalog

Implementation-ready reference for the app's event model: every event, its
payload, **how it is raised**, and **how each consumer reacts**. Derived from a
per-surface audit of FeedPage, the rail/NavigationView, ConnectionsPage, the
title-bar chrome, and the tray.

## Transport

Events flow through the local event bus in `Core/Messaging` (`EventBus`), which
replaced FastEndpoints' in-process events.

- **UI consumers** (rail/`MainWindow`, `FeedViewModel`, `ConnectionsViewModel`,
  `MainWindowViewModel`) subscribe via
  `EventBus.Subscribe<TEvent>(recipient, handler, ThreadOption.UIThread)`.
  The bus marshals to the UI thread via `IUIDispatcher`, so consumers do **not**
  wrap their bodies in `Application.Current.Dispatcher.Invoke(...)`.
- **Service consumers** (`ConnectionManager`) stay as DI-resolved
  `IEventHandler<TEvent>` classes on `ThreadOption.PublisherThread` and manage
  their own concurrency (`SemaphoreSlim` around `_connections`).
- Use the weak-recipient `Subscribe` overload for view models so there are no
  unsubscribe leaks against singleton publishers.

### Raising mechanism — Bus vs CLR

**Rule: every event in this catalog is a bus event (`IEvent`)**, raised with the
extension `new SomeEvent(...).PublishAsync(mode)`. There is one mechanism so that
every UI consumer gets UI-thread marshaling and weak subscription uniformly.

The only events that stay plain CLR `EventHandler`s are **component-internal
signals not in this catalog** — e.g. `TopicConnection.StateChanged` →
`ConnectionManager`. The manager consumes that internally and re-publishes the
bus events (`TopicConnectionStatusChanged`, aggregate `ConnectionStatusChanged`).

Several events exist today as CLR `EventHandler`s and are tagged **migrate**
below — converting them to bus events is part of the work.

**Publish mode:** the extension defaults to `PublishMode.WaitForNone`
(fire-and-forget; handler faults are logged by the bus). That is the intended
default — publishers don't block on consumers, and connection outcomes surface
back as their own events. Use `WaitForAll` only where a publisher genuinely needs
handler completion.

### Mechanism summary

| Event | Mechanism | Publisher | Raise call |
|---|---|---|---|
| `TopicAdded` | Bus | `TopicManager` | `new TopicAdded(t).PublishAsync()` |
| `TopicUpdated` | Bus | `TopicManager` | `new TopicUpdated(t).PublishAsync()` |
| `TopicDeleted` | Bus | `TopicManager` | `new TopicDeleted(id).PublishAsync()` |
| `TopicMoved` | Bus · **new** | `TopicArrangement` | `new TopicMoved(id).PublishAsync()` |
| `GroupMoved` | Bus · **new** | `TopicArrangement` | `new GroupMoved(name).PublishAsync()` |
| `ConnectionStatusChanged` | Bus · **migrate** (CLR today) | `ConnectionManager` | `new ConnectionStatusChanged().PublishAsync()` |
| `TopicConnectionStatusChanged` | Bus · **new** | `ConnectionManager` | `new TopicConnectionStatusChanged(id, status, err).PublishAsync()` |
| `NotificationsStatusChanged` | Bus · **migrate** (CLR today) | `NotificationGate` | `new NotificationsStatusChanged().PublishAsync()` |
| `TopicNotificationsStatusChanged` | Bus · **migrate + modify** (CLR today) | `NotificationGate` | `new TopicNotificationsStatusChanged(id, isPaused).PublishAsync()` |
| `ServerDisplayChanged` | Bus · **new** (from `DisplayChanged`) | settings/server logic | `new ServerDisplayChanged().PublishAsync()` |
| `MessageInserted` | Bus · **migrate** (CLR today) | `HistoryRepository` | `new MessageInserted(m).PublishAsync()` |
| `MessagesDeleted` | Bus · **new** | `HistoryRepository` | `new MessagesDeleted(topicId).PublishAsync()` |
| `UnreadCountChanged` | Bus · **migrate** (CLR `Changed` today) | `UnreadTracker` | `new UnreadCountChanged().PublishAsync()` |

> `AppSettings.DisplayChanged` (CLR) is **removed** — replaced by
> `ServerDisplayChanged` + `TopicMoved`/`GroupMoved`.
> `ConnectionManager.ConnectionsChanged` (CLR) is **deleted** — no consumers.

## Design principles

Three tiers, each with its own update strategy:

1. **Structural / display** (low-frequency, user-initiated): topic lifecycle,
   `TopicMoved`, `GroupMoved`, `ServerDisplayChanged`. **Targeted, single-item**
   mutations — never a full rebuild. A rebuild also discards rail selection,
   folder expansion, scroll position, and re-creates badge adorners (flicker),
   so incremental wins on correctness, not just cost.
2. **Status / count** (high-frequency, socket-driven): `TopicConnectionStatusChanged`,
   `TopicNotificationsStatusChanged`, `UnreadCountChanged`. **O(1) targeted** updates — never a
   rebuild, never `GetTopicStates()`.
3. **Aggregate** (app-wide): `ConnectionStatusChanged`, `NotificationsStatusChanged`.
   Coarse parameterless event + a cheap in-memory recompute.

---

## Events

Conventions: **UI consumers** subscribe `ThreadOption.UIThread`; **`ConnectionManager`**
is a DI handler on `PublisherThread`. "Query" notes work beyond the payload.

### `TopicAdded(TopicSettings Topic)`
**Raised:** Bus · `new TopicAdded(topic).PublishAsync()` · publisher `TopicManager.AddOrUpdateAsync` (new topic)

- **ConnectionManager** — if `Topic.Enabled`, `AddConnection(Topic)`.
- **Rail** — build one `RailItem`, insert at its arrangement index (create the
  group folder if it's the group's first topic). No rebuild.
- **ConnectionsPage** — insert one row at its position; recompute `showServer`
  (rebuild rows only if it flipped).
- **Feed** — no-op.

### `TopicUpdated(TopicSettings Topic)`
**Raised:** Bus · `new TopicUpdated(topic).PublishAsync()` · publisher `AddOrUpdateAsync` (edit), `ToggleEnabledAsync`

- **ConnectionManager** — enabled → (add if missing / rebuild if
  `!MatchesTopicSettings` / leave); disabled → remove. *(existing handler)*
- **Rail** — update that item's label, opacity (`Enabled`), subtitle; if
  section/group changed, relocate it. No rebuild.
- **ConnectionsPage** — replace that row's `DisplayName`/`ServerName`; recompute
  `showServer`. Status is unaffected (arrives via the status event).
- **Feed** — re-enrich rows where `TopicId == Topic.Id`; if
  `Topic.Id == CurrentTopicId` → refresh `Title`/`Subtitle` + recompute
  `ShowReconnectButton` (now incl. `Enabled`). Query: none.

### `TopicDeleted(Guid TopicId)`
**Raised:** Bus · `new TopicDeleted(id).PublishAsync()` · publisher `TopicManager.RemoveAsync`

- **ConnectionManager** — `RemoveConnectionAsync(TopicId)` (also raises aggregate
  `ConnectionStatusChanged`).
- **Rail** — remove that item from `_railItems` + its container; drop the folder +
  badge if now empty. No rebuild.
- **MainWindow (nav)** — if `_feedVm.CurrentTopicId == TopicId` → navigate to
  `FeedPage`, set `CurrentTopicId = null`.
- **ConnectionsPage** — remove that row; recompute `showServer`.
- **Feed** — no-op (navigation + `MessagesDeleted` cover content).

### `TopicMoved(Guid TopicId)`
**Raised:** Bus · `new TopicMoved(id).PublishAsync()` · publisher `TopicArrangement.MoveTopicWithinGroup` / `MoveTopicToGroup` / `MoveTopicRelativeTo`

- **Rail** — reposition that single item: move within its container if the
  section is unchanged, else relocate (creating/dropping folders as needed).
  No rebuild.
- No other consumer. (ConnectionsPage order is not reflected live today.)

### `GroupMoved(string GroupName)`
**Raised:** Bus · `new GroupMoved(name).PublishAsync()` · publisher `TopicArrangement.MoveGroup` / `MoveGroupRelativeTo`

- **Rail** — move that one folder element to its new index (from
  `OrderedGroupNames`). No rebuild.
- No other consumer.

> Group expand/collapse raises **no event** — the `NavigationViewItem` owns its
> `IsExpanded` visual and `SetGroupCollapsed` only persists. With rebuilds gone,
> collapse state survives naturally.

### `ConnectionStatusChanged()` *(aggregate)*
**Raised:** Bus · **migrate from CLR** · `new ConnectionStatusChanged().PublishAsync()` · publisher `ConnectionManager` (socket transitions + public `AddConnection` / `RemoveConnectionAsync`)

- **Title-bar pip** — re-query `GetConnectionStatus()` → `ConnectionStatus` + text.
- **Tray** — `SetConnectionStatus(GetConnectionStatus())` → icon color/tooltip.
  *(add dedupe)*

### `TopicConnectionStatusChanged(Guid TopicId, TopicConnectionStatus Status, string? LastError)`
**Raised:** Bus · **new** · `new TopicConnectionStatusChanged(id, status, err).PublishAsync()` · publisher `ConnectionManager.OnTopicConnectionStatusChanged`

- **Rail** — `_railItems[TopicId].Pip.Fill = PipBrushFor(Status)`. O(1), no query.
- **ConnectionsPage** — `Rows[i] = row.WithStatus(Status, LastError)`; recompute
  `CanDisconnectAll`. O(1), no query.
- **Feed** — if `TopicId == CurrentTopicId` → recompute `ShowReconnectButton` from
  `Status` + pause lookup. O(1).

### `NotificationsStatusChanged()`
**Raised:** Bus · **migrate from CLR** · `new NotificationsStatusChanged().PublishAsync()` · publisher `NotificationGate.PauseAll` / `ResumeAll`

- **Title-bar** — `IsGloballyPaused` → button label/visibility.
- **Tray** — `SetNotificationStatus(gate.GlobalStatus)` → tooltip + pause-menu
  label. *(add dedupe)*
- **Rail** — recompute every pause glyph (`global || per-topic`), visibility only.
- **Feed** — if `CurrentTopicId` set → recompute `ShowReconnectButton`.

### `TopicNotificationsStatusChanged(Guid TopicId, bool IsPaused)`
**Raised:** Bus · **migrate from CLR + add `bool`** · `new TopicNotificationsStatusChanged(id, isPaused).PublishAsync()` · publisher `NotificationGate.PauseTopic` / `ResumeTopic`

- **Rail** — `_railItems[TopicId].PauseGlyph.Visibility =
  (IsPaused || gate.IsGloballyPaused)`. O(1).
- **Feed** — if `TopicId == CurrentTopicId` → recompute `ShowReconnectButton`.
  Query: `GetTopicStatus(TopicId)` for the connection axis.

### `ServerDisplayChanged()`
**Raised:** Bus · **new** (split from `DisplayChanged`) · `new ServerDisplayChanged().PublishAsync()` · publisher server rename / `ShowServerLabel` toggle / server add-remove

- **Rail** — update each item's subtitle text + `showServer` gate in place.
  (Legitimately all-items, but low-frequency; no rebuild.)
- **ConnectionsPage** — recompute `ServerName` + `showServer` across rows.
- **Feed** — re-enrich `ServerName` on all rows in place (All-topics view).

### `MessageInserted(HistoryMessage Message)`
**Raised:** Bus · **migrate from CLR** · `new MessageInserted(m).PublishAsync()` · publisher `HistoryRepository.Insert`

- **Feed** — filter (topic/priority/search); enrich; insert at 0; trim to
  `MAX_DISPLAYED`; `IsEmpty = false`. O(1).

### `MessagesDeleted(Guid? TopicId)`
**Raised:** Bus · **new** · `new MessagesDeleted(topicId).PublishAsync()` · publisher `HistoryRepository` (`DeleteByTopicId` → value, `DeleteAll` → null)

- **Feed** — `null` → clear `Messages`; value → remove rows with that `TopicId`
  (All-topics view); recompute `IsEmpty`. Lets `Clear` drop its manual
  `Messages.Clear()`.

### `UnreadCountChanged()`  *(optionally `Guid TopicId`)*
**Raised:** Bus · **migrate from CLR `Changed`** · `new UnreadCountChanged().PublishAsync()` · publisher `UnreadTracker`

- **Rail** — `RefreshBadges()` (or, if id carried: that topic's badge + its folder
  sum + total).
- **Tray** — `SetUnreadCount(unread.Total)`. *(already deduped)*

---

## Consolidated prerequisites & new APIs

**New / changed queries**
- `ConnectionManager.GetTopicStatus(Guid)` → single topic status (feed's
  pause/global recompute paths).

**Publisher-side changes**
- `ConnectionManager.OnTopicConnectionStatusChanged`: publish
  `TopicConnectionStatusChanged(id, status, conn.LastError)` **and** aggregate
  `ConnectionStatusChanged`.
- Public `AddConnection` / `RemoveConnectionAsync` publish aggregate
  `ConnectionStatusChanged` (so removing the last topic flips the pip/tray to
  Disconnected).
- `TopicArrangement`: replace `RaiseDisplayChanged()` with `TopicMoved` /
  `GroupMoved` (or return a result — caller publishes).
- **Delete `ConnectionsChanged`** and **remove `DisplayChanged`**.

**Consumer-side refactors**
- `RailItem` → add label + subtitle `TextBlock` refs + current-group; add rail
  single-item helpers (`InsertTopicItem`, `RemoveTopicItem`,
  `RepositionTopicItem`, `RepositionFolder`, section→`MenuItems` index mapper);
  demote `RebuildTopicItems` to first-load init only.
- `HistoryMessage` → observable display fields (`TopicLabel`, `ServerName`,
  `ShowTopic`) for in-place re-enrich.
- `TopicConnectionRow.WithStatus(status, error)` copy helper (row stays
  immutable; single-item replace).
- Tray: dedupe `SetConnectionStatus` / `SetNotificationStatus` (match
  `SetUnreadCount`).
- Remove per-handler `Dispatcher.Invoke` in VMs (bus handles via
  `ThreadOption.UIThread`).

## Consumer × event matrix

| Event | ConnMgr | Rail | ConnectionsPage | Feed | Title-bar | Tray |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| `TopicAdded` | ● | ● | ● | | | |
| `TopicUpdated` | ● | ● | ● | ● | | |
| `TopicDeleted` | ● | ● (+nav) | ● | | | |
| `TopicMoved` | | ● | | | | |
| `GroupMoved` | | ● | | | | |
| `ConnectionStatusChanged` | | | | | ● | ● |
| `TopicConnectionStatusChanged` | | ● | ● | ● | | |
| `NotificationsStatusChanged` | | ● | | ● | ● | ● |
| `TopicNotificationsStatusChanged` | | ● | | ● | | |
| `ServerDisplayChanged` | | ● | ● | ● | | |
| `MessageInserted` | | | | ● | | |
| `MessagesDeleted` | | | | ● | | |
| `UnreadCountChanged` | | ● | | | | ● |

---

## Implementation sequence

Ordered to keep the app compiling/working between steps. Steps 1–2 are
foundational; 3–7 are publisher slices; 8–11 are consumer prereqs; 12–15 rewire
consumers; 16 is cleanup. `ConnectionsChanged`/`DisplayChanged` are deleted last
(step 16), once no consumer references them.

1. **ConnectionManager concurrency + de-storm** *(no event-model change yet)*
   - `ConcurrentDictionary` for `_connections` (safe UI-thread reads) **+**
     `SemaphoreSlim(1,1)` serializing the compound mutations.
   - Internal (silent) vs public (event-raising) `Add`/`Remove`/`Rebuild`
     primitives so a batch raises once; composites use the silent ones.
   - Add `GetTopicStatus(Guid)`.
   - Public `Add`/`RemoveConnectionAsync` raise aggregate status.

2. **Define/modify event types** (bus `IEvent`s): `TopicMoved`, `GroupMoved`,
   `TopicConnectionStatusChanged(id, status, err)`, `ServerDisplayChanged`,
   `MessagesDeleted(Guid?)`, `UnreadCountChanged`; add `bool IsPaused` to
   `TopicNotificationsStatusChanged`. (Pure type additions — nothing consumes them yet.)

3. **Migrate existing CLR events to bus**: `ConnectionStatusChanged`,
   `NotificationsStatusChanged`, `TopicNotificationsStatusChanged`, `MessageInserted`, `Changed →
   UnreadCountChanged`. Keep old CLR events in parallel temporarily if it eases the
   cutover, or migrate publisher+consumers per event.

4. **ConnectionManager publishers**: `OnTopicConnectionStatusChanged` publishes
   granular + aggregate.

5. **TopicArrangement publishers**: `TopicMoved` / `GroupMoved` in place of
   `RaiseDisplayChanged`.

6. **HistoryRepository publishers**: `MessagesDeleted` from `DeleteByTopicId` /
   `DeleteAll`.

7. **Settings/server publishers**: `ServerDisplayChanged` for rename /
   label-toggle / server count change.

8. **`HistoryMessage`** → observable display fields.

9. **`TopicConnectionRow.WithStatus(status, error)`** copy helper.

10. **`RailItem` extension + rail single-item helpers** (insert/remove/reposition,
    folder lifecycle, section→`MenuItems` index mapper). Build & unit-test the
    index mapper in isolation first.

11. **Tray dedupe** in `SetConnectionStatus` / `SetNotificationStatus`.

12. **Feed** → subscribe bus events (`ThreadOption.UIThread`); targeted
    re-enrich/insert/prune; reconnect-button recompute; drop full reloads,
    `ConnectionsChanged`, `DisplayChanged`; remove `Dispatcher.Invoke`.

13. **Rail / MainWindow** → subscribe lifecycle + `TopicMoved`/`GroupMoved` +
    status/pause/server-display + `UnreadCountChanged`; incremental updates;
    `TopicDeleted` navigation; drop `ConnectionsChanged` + coarse `DisplayChanged`.

14. **ConnectionsPage** → subscribe; single-row replace on status; structural
    recompute (`showServer`); drop `ConnectionsChanged` + aggregate
    `ConnectionStatusChanged`.

15. **Title-bar VM + tray (`App.xaml.cs`)** → subscribe to bus aggregate events;
    remove CLR subscriptions + `Dispatcher.Invoke`.

16. **Cleanup**: delete `ConnectionsChanged` and `DisplayChanged`; remove
    `RefreshTopicAdornments` from event paths (rebuild-time init only); delete any
    now-unused CLR event plumbing. Manual pass per surface to confirm no full
    rebuilds on status flaps.

---

## Final implementation notes (as built)

Deltas from the catalog above, reflecting the shipped code:

- **Event names** settled as: `NotificationsStatusChanged` (global pause),
  `TopicNotificationsStatusChanged(Guid TopicId, bool IsPaused)` (per-topic pause),
  `UnreadCountChanged(Guid? TopicId)`, `ServerDisplayChanged`. (Renamed throughout
  this doc.)

- **`MessagesDeleted` carries a source:**
  `MessagesDeleted(Guid? TopicId, MessageDeletionSource Source)` where
  `MessageDeletionSource ∈ { Feed, Removal, Retention }`. The **feed removes its own
  rows locally** (Clear / delete-message) and **ignores** `Source == Feed` echoes —
  so, contrary to the earlier "Clear relies on the event" note, Clear keeps its local
  `Messages.Clear()`. `UnreadTracker` re-seeds on any `MessagesDeleted` regardless of
  source.

- **`ServerDeleted(Guid ServerId, IReadOnlyList<Guid> RemovedTopicIds)`** — **new**,
  published by `AppSettings.RemoveServer` (which captures the cascade-removed topic
  ids before removing them). Server removal does **not** emit per-topic `TopicDeleted`
  or `ServerDisplayChanged`. Consumers: rail + ConnectionsPage rebuild; MainWindow
  navigates to All-topics if the current topic was removed. Connections are torn down
  by `SettingsViewModel`'s existing `ApplySettingsAsync()` call.

- **Rail reposition preserves the `NavigationViewItem` instance.** `TopicMoved` /
  `TopicUpdated` move + update the existing item in place rather than rebuilding it —
  replacing the instance breaks WPF-UI `NavigationView` selection for that item (it
  keeps tracking the removed instance). `RebuildTopicItems` is first-load + the
  `ServerDisplayChanged`/`ServerDeleted` rare-rebuild path only.

- **`UnreadTracker` dropped its `ConnectionManager` dependency** — it reacts to
  `MessageInserted` + `MessagesDeleted` only (topic-set changes that affect counts
  always go through a delete).

- **Transport:** FastEndpoints was removed entirely; all events use `Core/Messaging`
  (`IEvent` + `EventBus`). UI consumers subscribe with `ThreadOption.UIThread`.
