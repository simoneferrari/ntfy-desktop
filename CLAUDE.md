# CLAUDE.md

Entry point for Claude sessions in this repo. Read the two docs below first, then follow the conventions here.

## Read first
- **`.claude/HANDOFF.md`** — session orientation: build/run, the Git workflow, and hard-won conventions/don'ts.
- **`ARCHITECTURE.md`** — the stable design (feature layout, event bus, connection ↔ notification separation).
- **`.claude/ROADMAP-NOTES.md`** — internal notes behind the public roadmap in `README.md`.
- **`issues-backlog.md`** — known bugs and small deferred items not yet on the roadmap.

## Tools
- **Use the context7 MCP server for docs about the app's NuGet packages** — WPF-UI, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, H.NotifyIcon.Wpf, etc. Query it (`resolve-library-id` → `query-docs`) rather than relying on memory, since your training data may lag behind current package versions.
- **Fall back when context7 can't help.** It only covers packages, and it sometimes errors or lacks coverage. For anything it doesn't serve — non-package APIs like the **ntfy HTTP/WebSocket protocol**, or when a context7 call fails — use **web search** (and read primary sources/source code as needed). Do **not** guess or rely on assumptions when a fallback is available.

## Workflow
- **Trunk-based: a single long-lived branch, `master`.** (An earlier `dev`/`master` two-branch model was retired in 0.6.6 — see `.claude/HANDOFF.md`.)
- Code changes go through a `feature/<name>` branch + PR → `master` (GitHub Flow; details in `.claude/HANDOFF.md`).
- Doc-only / version-bump / CI changes may be committed directly to `master`.
- **Releases are tag-driven** (both channels cut from `master`): tag `vX.Y.Z-dev.N` for a dev-channel pre-release, or `vX.Y.Z` for a stable release — the `-` suffix is what picks the channel. Hotfix a shipped stable by branching off its tag. Full recipe in `.claude/HANDOFF.md`.
- Commit or push only when asked.
