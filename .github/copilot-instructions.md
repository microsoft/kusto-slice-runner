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
- `src\KoLite.Local.Sqlite` contains local SQLite persistence, migrations, queue, catalog, state, observability, repair, and rerun services.
- `src\KoLite.Local.Kusto` contains live Kusto request building, authentication, execution, and error classification.
- `src\KoLite.LocalApp` contains the ASP.NET Core/Razor dashboard, hosted scheduler and worker services, health/shutdown endpoints, UI read models, and static assets.
- `tests\KoLite.Local.*` mirrors the active solution with unit, integration, web, and local end-to-end tests.
- `scripts` contains `Stop-KoLiteApp.ps1` (graceful drain shutdown of the local app), `Get-KoLiteDatabase.ps1` (reports the in-use local SQLite database path), `copy-chartjs.mjs` (refreshes the bundled Chart.js assets), and `copy-cytoscape.mjs` (refreshes the bundled Cytoscape.js + cytoscape-dagre assets); the copy scripts run via `npm ci`.

## Key conventions

- Keep `.github\copilot-instructions.md` as the single repo-level Copilot instruction file. Do not add duplicate root-level instruction files.
- Prefer Windows paths and PowerShell examples. Existing scripts use `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`; preserve that style in new PowerShell scripts.
- Use the .NET SDK from `global.json`. When writing C#, use block-scoped namespace declarations (`namespace Name { ... }`) instead of file-scoped namespaces, keep app entry points inside an explicit `Program` class instead of top-level statements, and use conventional class declarations with explicit constructors instead of class primary constructors.
- Preserve local-first safety defaults. Do not enable live scheduling, widen destructive operations, weaken explicit confirmations, or execute Kusto cleanup/import actions unless the user explicitly asks for that outcome.
- Treat local SQLite files as durable user runtime state. Do not reset or delete them unless the user explicitly requests it and the existing code path requires confirmation.
- The local app should use the real Kusto output writer for user-facing execution. Test projects may use in-memory or fake executors where they already exist.
- Scheduler enqueue behavior and worker claim/execution behavior are separate. Preserve per-job `maxParallelism`, global worker concurrency, visibility/query timeout leases, pause/delete state, and dependency readiness semantics.
- Schedule JSON import/export accepts a single schedule object or an array. The parser rejects unknown top-level fields, unknown `target` fields, and unknown `dependsOn` entry fields. A job's permanent identity is an opaque GUID `id` (server-assigned, immutable); `activityId` is a mutable, unique display label. Imports match by `id` when present (enabling rename = same `id`, new `activityId`), else by `activityId`. `dependsOn` entries reference an upstream by `id` and/or `activityId` and are stored by GUID.
- For `.csl` and `.kql`, follow `.github\instructions\kusto.instructions.md`.

## Kusto safety

KO Lite can write to Kusto through `.set-or-append`. Review every target cluster, database, function, output table, and permission before enabling scheduling or running Kusto commands. Prefer query validation patterns that avoid materializing large result sets: small limits, `| consume`, explicit timeouts, and saved artifacts when needed.
