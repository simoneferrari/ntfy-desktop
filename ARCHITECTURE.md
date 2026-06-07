# Architecture

## Stack

| Layer | Choice |
|---|---|
| Runtime | .NET 10, TFM `net10.0-windows10.0.17763.0` |
| UI | WPF + WPF-UI 4.3.0 (FluentWindow, NavigationView, Mica) |
| MVVM | CommunityToolkit.Mvvm 8.4.0 — `[ObservableProperty]`, `[RelayCommand]`, `[NotifyPropertyChangedFor]` |
| DI / hosting | Microsoft.Extensions.Hosting (Generic Host); singletons for VMs, `BackgroundService` for hosted work |
| Tray | H.NotifyIcon.Wpf 2.4.1 |
| Persistence | Microsoft.Data.Sqlite (history) + System.Text.Json (settings) |
| Security | DPAPI (`ProtectedData`, `CurrentUser`) for access token at rest |
| Messaging | In-process event bus (`Core/Messaging`: `IEvent` + `EventBus`, CommunityToolkit `WeakReferenceMessenger` under the hood). UI subscribers marshal via `ThreadOption.UIThread`. See `docs/events.md`. |

## Layout

Everything lives under `Features/<Name>/`, each folder registering its own services via an `Add<Name>()` DI extension (`*Feature.cs`).  
`Domain/` holds types shared across features.

```
NtfyDesktop/
  App.xaml.cs                   — host build, DataPath, tray wiring
  Domain/
    NtfyMessage.cs, Priority.cs — cross-feature types
  Features/
    AppCompositionExtensions.cs — composes all features into DI
    Connections/                — WebSocket subscriptions, connection health
    Notifications/              — pause gate, toast delivery
    Topics/                     — topic configuration (CRUD)
    Settings/                   — app-wide settings (server URL, token, defaults)
    History/                    — SQLite message store
    Feed/                       — in-app message list
    Unread/                     — unread-count tracking + rail badges
    Shell/                      — main window, nav rail, tray icon
```

### Connections

`ConnectionManager` owns one `TopicConnection` (WebSocket) per enabled topic.

- `ApplySettingsAsync` — idempotent; adds/removes sockets to match current settings.
- `RestartAllAsync` — hard teardown + re-apply (used when server URL or token changes).
- `GetConnectionStatus()` → `ConnectionStatus { Connected, Degraded, Disconnected }` — pure socket health, no concept of pause.
- `GetTopicStates()` → `IReadOnlyList<TopicConnectionState>` — per-socket status snapshot.
- Publishes `ConnectionStatusChanged` (aggregate) and `TopicConnectionStatusChanged(id, status, error)` (per topic) on the bus; `GetTopicConnectionStatus(id)` exposes a single topic's status. Mutations are serialised (`SemaphoreSlim` + `ConcurrentDictionary`).
- `TopicConnection` refuses to send the bearer header over `ws://` (cleartext).
- **Catch-up on missed messages:** every connect attempt resumes with ntfy's `?since=<unix-timestamp>` (`HistoryRepository.GetSinceValue`, re-read per attempt via a `Func<string?>`). The server replays cached messages from that time, then streams live on the same socket — so both an app-restart gap and any mid-session reconnect gap backfill automatically. On subscribe, `ConnectionManager` calls `HistoryRepository.EnsureCursor(topicId, now)` to **prime a baseline** for a topic that has none — its first connect pulls no backlog (never `since=all`, which would dump the whole server cache), but it's no longer cursorless, so every gap *after* that first subscription is caught. (Without this a topic that only ever receives messages while the app is closed would stay cursorless forever and never catch up.) Replayed messages are **tagged `IsBackfill`**: ntfy sends no live/replay delimiter, so `TopicConnection` bounds it by time — a message whose `time` predates the attempt's connect instant (captured per attempt) is backlog. Backfill messages still flow to history/feed/unread; instead of one toast each, `ShowToastNotification` records them with `BackfillSummaryNotifier`, which debounces the burst (ntfy sends no end-of-backlog marker) and fires a single **"N messages while you were away"** summary toast — so a reconnect never re-toasts each already-missed message. Only messages that would have notified live are counted (the same pause/priority/active-hours gate is applied first).
- **`since` is a timestamp, not a message id, on purpose.** `since=<id>` only works while that id is still in the server cache (ntfy default ~12h); once it ages out, ntfy can't locate the id and returns **nothing** — so a topic quiet longer than the cache window would never catch up (even brand-new messages wouldn't replay). `since=<timestamp>` degrades gracefully: a stale value just returns whatever's still cached after it. A timestamp `since` is *inclusive* of its boundary second, so it re-delivers already-stored messages — `HistoryRepository.Insert` therefore returns whether the row was genuinely new (`INSERT OR IGNORE` affected a row), and **both** downstream fan-outs are gated on it: `Insert` publishes `MessageInserted` only when new, and `ConnectionManager` publishes `NtfyMessageReceived` only when new. Otherwise the re-delivered boundary message would duplicate a feed row / unread bump and inflate the catch-up summary (neither the feed nor `UnreadTracker` dedupes on message id).
- **The cursor is stored separately from message history** (`topic_cursor` table: `topic_id`, `message_id`, `time`), advanced forward-only on every `Insert` and seeded once from existing history on table creation. It is deliberately *not* derived from `messages`: retention sweeps and user deletes only touch `messages`, so the cursor can't rewind and make the server replay (resurrect) deleted/pruned messages on reconnect. (`DevRewindCursors`, Debug-only, deliberately rewinds it to re-test catch-up.)

### Notifications

`NotificationGate` (singleton) owns all pause read/write.

- Global pause: `PauseAll()` / `ResumeAll()` / `IsGloballyPaused`.
- Per-topic pause: `PauseTopic(name)` / `ResumeTopic(name)` / `IsTopicPaused(name)`.
- Writes through to `AppSettings.IsPaused` and `TopicSettings.IsPaused`.
- Publishes `NotificationsStatusChanged` (global) and `TopicNotificationsStatusChanged(id, isPaused)` (per topic) on the bus.

`ShowToastNotification` handles `NtfyMessageReceived` and gates delivery through `NotificationGate`. If the gate says suppressed, the message is still written to history — only the toast is dropped.

### Connection ↔ Notification separation

These are intentionally independent axes. Key invariants:

- `ConnectionManager` has no knowledge of pause; it never calls into `NotificationGate`.
- `NotificationGate` has no knowledge of sockets.
- Pause does **not** stop WebSocket connections — sockets stay live, messages keep arriving, only toast delivery is suppressed.
- UI composes both axes: connection pip (green/amber/red) + separate pause badge/button.
- `TopicConnectionState` has no `IsPaused` field. Pause is queried separately via the gate.
- Zero topics → `ConnectionStatus.Disconnected` (not `Degraded`).

### Topics

`TopicManager` coordinates topic lifecycle — `AddOrUpdate`, `Remove(topic, deleteHistory)`, `ToggleEnabled` — persisting to `AppSettings` and publishing `TopicAdded` / `TopicUpdated` / `TopicDeleted` on the event bus (the connection side reacts to those; see `docs/events.md`). Topic CRUD is surfaced in the nav rail (not on a dedicated settings page). Removal prompts the user to keep the topic's history (still browsable under "All topics") or delete it, mirroring server removal. `TopicSettings.GroupName` (nullable) assigns a topic to a rail folder, set via an editable combo in the topic editor.

Ordering is manual and lives in `TopicArrangement`: the `AppSettings.Topics` list order is the source of truth for topic order *within a section* (a group, or the ungrouped set), and `AppSettings.GroupOrder` for the folder order. A one-time `Migrate()` seed (gated by `OrderInitialized`) sorts both alphabetically so the first launch matches the old alphabetical rail. `TopicArrangement` exposes `MoveTopicWithinGroup`/`MoveTopicToGroup`/`MoveGroup` (+ `Can…` guards), drag-drop placement (`MoveTopicRelativeTo` / `MoveGroupRelativeTo`), and `SyncGroupOrder` (reconciles `GroupOrder` with groups actually in use). Reorders persist and publish `TopicMoved` / `GroupMoved`; the rail applies them by repositioning the single affected item/folder in place (no full rebuild). Surfaced two ways: right-click menus (topic: Move up/down, Move to group; folder: Move up/down) and in-rail drag-and-drop — each rail item is both a drag source (threshold-gated `DoDragDrop` so clicks still select) and a drop target, with the payload being the topic id (`Guid`) or group name (`string`). Indicators are adorners — an insertion line for reordering, a highlight for "drop into group" / dropping onto "All topics" to ungroup. Folder before/after is decided by `GroupOrder` position, not cursor pixels (an expanded folder's height includes its children).

### Settings

`AppSettings` owns JSON serialisation and DPAPI-wrapped token storage (`GetAccessToken` / `SetAccessToken`). The `SettingsViewModel` uses snapshot-based dirty tracking: a `FormSnapshot` is taken on `Load()`; `IsDirty` is recomputed on every property change by comparing against the snapshot.

### History

`HistoryRepository` wraps SQLite. `HistoryRetentionService` (BackgroundService) sweeps old rows hourly. The database is **not** encrypted (acknowledged tech debt). A second table, `topic_cursor`, holds the per-topic ntfy `since=` catch-up cursor (a Unix timestamp) — separate from `messages` precisely so retention/deletes don't rewind it (see Connections → Catch-up). The `messages.read` column (0/1) backs unread tracking; on first add it marks all existing rows read so upgraders don't see a badge flood. The `messages.attachment` column stores the raw ntfy `attachment` JSON object (`{ name, type, size, expires, url }`) as a single forward-compatible blob, added via the `EnsureColumn` migration helper (see Feed → Attachments). The repository publishes `MessageInserted` (always) and `MessagesDeleted(topicId, source, attachmentUrls)` (after any delete — `source` ∈ {Feed, Removal, Retention} lets a consumer ignore deletes it originated; `attachmentUrls`, gathered just before the rows go, lets the attachment cache drop their files) on the bus, so consumers stay in sync without coupling to each caller.

### Feed

`FeedViewModel` backs both the per-topic and combined **All topics** message lists; `FeedPage` renders them in a virtualizing (recycling) `ListBox`.

- **Attachments.** A message's image attachment is rendered inline; a non-image attachment shows a compact link chip. Image-ness is by MIME `type`, falling back to the file extension of the name/URL when ntfy omits `type` (it does for an external `Attach:` URL). Images are fetched by `AttachmentImageService` (singleton), triggered from `FeedViewModel.EnsureAttachmentLoaded` as messages enter the feed (initial load + live insert) rather than from a XAML event — the virtualizing `ListBox` recycles containers without re-raising `Loaded`, which made an element-level trigger unreliable. `EnsureAttachmentLoaded` is idempotent (per-message guard) and the service caps concurrent downloads (`SemaphoreSlim`) so a feed full of images doesn't stampede the server. The service caches twice — decoded, downscaled (`DecodePixelWidth`), frozen `BitmapImage`s in memory, and the raw bytes on disk under `App.DataPath\attachments\` keyed by a SHA-256 of the URL — so images survive scrolling, restart, and the ntfy server's short attachment retention. `AttachmentCacheSweepService` (BackgroundService, in the Feed feature so History needn't depend on Feed) ages out stale cache files hourly using the same retention window as message history. Downloads attach the server's bearer token **only** for an attachment URL that is same-origin (scheme+host+port) with the topic's configured server and is https — a publisher can't point `attachment.url` at their own host to harvest the token, and it's never sent over cleartext. Clicking any attachment (inline image or file chip) downloads it via `EnsureFileAsync` to the cache *with its real extension* (derived from filename → URL path → MIME type) and opens the local file with the OS default app — so server-hosted files authenticate (a browser couldn't) and Windows picks the right handler instead of prompting. Executable/script extensions are never shell-opened (they'd run); those, and any download failure, fall back to opening the URL through `SafeUrl`.

- **Action buttons.** A message's ntfy `actions` (up to three) render as a button row, parsed and persisted exactly like attachments — a single forward-compatible `actions` JSON column on `messages` (via `EnsureColumn`), carried on `NtfyMessage.Actions` / `HistoryMessage.Actions`. All execution funnels through `MessageActionInvoker` (singleton) so the safety rules live in one place: `view` opens the URL through `SafeUrl` (scheme allow-list), `copy` writes the value to the clipboard, and `http` — a publisher-controlled request — shows a confirmation dialog with the method + URL and only fires on approval, **never attaching the server bearer token** (the URL is arbitrary; injecting the token would leak it). `broadcast` is Android-only and anything unsupported renders as a disabled button with an explanatory tooltip. Toast action buttons are not implemented (feed only); the protocol round-trip needed to confirm an `http` action launched from a toast is deferred.

- **Auto-download (opt-in).** ntfy servers keep attachments only briefly, so on-demand fetching can miss the window. When `AppSettings.AutoDownloadAttachments` is on, `AttachmentPrefetchHandler` (`IEventHandler<MessageInserted>`, auto-registered, fires for every new stored message regardless of the open feed) prefetches each attachment to the cache on arrival, skipping ones whose server-reported size exceeds `AutoDownloadMaxFileMb` (unknown sizes are bounded by the same cap during download). Off by default. Both prefetched and on-demand files share one disk cache governed by a single budget: `EnforceQuota` keeps the cache dir under `AttachmentCacheMaxMb` by evicting least-recently-used files (oldest modified time; cache hits `Touch` the file so in-use files stay fresh), exempting the just-written file so opening a large attachment can't delete it before the OS reads it. The service's hard `MaxBytes` (25 MB) caps any single download; larger files fall back to the browser. When messages are deleted, `AttachmentCacheCleanupHandler` (`IEventHandler<MessagesDeleted>`) drops the removed rows' cached files via `RemoveFromCache` (matched by URL hash) so a deleted message doesn't leave attachments behind.

### Unread

`UnreadTracker` (singleton) owns unread counts surfaced as rail badges. It caches per-topic counts in memory, updating incrementally on the bus `MessageInserted` event and re-seeding (`GetUnreadCounts`) on `MessagesDeleted`; it publishes `UnreadCountChanged` for the rail badges and tray. (It no longer depends on `ConnectionManager` — topic-set changes that affect counts always go through a delete.) A topic is marked read only in the context of **its own** feed — navigating to that topic's feed, a message arriving for it while that feed is active and focused, or the window regaining focus while that feed is active (`SetActiveView` / `SetWindowActive` / `MarksRead`, all keyed on a concrete `TopicId`). The combined **All topics** feed is a passive overview and marks nothing read: otherwise opening the app — which lands on All topics — would wipe every unread badge and hide which messages were missed during catch-up. Bulk clearing is therefore explicit: a per-topic **Mark as read** (rail context menu) and **Mark all read** (the All-topics item's context menu), calling `MarkTopicRead` / `MarkAllRead`. The badge itself is a `BadgeAdorner` overlaid on each `NavigationViewItem`'s icon — the Icon slot only accepts an `IconElement`, so an adorner is the only way to sit a count bubble on top of the glyph. `MainWindow.xaml` wraps its content in an `AdornerDecorator` to guarantee an adorner layer.

### Shell

`MainWindow` is a three-row grid: TitleBar · caution strip (visible only when paused) · NavigationView. The nav rail has:

- "All topics" → FeedPage
- "Add topic" action item (opens `TopicEditorDialog`; does not navigate)
- Dynamic per-topic items: connection pip · label · pause glyph · three-dot menu
- Footer: Connections, Settings

`RebuildTopicItems` lays out the dynamic topic items by `TopicSettings.GroupName`: ungrouped topics first at the top level, then one collapsible folder per group (a non-navigating `NavigationViewItem` with the group's topics as child `MenuItems`). Folders carry an aggregate unread badge and persist their expand/collapse state in `AppSettings.CollapsedGroups` (watched via a `DependencyPropertyDescriptor` on `IsExpanded`, detached on `Unloaded`). With zero groups this is just a flat alphabetical list. Which server a topic is on is shown only as a subtitle, gated by `AppSettings.ShowServerLabel` (the old by-server *grouping* was replaced by user groups; the legacy `RailServerDisplay` enum survives only to migrate into `ShowServerLabel`).

`TrayIconHost` drives the tray icon colour from `ConnectionStatus` only. The tooltip composes all three axes ("connected, notifications paused, N unread") — the unread total comes from `UnreadTracker.Total`. (An on-icon count badge was tried but dropped: illegible at the tray's effective 16px size.)

## Messaging (event bus)

Cross-component communication goes through an in-process bus in `Core/Messaging`
(`EventBus`, backed by CommunityToolkit `WeakReferenceMessenger`). It replaced
FastEndpoints and the older per-class CLR events.

- **Events** implement `IEvent` (one per file under each feature's `Events/`
  folder). Publish with the extension: `new SomeEvent(...).PublishAsync()`.
- **UI consumers** (rail/`MainWindow`, `FeedViewModel`, `ConnectionsViewModel`,
  `MainWindowViewModel`, tray) subscribe via
  `EventBus.Subscribe<TEvent>(recipient, handler, ThreadOption.UIThread)` — the
  bus marshals to the UI thread, so handlers mutate bound state directly (no
  `Dispatcher.Invoke`). Recipients are weakly referenced.
- **Service consumers** (e.g. `ConnectionManager` reacting to topic lifecycle)
  are DI-resolved `IEventHandler<TEvent>` classes on the publisher thread.
- Three tiers: **structural** (topic/server lifecycle, ordering) → targeted
  single-item UI updates, with a rail rebuild reserved for rare/cross-container
  changes; **status/count** (connection, pause, unread) → O(1) targeted updates,
  never a rebuild; **aggregate** (`ConnectionStatusChanged`,
  `NotificationsStatusChanged`) → coarse signal + a cheap re-read.

The full catalog — every event, its publisher, and how each surface reacts — is
in [`docs/events.md`](docs/events.md).

> **Gotcha:** `PublishAsync` infers the bus envelope from the **static** type, so
> publishing through a base-typed `IEvent` variable yields `EventEnvelope<IEvent>`
> and matches no `Subscribe<Concrete>` recipient (a silent no-op). Always publish
> the concrete type.

## Event flow

The message pipeline specifically:

```
TopicConnection (WebSocket, resumed with ?since=<cursor timestamp>)
  └─ ConnectionManager.OnMessageReceived
       ├─ HistoryRepository.Insert — INSERT-OR-IGNORE; advances cursor; returns isNew
       │     └─ (if new) MessageInserted — Feed appends row; UnreadTracker bumps the badge
       └─ if isNew → NtfyMessageReceived (IsBackfill = replayed catch-up)
            └─ ShowToastNotification — gate (pause/priority/hours); live→toast,
                                       backfill→BackfillSummaryNotifier (debounced summary)
```

Both fan-outs fire **only for genuinely-new rows**. An inclusive `since=<timestamp>`
re-delivers each topic's boundary message on every reconnect; gating on `Insert`'s novelty
result is what stops that from showing a phantom "while you were away" summary or a duplicate
feed row.

## Security posture

| Concern | Current state |
|---|---|
| Access token at rest | DPAPI-encrypted (`CurrentUser` scope) in `settings.json` |
| Token over cleartext | `TopicConnection` refuses bearer header over `ws://` / `http://` |
| Attachment downloads | Bearer token sent only to a same-origin (scheme+host+port) **https** attachment URL on the topic's server — never to a publisher-supplied external host, never over cleartext (`AttachmentImageService.ResolveAuthToken`) |
| History database | **Not encrypted** — acknowledged tech debt |
| Exception output | `ex.ToString()` may include local paths — decision deferred |
