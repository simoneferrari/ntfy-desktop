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
| Messaging | FastEndpoints.Messaging — event bus (`NtfyMessageReceived` published from `ConnectionManager`) |

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
- Fires `ConnectionStatusChanged` when the aggregate status changes.
- `TopicConnection` refuses to send the bearer header over `ws://` (cleartext).

### Notifications

`NotificationGate` (singleton) owns all pause read/write.

- Global pause: `PauseAll()` / `ResumeAll()` / `IsGloballyPaused`.
- Per-topic pause: `PauseTopic(name)` / `ResumeTopic(name)` / `IsTopicPaused(name)`.
- Writes through to `AppSettings.IsPaused` and `TopicSettings.IsPaused`.
- Fires `GlobalStatusChanged` and `TopicPauseChanged` events.

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

`TopicsViewModel` is the canonical list of configured topics (`ObservableCollection<TopicSettings>`). It exposes `AddOrUpdateAsync`, `RemoveAsync(topic, deleteHistory)`, and `ToggleEnabledAsync`. Topic CRUD is surfaced in the nav rail (not on a dedicated settings page). Removal prompts the user to keep the topic's history (still browsable under "All topics") or delete it, mirroring server removal. `TopicSettings.GroupName` (nullable) assigns a topic to a rail folder, set via an editable combo in the topic editor.

Ordering is manual: the `AppSettings.Topics` list order is the source of truth for topic order *within a section* (a group, or the ungrouped set), and `AppSettings.GroupOrder` for the folder order. A one-time `Migrate()` seed (gated by `OrderInitialized`) sorts both alphabetically so the first launch matches the old alphabetical rail. `TopicsViewModel` exposes `MoveTopic`/`MoveTopicToGroup`/`MoveGroup` (+ `Can…` guards) and `SyncGroupOrder` (reconciles `GroupOrder` with groups actually in use). Reorders persist and raise `AppSettings.DisplayChanged` to rebuild the rail. Surfaced via the topic three-dot menu (Move up/down, Move to group) and a folder right-click menu (Move up/down); drag-and-drop is a planned follow-up.

### Settings

`AppSettings` owns JSON serialisation and DPAPI-wrapped token storage (`GetAccessToken` / `SetAccessToken`). The `SettingsViewModel` uses snapshot-based dirty tracking: a `FormSnapshot` is taken on `Load()`; `IsDirty` is recomputed on every property change by comparing against the snapshot.

### History

`HistoryRepository` wraps SQLite. `HistoryRetentionService` (BackgroundService) sweeps old rows hourly. The database is **not** encrypted (acknowledged tech debt). The `messages.read` column (0/1) backs unread tracking; on first add it marks all existing rows read so upgraders don't see a badge flood. The repository fires `MessageInserted` (always) and `HistoryChanged` (after any delete) so consumers stay in sync without coupling to each caller.

### Unread

`UnreadTracker` (singleton) owns unread counts surfaced as rail badges. It caches per-topic counts in memory, updating incrementally on `HistoryRepository.MessageInserted` and re-seeding (`GetUnreadCounts`) on `HistoryChanged` / `TopicsChanged`. A feed is marked read on three triggers, all routed through `SetActiveView` / `SetWindowActive`: navigating to it, a message arriving while it's the active view and the window is focused, and the window regaining focus. The badge itself is a `BadgeAdorner` overlaid on each `NavigationViewItem`'s icon — the Icon slot only accepts an `IconElement`, so an adorner is the only way to sit a count bubble on top of the glyph. `MainWindow.xaml` wraps its content in an `AdornerDecorator` to guarantee an adorner layer.

### Shell

`MainWindow` is a three-row grid: TitleBar · caution strip (visible only when paused) · NavigationView. The nav rail has:

- "All topics" → FeedPage
- "Add topic" action item (opens `TopicEditorDialog`; does not navigate)
- Dynamic per-topic items: connection pip · label · pause glyph · three-dot menu
- Footer: Connections, Settings

`RebuildTopicItems` lays out the dynamic topic items by `TopicSettings.GroupName`: ungrouped topics first at the top level, then one collapsible folder per group (a non-navigating `NavigationViewItem` with the group's topics as child `MenuItems`). Folders carry an aggregate unread badge and persist their expand/collapse state in `AppSettings.CollapsedGroups` (watched via a `DependencyPropertyDescriptor` on `IsExpanded`, detached on `Unloaded`). With zero groups this is just a flat alphabetical list. Which server a topic is on is shown only as a subtitle, gated by `AppSettings.ShowServerLabel` (the old by-server *grouping* was replaced by user groups; the legacy `RailServerDisplay` enum survives only to migrate into `ShowServerLabel`).

`TrayIconHost` drives the tray icon colour from `ConnectionStatus` only. The tooltip composes both axes ("connected, notifications paused").

## Event flow

```
TopicConnection (WebSocket)
  └─ NtfyMessageReceived published (FastEndpoints)
       ├─ ShowToastNotification   — checks NotificationGate; shows/drops toast
       └─ HistoryRepository       — always inserts (pause doesn't suppress history)
            └─ MessageInserted     — Feed appends row; UnreadTracker bumps the badge
```

## Security posture

| Concern | Current state |
|---|---|
| Access token at rest | DPAPI-encrypted (`CurrentUser` scope) in `settings.json` |
| Token over cleartext | `TopicConnection` refuses bearer header over `ws://` / `http://` |
| History database | **Not encrypted** — acknowledged tech debt |
| Exception output | `ex.ToString()` may include local paths — decision deferred |
