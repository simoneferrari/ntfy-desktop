# Issues & backlog

Known bugs and small deferred items not yet worth a roadmap entry.

**Lifecycle:** open items live under **Open** (newest first); when a fix is in flight,
add a `Status:` line naming the branch rather than removing the item. Once a fix is
**confirmed and merged**, move the item to **Resolved** with a one-line resolution and a
reference (branch/PR, plus the version once it's tagged). Resolved entries double as
release-note material.

## Open

### Bugs

#### Unread badge lingers after collapsing a group folder

When a group folder is collapsed, an unread-count badge from one of its (now-hidden) topics
stays visible in the rail. WPF keeps a collapsed folder's child rows loaded (only their
container is hidden), so the child icon never raises `Unloaded` and its `BadgeAdorner` lingers
in the adorner layer at the icon's stale position. Pre-existing; surfaced while testing the
toast-click fix.
Status: fixed on `fix/collapsed-folder-unread-badge` — `BadgeAdorner` binds its `Visibility`
to the adorned icon's `IsVisible`, so it hides whenever an ancestor collapses. Awaiting merge.

### Small items

_None currently._

## Resolved

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
