# Issues & backlog

Known bugs and small deferred items not yet worth a roadmap entry. Newest first.
Move an item to a `feature/`/`fix/` branch when picking it up; delete it once merged.

## Bugs

### Newly-added topic's feed is empty on first click (until restart)

**Repro**
1. Add a new server (e.g. `https://ntfy.sh`).
2. Add a topic (e.g. `test`) on that server, assigned to a brand-new group.
3. Publish a message to the topic.
4. The rail shows a `1` unread badge on the topic.
5. Click the topic in the rail.

**Observed:** the per-topic feed page does not update — the new message isn't shown.
Navigating to **All topics** *does* show the message. After closing and reopening the
app, clicking the topic shows the message correctly.

**Expected:** clicking a freshly-added topic shows its messages immediately, no restart.

**Notes / suspected area:** the message persists fine (it's in `history.db` and renders
after a restart and in the All-topics feed), so this is an in-session UI/state refresh
issue, not persistence. Likely in the rail navigation → `FeedViewModel` per-topic reload
path for a topic added during the current session (`MainWindow.NavigateToTopic` /
`FeedViewModel.CurrentTopicId` reload, or the new topic id not being wired up until the
next launch). First seen v0.6 while testing action buttons.
