# Development

How we work on this repo: build/run, the Git + release workflow, and hard-won conventions.
For *how the app is built* see [ARCHITECTURE.md](ARCHITECTURE.md); for planned/deferred work see [issues-backlog.md](issues-backlog.md).

## Build / run

- `dotnet build NtfyDesktop.csproj` (or build/run from an IDE).
- Settings + history live under `App.DataPath` → `%AppData%\NtfyDesktop\`.

## Git workflow (per change)

**Trunk-based: a single long-lived branch, `master`.** Both update channels are cut from it — the channel is decided by the **tag's version suffix, not the branch** (see Releasing). (History: an earlier `dev`/`master` two-branch model was retired in 0.6.6 — channels are tag-driven, so the second branch only added drift/sync friction.)

1. Branch off `master`: `feature/<name>`.
2. Implement and **build**, then **verify the change works in the running app** — a green build alone is not the gate.
3. Commit and **push the feature branch** (`git push -u origin feature/<name>`).
4. Open a PR → `master` and merge after review. CI (`build.yml`) runs on pushes/PRs to `master`.
5. Delete the feature branch (local + remote) after merge.

**Exception — commit directly to `master` (no PR)** for trivial non-code/release-meta changes (README/doc edits, version bumps, CI/workflow tweaks).

> Maintainer-specific collaboration details (who tests, who commits, who merges, no `gh` CLI in this setup) live in local, untracked notes — see `.claude/LOCAL.md`.

## Releasing (tag-driven; the tag's version picks the channel — both cut from `master`)

- **Dev release** (as features land toward a milestone): set `<Version>` to a SemVer pre-release of the target — `0.7.0-dev.1` (then `-dev.2`, … / `-rc.1`) — and tag `master` (`v0.7.0-dev.1`). Any `-` suffix → `release.yml` packs the **`dev`** channel, GitHub **pre-release**. Dev-channel installs auto-update through every dev build.
- **Stable release** (milestone complete): set `<Version>` to the final `0.7.0` (no suffix), tag `master` (`v0.7.0`). No suffix → **`stable`** channel, not pre-release → stable installs auto-update, and `releases/latest` (hence the README link) points here. Because dev builds are pre-releases, `releases/latest` always tracks the newest *stable* tag even while dev tags are newer.
- Use dotted SemVer (`0.7.0-dev.1`, not `0.7-dev-1`) so ordering is correct.
- Channels wall the two apart (each install reads its own `releases.{channel}.json`). **In-app channel switching is implemented** (Settings → Updates, installed builds only): persists `AppSettings.UpdateChannel`, points `UpdateManager` at it via `ExplicitChannel` + `AllowVersionDowngrade` (dev→stable is a downgrade), surfacing the cross-channel build through the normal "Restart & update" banner. Installing the matching Setup still works as an alternative opt-in.

### Hotfixing a released stable version

Trunk-based escape hatch — `master` has moved on to newer dev work you don't want to ship:

1. Branch from the **released tag**, not `master`: `git switch -c hotfix/1.1.1 v1.1.0` (or reuse a `release/1.1` branch if one exists).
2. Make the fix; bump `<Version>` to the patch (`1.1.1`) on that branch.
3. Tag the patch (`v1.1.1`) → no suffix → stable channel → stable users get it. (`release.yml`'s delta step pulls the previous stable, `v1.1.0` — correct, since `1.2` is still dev-only.)
4. **Forward-port:** `git switch master` and **cherry-pick the fix commit(s)** (NOT the version-bump) so the next `1.2` build keeps the fix. Merging the branch instead would drag the `1.1.1` bump onto `master` and flip-flop its version.
5. Delete `hotfix/1.1.1`, **or** keep it as **`release/1.1`** if that version line will accrue more patches (the one case a branch outlives a single fix).

- Dev users don't get the stable hotfix directly (different channel); the cherry-pick means the next `…-dev.N` carries it — cut one promptly if it's urgent for them too.
- **Future (when there are real users): automate this.** On a stable tag, auto-create the matching `release/<major.minor>` branch; hotfixes land there and a workflow opens the forward-port PR (cherry-pick) back to `master`. Not worth the machinery yet.

## Conventions / decisions worth not relearning

- **Topics-as-channels in the nav rail** (no tray submenu). "All Topics" + dynamic per-topic items.
- **Page-level scrolling fix:** set `ScrollViewer.CanContentScroll="False"` on each `Page`. NavigationView's outer ScrollViewer otherwise gives content unbounded height. Do NOT try to override default styles by type — it broke the Frame journal.
- **PasswordBox isn't bindable.** `SettingsPage.xaml.cs` pumps `_vm.AccessToken` ↔ `TokenBox.Password` manually with a `_suspendTokenSync` flag.
- **Dirty tracking is snapshot-based.** `SettingsViewModel.FormSnapshot` captured on `Load()`; `OnPropertyChanged` recomputes `IsDirty = !TakeSnapshot().Equals(_snapshot)`. A property that's been edited and manually reverted clears dirty.
- **Bindings on the Settings page use `UpdateSourceTrigger=PropertyChanged`** explicitly — default `LostFocus` broke dirty for `NumberBox`. Active hours are bound as `string ActiveHoursStartText/EndText`, parsed in `SaveAsync`.
- **Smart restart:** `SettingsViewModel.SaveAsync` only calls `RestartAllAsync` when ServerUrl or AccessToken actually changed.
- **Nav-away guard:** `MainWindow` hooks `RootNavigation.Navigating`. If leaving SettingsPage with `IsDirty`, cancels, shows a `Wpf.Ui.Controls.MessageBox` (Save/Discard/Cancel), then re-issues nav with `_bypassDirtyGuard=true`.
- **NavigationViewItem Click + no `TargetPageType` + `e.Handled=true`** leaves selection alone. Don't add restoration logic for action items — it'll cause unwanted selection sliding.
- **Pause-glyph and pause-badge are everywhere they need to be, in one place:** in the nav rail. Don't reintroduce pause UI on the Connections page.
- **Status pip colors are duplicated** in `MainWindow.xaml.cs` and `ConnectionsViewModel.cs` — kept frozen `SolidColorBrush` for perf. Don't centralize without reason.
- **Three-dot button alignment:** WPF-UI's `NavigationViewItem` template doesn't horizontally stretch its inner ContentPresenter regardless of `HorizontalContentAlignment`. The topic-item content DockPanel sets `MinWidth=160` to force the docked-right button to the rail's right edge. If you change `OpenPaneLength`, update that number to match.
- **Crisp-text hints on the FluentWindow:** `UseLayoutRounding="True" SnapsToDevicePixels="True" TextOptions.TextFormattingMode="Display" TextOptions.TextRenderingMode="ClearType" RenderOptions.ClearTypeHint="Enabled"`. These cascade. WPF text on Windows has a hard ceiling regardless — don't chase further blur fixes.
- **Pause icons use `Filled="True"` (`SymbolIcon`).** Filled glyphs render sharper at small sizes.
- **Background is `White`, not `Transparent`.** Mica looked nice but ClearType can't render over a translucent backdrop. `White` + `WindowBackdropType="Mica"` is fine — Mica simply has no visible effect with a white background.
- **Toast click activation routes through `ntfy-desktop://`.** Every toast carries a `launch` URL: the publisher's `Click` URL if it passes `SafeUrl.IsAllowed`, otherwise a generated `ntfy-desktop://show?topic=...&msg=...`. The scheme is registered per-user via `ProtocolRegistration` on startup. A second exe launched by Windows for the toast click forwards its args to the existing instance via the per-data-path NamedPipe (`SingleInstanceServer`); the running instance shows the window and calls `MainWindow.NavigateToTopic`. NavigationView's `SelectedItem` setter isn't public, but `Navigate(item.Id)` (the per-item id, not `typeof(FeedPage)`) activates that exact rail item — every rail item shares `TargetPageType=FeedPage`, so a type-based navigate would always land on the first match ("All topics"). `NavigateToTopic` navigates by id so the rail visually highlights the right topic and the resulting `SelectionChanged` drives the feed/unread (mirroring a user click); falls back to "All topics" if the topic was removed. It also expands the topic's group folder first — WPF-UI nulls a child item's parent link on unload (a collapsed folder unloads its children), so `NavigationViewItem.Activate` can't auto-expand the folder.
- **Color emojis don't render in-app.** WPF's text engine doesn't read COLR/CPAL color tables in Segoe UI Emoji — only the monochrome fallback glyphs. Tried overriding `TextFormattingMode` / `TextRenderingMode` per-element; still mono. Accepting the limitation. Toasts (rendered by Windows shell, not WPF) are in color. To get color emojis in-app would need image-based rendering (Twemoji sprite) or WebView2 — both too heavy for the payoff.

## Don'ts (learned the hard way)

- Don't add custom `Style` for WPF-UI controls without `BasedOn="{StaticResource {x:Type ui:Button}}"` — the ControlTemplate gets replaced and icons render as gray squares.
- Don't try to override page styles by type to suppress the outer ScrollViewer — use the `ScrollViewer.CanContentScroll="False"` attached property instead.
- Don't navigate via `NavigationView.SelectedItem = ...` — setter isn't public. Use `Navigate(typeof(...))`.
- Don't gate connection startup on `IsPaused` — `ApplySettingsAsync` must start connections regardless; pause is enforced at message-delivery time.
- Don't reintroduce pause logic on `ConnectionManager` or pause UI on `ConnectionsPage`. The two axes are separated for a reason.
- Don't add NavigationViewItem selection-restore for action items with no `TargetPageType` — they don't get selected when `e.Handled=true`.

## Open / deferred (small)

Larger planned work and its design notes live in [issues-backlog.md](issues-backlog.md). Small standing items:

- Decide what to filter from `ex.ToString()` in global exception handlers (it may include local paths).
- The WPF-UI ContextMenu entrance animation (slides down from above the trigger) is a known annoyance; disabling it would need a custom Popup-based menu. Accepted — not worth it.
- Residual text-rendering blur in WPF-UI–themed body text. All standard WPF fixes are applied; further improvement would need restyling individual controls. Living with it.
