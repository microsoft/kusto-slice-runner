# Copilot instructions for Kusto Slice Runner

## Repository shape

Kusto Slice Runner is a standalone local-first dashboard and worker for running scheduled Kusto output jobs from a local SQLite catalog. The local app owns catalog, queue, slice state, operational logs, rerun reports, repair state, performance observations, and UI read models in SQLite. Normal execution-enabled instances contact Kusto for slice execution and mandatory background command-statistics collection; explicit lineage resolution is another read-only Kusto path. Performance page requests read SQLite only.

Start with `README.md`. For architecture-sensitive work, read `docs\local-first-architecture.md` and `docs\operations-runbook.md`; for schedule JSON work, read `docs\schedule-json.md`; for validation commands, read `DEVELOPMENT.md`.

## Commands

From the repository root:

```powershell
npm ci
npm run test:js
dotnet restore .\Ksr.Local.sln
dotnet format .\Ksr.Local.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build .\Ksr.Local.sln --no-restore --nologo
dotnet test .\Ksr.Local.sln --no-build --nologo
dotnet list .\Ksr.Local.sln package --vulnerable
npm audit --omit=dev --audit-level=moderate
```

For targeted changes, run the narrow relevant test project first, then decide whether the full solution test is needed. `npm ci` refreshes the bundled browser assets under `src\Ksr.LocalApp\wwwroot\lib` (Chart.js in `lib\chartjs`; Cytoscape.js + cytoscape-dagre in `lib\cytoscape`).

## High-level architecture

- `Ksr.Local.sln` is the active standalone local-first solution.
- `src\Ksr.Local.Core` contains schedule parsing, scheduling models, mutation policy, dependency readiness, rerun and repair contracts.
- `src\Ksr.Local.Sqlite` contains local SQLite persistence, schema, queue, catalog, state, observability, repair, and rerun services.
- `src\Ksr.Local.Kusto` contains live Kusto request building, authentication, execution, and error classification.
- `src\Ksr.LocalApp` contains the ASP.NET Core/Razor dashboard, hosted scheduler and worker services, health/shutdown endpoints, UI read models, and static assets.
- `tests\Ksr.Local.*` mirrors the active solution with unit, integration, web, and local end-to-end tests.
- `scripts` contains `Stop-KsrApp.ps1` (graceful drain shutdown of the local app), `Get-KsrDatabase.ps1` (reports the in-use local SQLite database path), `copy-chartjs.mjs` (refreshes the bundled Chart.js assets), and `copy-cytoscape.mjs` (refreshes the bundled Cytoscape.js + cytoscape-dagre assets); the copy scripts run via `npm ci`.

## Key conventions

- Keep `.github\copilot-instructions.md` as the single repo-level Copilot instruction file. Do not add duplicate root-level instruction files.
- Do not invoke the ADO or Bluebird MCP servers for work in this repository, even when they are available; they do not provide useful Kusto Slice Runner context. Use local workspace tools and local `git` for repository discovery and history, and use the `gh` CLI for GitHub operations. This restriction applies only to those two servers; use other task-relevant tooling, including Kusto tooling, when appropriate.
- For branch, worktree, and pull request conventions, see the "Git workflow (feature branches and worktrees)" section below. By default, commit on the current branch (normally `main`) and do not create a branch, worktree, or pull request unless the user explicitly asks for one.
- Prefer Windows paths and PowerShell examples. Existing scripts use `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`; preserve that style in new PowerShell scripts.
- Use the .NET SDK from `global.json`. When writing C#, use block-scoped namespace declarations (`namespace Name { ... }`) instead of file-scoped namespaces, keep app entry points inside an explicit `Program` class instead of top-level statements, and use conventional class declarations with explicit constructors instead of class primary constructors.
- Preserve local-first safety defaults. Do not enable live scheduling, widen destructive operations, weaken explicit confirmations, or execute Kusto cleanup/import actions unless the user explicitly asks for that outcome.
- Treat local SQLite files as durable user runtime state. Do not reset or delete them unless the user explicitly requests it and the existing code path requires confirmation.
- Startup retires obsolete throttling observation storage. Follow the operations runbook's upgrade precautions; never validate a new build against an older app's live database, even in UI-only mode.
- Preserve the three HTTP boundaries: versioned loopback-only agent JSON under `/api/v1` (generated OpenAPI, named contracts, Problem Details, ETag/If-Match, opaque cursor pagination), canonical browser job routes under `/jobs`, and minimal `/healthz` plus loopback-only `/control/v1`. Agent actions may create/update/pause/resume/soft-delete/restore and repair failed work, but must never expose hard delete, whole-slice rerun, Kusto cleanup, or arbitrary Kusto writes.
- When the HTTP contract changes, update generated-contract tests, both schedule-management skills and helper scripts, the maintainer agent, operational scripts, release packaging/smoke tests, API and operations docs, and stale-route assertions together. Treat already-loaded Copilot sessions and old extracted release folders as cross-version clients.
- The local app should use the real Kusto output writer for user-facing execution. Test projects may use in-memory or fake executors where they already exist.
- Scheduler enqueue behavior and worker claim/execution behavior are separate. Preserve per-job `maxParallelism`, global worker concurrency, visibility/query timeout leases, pause/delete state, and dependency readiness semantics.
- Optional `chunks` (1-32) splits one logical time window into 0-based child executions. The parent window is complete, dependency-ready, and healthy only after every chunk completes. `maxParallelism` counts execution units (one per chunk or unchunked slice), has no upper limit, and is enforced per job. The all-up worker pool is unbounded by default but can be configured with `Ksr:WorkerPool:MaxConcurrency`; `MaxDispatchStartsPerCycle=100` limits starts per cycle, not total concurrency. Pause stops new chunk scheduling/claims/retries immediately while in-flight chunks finish. `chunks` is immutable after a job starts.
- Keep Kusto ingestion identity separate from local queue identity. Unchunked ingest-by keys must remain unchanged. Every chunk has one stable key derived from parent slice key + chunk id + total chunks, reused across retry, repair, orphan recovery, and restart; different chunks must never share a key.
- For chunk failures, keep repair, rerun, and orphan recovery distinct. Repair automatically queues every terminal failed chunk without active retry work and preserves successful siblings; rerun resets every chunk/downstream after cleanup acknowledgement; orphan recovery only requeues expired leases. Keep chunk IDs visible and consistent across logs, diagnostics, UI, and durable repair/rerun history.
- Keep Activity metrics explicit about granularity: running/queued logical counts and the running table remain one per parent time window, while execution counts and processed totals/charts are per chunk (or per unchunked slice). Processed metrics use each execution unit's latest terminal outcome so retries do not inflate throughput. Keep one running row per logical window, expose every active chunk/worker, and base chunked ETAs on measured whole-window durations rather than multiplying per-chunk durations.
- Activity Performance has a different population: all known completed attempts for reliability, successful query-attempt resource samples for exact pooled job/chunk percentiles. Never substitute local elapsed time for server duration, average chunk percentiles/rates, infer region labels, or include running/unknown outcomes. Retain performance facts across reruns and remove them on confirmed job hard deletion.
- Gate the prominent Performance warning on visible-job coverage in the selected period: at least 20% missing and at least five missing successful attempts, excluding the newest five minutes and known duplicate-suppressed attempts. Count missing-any-resource once per attempt, sum parent job counts rather than chunk/job percentages, and update the gate during local name filtering. Keep lower-impact/global errors in Collection details; do not change table statistics or collector behavior.
- Performance collection/backfill is automatic, without a feature off switch, and inherits `Ksr:Scheduler:Enabled=false` for explicit no-execution runs. Generate/persist per-attempt request correlation without changing stable ingest-by identities. Test hosts with execution enabled must fake the statistics reader as well as the output executor.
- Schedule JSON import/export accepts a single schedule object or an array. The parser rejects unknown top-level fields, unknown `target` fields, and unknown `dependsOn` entry fields. A job's permanent identity is an opaque GUID `id` (server-assigned, immutable); `activityId` is a mutable, unique display label. Imports match by `id` when present (enabling rename = same `id`, new `activityId`), else by `activityId`. `dependsOn` entries reference an upstream by `id` and/or `activityId` and are stored by GUID.
- `description` is optional Markdown catalog metadata, limited to 65,536 characters. It round-trips through copy, import/export, catalog history, and the single-job API schedule; it is omitted from compact API job summaries and must never enter Kusto requests or function arguments. When the schedule contract changes, keep the C# parser, standalone PowerShell validator, both schedule-management skills and templates, the maintainer agent, API docs, and tests synchronized.
- For `.csl` and `.kql`, follow `.github\instructions\kusto.instructions.md`.

## External Kusto CLI skill

Kusto Slice Runner does not bundle a general Kusto CLI skill. When the external
`kusto-cli` skill is available, use it for explicit Kusto CLI requests and
control/management commands that read-only tools cannot perform. Run its
bundled `scripts\Invoke-KustoCli.ps1` from the loaded skill's base directory.

Always confirm and pass `-ClusterUri` and `-Database` explicitly. Kusto Slice Runner jobs
can target different clusters and databases, so do not infer a target from the
repository or app name. There is no default Kusto target. If a target is
unspecified, ask the user to provide it before running any Kusto command.

If a checked-in command contains a placeholder, stop and resolve the intended
concrete value before execution rather than silently rewriting it.

## Git workflow (feature branches and worktrees)

By default, commit on the current branch (normally `main`); do **not** create a git branch, worktree, or pull request unless the user explicitly asks for one. When the user does ask for a feature branch or worktree, use the following format unless they specify a different name or location.

- **Branch name:** `<username>/<feature>`, the feature in kebab-case (e.g., `benmartens/schedule-json-export`).
- **Group worktrees** under a sibling folder of the repo root named `kusto-slice-runner.worktrees\`, one subfolder per feature: `kusto-slice-runner.worktrees\<feature>` (e.g., `C:\src\kusto-slice-runner.worktrees\schedule-json-export`, alongside `C:\src\kusto-slice-runner`). Grouping them this way avoids loose worktree folders scattered in the parent directory.
- **Create from the repo root (on `main`):**

  ```powershell
  git worktree add ..\kusto-slice-runner.worktrees\<feature> -b <username>/<feature>
  git -C ..\kusto-slice-runner.worktrees\<feature> push -u origin <username>/<feature>
  ```

  `git worktree add` creates the `kusto-slice-runner.worktrees` parent folder as needed.
- **Teardown when the feature is done:** merge the branch into `main` (or open a PR), push `main`, then `git worktree remove <dir>`, `git branch -d <username>/<feature>`, and `git push origin --delete <username>/<feature>`. Leave other contributors' worktrees alone.
- **Concurrent-activity caution:** this repo can have concurrent worktree activity on `main`, so re-check `HEAD`/the tip immediately before any merge, amend, reset, or rebase, and stage only your own files so unrelated working-tree changes from other sessions aren't swept into your commit.

## Kusto safety

Kusto Slice Runner can write to Kusto through `.set-or-append`. Review every target cluster, database, function, output table, and permission before enabling scheduling or running Kusto commands. Prefer query validation patterns that avoid materializing large result sets: small limits, `| consume`, explicit timeouts, and saved artifacts when needed.
