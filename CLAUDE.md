# CLAUDE.md

Entry point for Claude sessions in this repo. Read the two docs below first, then follow the conventions here.

## Read first
- **`.claude/HANDOFF.md`** — session orientation: build/run, the Git workflow, and hard-won conventions/don'ts.
- **`ARCHITECTURE.md`** — the stable design (feature layout, event bus, connection ↔ notification separation).
- **`.claude/ROADMAP-NOTES.md`** — internal notes behind the public roadmap in `README.md`.

## Tools
- **Use the context7 MCP server for library/framework/API documentation.** Whenever you need docs about a library, framework, SDK, API, or CLI used in this project (WPF-UI, CommunityToolkit.Mvvm, Microsoft.Data.Sqlite, the ntfy HTTP/WebSocket API, etc.), query context7 (`resolve-library-id` → `query-docs`) instead of relying on memory or web search. Your training data may lag behind current versions.

## Workflow
- Code changes go through a `feature/<name>` branch + PR → `master` (GitHub Flow; details in `.claude/HANDOFF.md`).
- Doc-only / version-bump changes may be committed directly to `master`.
- Commit or push only when asked.
