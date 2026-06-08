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

### Publish messages (compose) — 0.8

- The app is currently receive-only; ntfy is two-way. A compose window POSTs to a topic with title, body, priority, tags, `click` URL, and (stretch) action buttons — turning this into a full client. Supersedes the old "test-publish dialog" idea (dropped from 0.9; a real compose covers it).
- Reuse what exists: the server list + `ServerConfig.GetAuthorizationHeader()` for auth (the one source of truth for authenticated requests — see the auth note below), and the receive-side tag/markdown/action rendering as a live preview.
- Caveats: respect the same insecure-`http` credential warning as elsewhere; publish is `POST {server}/{topic}` (JSON body or headers — pick one and stay consistent); surface server errors (rate limit / 4xx) inline rather than a silent failure.

### Code signing (SignPath Foundation) — not milestone-gated

- Pursue the free [SignPath Foundation](https://signpath.org/) OSS programme (Azure Artifact Signing is **not** free — needs a paid subscription). Key on their HSM; GitHub Actions connector slots into the Velopack release pipeline.
- **Approval is the long pole, not the integration.** [Terms](https://signpath.org/terms.html) require: OSI license (MIT ✓), actively maintained + already released ✓, but executables must show "verifiable reputation" — a young pre-release may be asked to wait for download history. Also required: MFA on repo *and* SignPath, a documented **code-signing policy page** on the project homepage, and an automated/verifiable CI build with product-name/version metadata set.
- Plan as "apply now; ship signing once granted" — don't gate the 0.8 release on it. Removes the SmartScreen "Windows protected your PC" prompt the README currently documents.

### Per-topic custom notification sound / icon — 0.8

- Today sound is priority-based and global. Power-user ask: a **distinct sound per topic** so the source is identifiable by ear without looking. Feasible via custom audio in the toast XML (per-topic config on the topic, alongside display name / server).
- **Built-in library + fully custom.** The picker should default to a **curated set of bundled sounds and icons** chosen from a dropdown (the common case — no file wrangling), with a **"Custom…"** option to point at any local sound/image file. For sounds this means either the Windows `ms-winsoundevent:` presets (zero-bundle) and/or a few audio files we ship and reference via `ms-appdata`/`ms-appx`; for icons, a small bundled set alongside the user's own image. A custom file should be **copied under the data dir** so the toast can always resolve it (and it survives the source file moving) — and is covered by settings export if/when that lands.
- Adjacent smaller win: ntfy carries a per-message `icon` field — surface it in the feed (and toast where possible). Separable from the per-topic icon work.

### Timed snooze / mute — 0.8

- Auto-expiring mute ("silence for 1h / 8h / until tomorrow") for a topic or globally, vs. today's manual pause toggle + active-hours window. Reuse the existing pause machinery; the new part is an expiry timestamp + a timer that lifts it (and survives restart — persist the until-time, re-arm on launch, lift immediately if already past).

### Socket multiplexing (per server)

- Today `ConnectionManager` opens **one WebSocket per topic**; the official web client multiplexes all of a server's topics onto one socket (`wss://server/topic1,topic2/ws`).
- **Why it matters (real diagnosis):** a user hit "200 when 101 expected" on a couple of topics after restarting/killing the app repeatedly — orphaned per-topic sockets pushed them over the server's per-visitor subscription/connection limit, while the web client's single socket stayed under it.
- Fix: group enabled topics by `ServerId`, open one `TopicConnection` per server subscribing to the comma-joined topic list, and route received messages back to the right topic by name. Touches `ConnectionManager` (keying) + `TopicConnection` (URL build, message→topic resolution).
- Non-trivial: the per-topic pip/pause/reconnect UI must still work per topic over a shared socket. Also interacts with catch-up — `since` becomes per-topic-within-one-socket (ntfy's comma-topic URL supports it, but it complicates the cursor).

### Selectable message body text (feed)

- Message bodies in the feed should be **selectable so they can be copied**.
- Caveat: the feed renders a **Markdown subset** (bold/italic/inline code/code blocks/links/line breaks — see the resolved markdown note below), so the body is a tree of inline runs and block elements, not one flat `TextBlock`. Selection has to span those rendered runs and code blocks, and copy should yield **clean text** (the rendered/plain text, not raw `**syntax**`). The existing markdown→plain-text flattening (already used for toasts) is the reference for what "clean" copy looks like.

### Update experience polish

Refinements on the automatic-updates feature shipped in 0.7. Four related sub-items:

- **More frequent background check.** Today the background check runs roughly **once a day**; drop the interval to **every 30–60 min**. One-liner in the update scheduler, but confirm it doesn't hammer GitHub Releases (respect rate limits / keep the on-startup check as-is).
- **Manual "check for updates" in the title bar.** Add a manual-check entry point in the **main-window title bar, next to "Pause notifications"**, so a check is reachable without going into Settings → Updates.
- **Tray check needs immediate feedback.** Checking for updates **from the tray** currently does nothing visible until a version is found (banner on the main window or a Windows toast) — if you're up to date, it looks like nothing happened. Show instant feedback when the check *starts* ("Checking for updates…") and an explicit **"You're up to date"** result, not just the success-case banner.
- **Download-progress redesign.** The current download-progress design isn't liked; prefer a **shorter and taller** treatment. Exact design **TBD at implementation**.

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
