# Issues & backlog

Bugs, small deferred items, and design notes for planned-but-unshipped work. The
**public roadmap** (what + status) lives in [README.md](README.md); this file is the
internal "how/why" behind the unshipped items, plus the bug tracker.

**Lifecycle:** open items live under **Open** (newest first); when a fix is in flight,
add a `Status:` line naming the branch rather than removing the item. Once a fix is
**confirmed and merged**, move the item to **Resolved** with a one-line resolution and a
reference (branch/PR, plus the version once it's tagged). Resolved entries double as
release-note material. Design notes under **Planned** move out (to Resolved, or just
deleted once their rationale has landed in [ARCHITECTURE.md](ARCHITECTURE.md)) as the
work ships.

## Open

### Bugs

_None currently._

### Small items

_None currently._

## Planned — design notes

Internal notes behind unshipped [README.md](README.md) roadmap items: caveats and
"stuff to remember when we get there," so we don't re-litigate them. Rationale for
*shipped* features lives in [ARCHITECTURE.md](ARCHITECTURE.md).

### Socket multiplexing (per server)

- Today `ConnectionManager` opens **one WebSocket per topic**; the official web client multiplexes all of a server's topics onto one socket (`wss://server/topic1,topic2/ws`).
- **Why it matters (real diagnosis):** a user hit "200 when 101 expected" on a couple of topics after restarting/killing the app repeatedly — orphaned per-topic sockets pushed them over the server's per-visitor subscription/connection limit, while the web client's single socket stayed under it.
- Fix: group enabled topics by `ServerId`, open one `TopicConnection` per server subscribing to the comma-joined topic list, and route received messages back to the right topic by name. Touches `ConnectionManager` (keying) + `TopicConnection` (URL build, message→topic resolution).
- Non-trivial: the per-topic pip/pause/reconnect UI must still work per topic over a shared socket. Also interacts with catch-up — `since` becomes per-topic-within-one-socket (ntfy's comma-topic URL supports it, but it complicates the cursor).

### Windows Focus Assist integration

- `Windows.UI.Notifications.Management.UserNotificationListener` — needs a user permission grant.

### `ntfy://` URL scheme handler

- Register `HKCU\Software\Classes\ntfy` per-user; the handler should open the app and trigger Add Topic with the URL prefilled.

## Resolved

#### Username/password authentication (0.7)

A server now authenticates with **either** an access token **or** a username/password pair (HTTP
Basic), chosen per server in the server editor. `ServerConfig` carries `AuthMethod`
(`Token`/`Password`, defaulting to `Token` so older `settings.json` deserialise unchanged), a
plaintext `Username`, and a DPAPI-encrypted `EncryptedPassword` alongside the existing encrypted
token. A single `ServerConfig.GetAuthorizationHeader()` produces the full `Authorization` value —
`Bearer <token>` or `Basic <base64(user:pass)>` — and is now the one source of truth for every
authenticated request: the subscribe socket (`TopicConnection`, which still refuses to send it over
cleartext `ws://`) and same-origin https attachment downloads (`AttachmentImageService`). The editor
shows a radio choice (token ↔ username & password) toggling the relevant fields, the password pumped
through the non-bindable `PasswordBox` like the token, and the insecure-http warning now covers either
credential. Branch `feature/username-password-auth`.

#### Markdown subset rendering in message bodies (0.7)

The feed renders a small Markdown subset — **bold, italic, inline code, fenced code blocks,
links, and line breaks** — when ntfy flags a message `text/markdown` (the `content_type` field);
a hand-written recursive-descent parser, no CommonMark dependency. The long tail (headings,
lists, tables, blockquotes) passes through as plain text, and markdown is **never auto-detected**
in a non-flagged body (it would mangle e.g. `_underscored_filenames_`). Windows toasts can't
render markup, so their body is flattened to clean plain text via the same parser instead of
showing raw `**syntax**`. `content_type` rides the pipeline via an additive `EnsureColumn`
migration. Design lives in [ARCHITECTURE.md](ARCHITECTURE.md) (Feed → Markdown bodies). Branch
`feature/markdown-rendering`.

#### Unread badge lingered after collapsing a group folder

A collapsed group's hidden child topic still showed its unread badge in the rail. WPF keeps a
collapsed folder's child rows loaded (only their container is hidden), so the child icon never
raised `Unloaded` and its `BadgeAdorner` lingered in the adorner layer at the icon's stale
position. **Fix:** `BadgeAdorner` binds its `Visibility` to the adorned icon's `IsVisible`, so
it hides whenever any ancestor collapses and reappears on expand. Branch
`fix/collapsed-folder-unread-badge`.

#### Toast click didn't highlight the topic in the rail

Clicking a toast navigated the feed to the topic but the rail kept "All topics" selected
instead of the topic. Cause: `NavigateToTopic` used `Navigate(typeof(FeedPage))`, and since
every rail item shares `TargetPageType=FeedPage`, WPF-UI resolved that to the first match
("All topics"). **Fix:** navigate by the rail item's `Id` (activates that exact item and
raises `SelectionChanged`, which drives the feed/unread like a real click), and expand the
topic's group folder first (WPF-UI nulls a child's parent link on unload, so `Activate` can't
auto-expand a collapsed folder). Falls back to "All topics" if the topic was removed. Branch
`fix/toast-click-rail-selection`.

#### Phantom "messages while away" toast after deleting a message

Deleting a message, closing, then reopening fired a "N messages while you were away" summary
for the deleted (already-read) message; not deleting it showed nothing. Cause: ntfy's inclusive
`since=<time>` re-delivers the cursor's own boundary message on every reconnect, normally
absorbed by `INSERT OR IGNORE` — but once that message's row was deleted, the re-delivery
looked new and was resurrected into the feed/unread and counted in the summary. **Fix:**
`HistoryRepository.Insert` drops the re-sent boundary message by matching the stored
`topic_cursor.message_id` (which survives the row delete). Same-second multi-delete where one is
the cursor stays a known corner. Branch `fix/away-notification-after-delete`.

#### Main window opened too small from the tray

The window opened at a fixed ~1000×660 centred regardless of screen size. **Fix:** default to
maximized on a fresh install and persist the user's size/position/maximized state
(`AppSettings.WindowPlacement`), restoring it on launch (`MainWindow.ApplyPersistedPlacement`)
and capturing it in-memory on move/resize, flushed on hide and on exit. Off-screen saved bounds
are ignored; restoring from minimized returns to the remembered state. Branch
`feature/window-maximized-on-open`.


#### Newly-added topic's feed was empty on first click (until restart)

A topic added in-session (most visibly one dropped into a brand-new group folder) didn't
switch the feed when clicked the first time — only an app restart fixed it. Cause: WPF-UI's
`NavigationView` doesn't reliably register a dynamically-inserted item for selection, so the
first click never raised `SelectionChanged` and `FeedViewModel.CurrentTopicId` never changed.
**Fix:** rebuild the rail on `TopicAdded` (`MainWindow.OnTopicAdded` → `RebuildTopicItems`)
instead of incrementally inserting, matching the existing `OnTopicMoved` rebuild. Branch
`fix/new-topic-feed-refresh`. First seen v0.6 while testing action buttons.
