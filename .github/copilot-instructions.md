# Copilot instructions for KO Lite

## Repository shape

KO Lite is a standalone local-first dashboard and worker for running scheduled Kusto output jobs from a local SQLite catalog. The local app owns catalog, queue, slice state, operational logs, rerun reports, repair state, and UI read models in SQLite. Kusto is contacted only when scheduler or worker execution is enabled and a worker executes a slice.

Start with `README.md`. For architecture-sensitive work, read `docs\local-first-architecture.md` and `docs\operations-runbook.md`; for schedule JSON work, read `docs\schedule-json.md`; for validation commands, read `DEVELOPMENT.md`.

## Commands

From the repository root:

```powershell
npm ci
dotnet restore .\KoLite.Local.sln
dotnet format .\KoLite.Local.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build .\KoLite.Local.sln --no-restore --nologo
dotnet test .\KoLite.Local.sln --no-build --nologo
dotnet list .\KoLite.Local.sln package --vulnerable
npm audit --omit=dev --audit-level=moderate
```

For targeted changes, run the narrow relevant test project first, then decide whether the full solution test is needed. `npm ci` refreshes the bundled browser assets under `src\KoLite.LocalApp\wwwroot\lib` (Chart.js in `lib\chartjs`; Cytoscape.js + cytoscape-dagre in `lib\cytoscape`).

## High-level architecture

- `KoLite.Local.sln` is the active standalone local-first solution.
- `src\KoLite.Local.Core` contains schedule parsing, scheduling models, mutation policy, dependency readiness, rerun and repair contracts.
- `src\KoLite.Local.Sqlite` contains local SQLite persistence, schema, queue, catalog, state, observability, repair, and rerun services.
- `src\KoLite.Local.Kusto` contains live Kusto request building, authentication, execution, and error classification.
- `src\KoLite.LocalApp` contains the ASP.NET Core/Razor dashboard, hosted scheduler and worker services, health/shutdown endpoints, UI read models, and static assets.
- `tests\KoLite.Local.*` mirrors the active solution with unit, integration, web, and local end-to-end tests.
- `scripts` contains `Stop-KoLiteApp.ps1` (graceful drain shutdown of the local app), `Get-KoLiteDatabase.ps1` (reports the in-use local SQLite database path), `copy-chartjs.mjs` (refreshes the bundled Chart.js assets), and `copy-cytoscape.mjs` (refreshes the bundled Cytoscape.js + cytoscape-dagre assets); the copy scripts run via `npm ci`.

## Key conventions

- Keep `.github\copilot-instructions.md` as the single repo-level Copilot instruction file. Do not add duplicate root-level instruction files.
- Do not invoke the ADO or Bluebird MCP servers for work in this repository, even when they are available; they do not provide useful KO Lite context. Use local workspace tools and local `git` for repository discovery and history, and use the `gh` CLI for GitHub operations. This restriction applies only to those two servers; use other task-relevant tooling, including Kusto tooling, when appropriate.
- For branch, worktree, and pull request conventions, see the "Git workflow (feature branches and worktrees)" section below. By default, commit on the current branch (normally `main`) and do not create a branch, worktree, or pull request unless the user explicitly asks for one.
- Prefer Windows paths and PowerShell examples. Existing scripts use `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`; preserve that style in new PowerShell scripts.
- Use the .NET SDK from `global.json`. When writing C#, use block-scoped namespace declarations (`namespace Name { ... }`) instead of file-scoped namespaces, keep app entry points inside an explicit `Program` class instead of top-level statements, and use conventional class declarations with explicit constructors instead of class primary constructors.
- Preserve local-first safety defaults. Do not enable live scheduling, widen destructive operations, weaken explicit confirmations, or execute Kusto cleanup/import actions unless the user explicitly asks for that outcome.
- Treat local SQLite files as durable user runtime state. Do not reset or delete them unless the user explicitly requests it and the existing code path requires confirmation.
- The local app should use the real Kusto output writer for user-facing execution. Test projects may use in-memory or fake executors where they already exist.
- Scheduler enqueue behavior and worker claim/execution behavior are separate. Preserve per-job `maxParallelism`, global worker concurrency, visibility/query timeout leases, pause/delete state, and dependency readiness semantics.
- Schedule JSON import/export accepts a single schedule object or an array. The parser rejects unknown top-level fields, unknown `target` fields, and unknown `dependsOn` entry fields. A job's permanent identity is an opaque GUID `id` (server-assigned, immutable); `activityId` is a mutable, unique display label. Imports match by `id` when present (enabling rename = same `id`, new `activityId`), else by `activityId`. `dependsOn` entries reference an upstream by `id` and/or `activityId` and are stored by GUID.
- `description` is optional Markdown catalog metadata, limited to 65,536 characters. It round-trips through copy, import/export, catalog history, and the single-job API schedule; it is omitted from compact API job summaries and must never enter Kusto requests or function arguments. When the schedule contract changes, keep the C# parser, standalone PowerShell validator, both schedule-management skills and templates, API docs, and tests synchronized.
- For `.csl` and `.kql`, follow `.github\instructions\kusto.instructions.md`.

## Git workflow (feature branches and worktrees)

By default, commit on the current branch (normally `main`); do **not** create a git branch, worktree, or pull request unless the user explicitly asks for one. When the user does ask for a feature branch or worktree, use the following format unless they specify a different name or location.

- **Branch name:** `<username>/<feature>`, the feature in kebab-case (e.g., `benmartens/schedule-json-export`).
- **Group worktrees** under a sibling folder of the repo root named `ko-lite.worktrees\`, one subfolder per feature: `ko-lite.worktrees\<feature>` (e.g., `C:\src\ko-lite.worktrees\schedule-json-export`, alongside `C:\src\ko-lite`). Grouping them this way avoids loose worktree folders scattered in the parent directory.
- **Create from the repo root (on `main`):**

  ```powershell
  git worktree add ..\ko-lite.worktrees\<feature> -b <username>/<feature>
  git -C ..\ko-lite.worktrees\<feature> push -u origin <username>/<feature>
  ```

  `git worktree add` creates the `ko-lite.worktrees` parent folder as needed.
- **Teardown when the feature is done:** merge the branch into `main` (or open a PR), push `main`, then `git worktree remove <dir>`, `git branch -d <username>/<feature>`, and `git push origin --delete <username>/<feature>`. Leave other contributors' worktrees alone.
- **Concurrent-activity caution:** this repo can have concurrent worktree activity on `main`, so re-check `HEAD`/the tip immediately before any merge, amend, reset, or rebase, and stage only your own files so unrelated working-tree changes from other sessions aren't swept into your commit.

## Kusto safety

KO Lite can write to Kusto through `.set-or-append`. Review every target cluster, database, function, output table, and permission before enabling scheduling or running Kusto commands. Prefer query validation patterns that avoid materializing large result sets: small limits, `| consume`, explicit timeouts, and saved artifacts when needed.
