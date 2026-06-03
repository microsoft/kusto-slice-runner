# KO Lite

KO Lite is an internal local-first dashboard and worker for running scheduled Kusto output jobs from a local SQLite catalog. It is intended to be run by an authenticated user or service identity that already has the required Kusto permissions.

The app is intentionally local-first: schedule definitions, queue state, slice history, operational logs, rerun reports, and repair state live in a local SQLite database. Kusto execution is live when scheduler/worker execution is enabled; there is no user-facing fake/offline executor in the local app.

## Safety first

- KO Lite writes to Kusto through `.set-or-append` using the configured schedule target, output table, function, and time slice.
- The scheduler and worker services are registered in the web app. Enabled jobs can enqueue and execute work unless scheduler dispatch is disabled.
- For a first run or UI-only review, start with `--KoLite:Scheduler:Enabled=false` or use `scripts\Start-KoLitePublishedUi.ps1`.
- Sample/new job JSON should stay paused until the target cluster, database, function, output table, and permissions have been reviewed.
- Rerun cleanup is intentionally manual. KO Lite suggests Kusto cleanup commands but does not execute them automatically.

## Project layout

| Path | Purpose |
| --- | --- |
| `KoLite.Local.sln` | Standalone local-first solution. |
| `src\KoLite.Local.Core` | Schedule parsing, scheduling models, mutation policy, dependency readiness, rerun/repair contracts. |
| `src\KoLite.Local.Sqlite` | Local SQLite persistence, migrations, queue, catalog, state, observability, repair, and rerun services. |
| `src\KoLite.Local.Kusto` | Live Kusto request building, authentication, execution, and error classification. |
| `src\KoLite.LocalApp` | Razor Pages dashboard, local hosted scheduler/worker services, health/shutdown endpoints, and static assets. |
| `tests\KoLite.Local.*` | Unit, integration, web, and local end-to-end tests for the active solution. |
| `scripts` | Publish, run, service-install, shutdown, diagnostics, and crash-recovery helpers. |
| `docs` | Architecture, schedule contract, operations, and release-readiness notes. |

## Documentation

- `docs\local-first-architecture.md` explains the local-first architecture and component responsibilities.
- `docs\schedule-json.md` documents the supported schedule JSON contract and import/export behavior.
- `docs\operations-runbook.md` covers safe local runs, publish/service scripts, health checks, shutdown, rerun, and diagnostics.
- `docs\release-readiness-checklist.md` lists the checks to run before copying this folder into a standalone internal repo.
- `docs\schedule-tags-implementation-plan.md` is a backlog proposal, not current product documentation.

## Prerequisites

- .NET SDK `10.0.300` or later feature band compatible with `global.json`.
- Node.js and npm for restoring/copying Chart.js assets with `npm ci`.
- Azure CLI sign-in for the default Kusto auth mode, or a managed identity for service-style runs.
- Kusto permissions to execute the configured function and append to the configured output table.

## Restore, build, and test

From the standalone repo root:

```powershell
npm ci
dotnet restore .\KoLite.Local.sln
dotnet build .\KoLite.Local.sln --no-restore --nologo
dotnet test .\KoLite.Local.sln --no-build --nologo
dotnet format .\KoLite.Local.sln --verify-no-changes --no-restore --verbosity minimal
```

`npm ci` restores Chart.js and runs the `postinstall` copy step for `src\KoLite.LocalApp\wwwroot\lib\chartjs`. The generated static assets are intentionally tracked so the .NET app can build and test without npm during normal development, but `npm ci` remains the source-of-truth refresh path.

## Safe first run

Start the website against an explicit local SQLite database with scheduler dispatch disabled:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-getting-started.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057` and check `http://127.0.0.1:5057/status/health`. If port `5057` is busy, add `--KoLite:Urls=http://127.0.0.1:5058` and use that URL instead.

When you are ready for live scheduling, review the jobs, keep only intentional jobs enabled, and restart with scheduler dispatch enabled:

```powershell
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=true --KoLite:Kusto:AuthMode=AzureCli
```

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:KoLiteSqlite` | Empty | Preferred explicit local SQLite path. |
| `KoLite:DatabasePath` | `%LOCALAPPDATA%\KoLite\ko-lite.db` | Fallback database path when no connection string is supplied. |
| `KoLite:Urls` | `http://127.0.0.1:5057` | Local bind URL. |
| `KoLite:Scheduler:Enabled` | `true` | Disable for UI-only/safe first-run review. |
| `KoLite:Scheduler:TickInterval` | `00:00:10` | Scheduler cadence. Must be greater than zero. |
| `KoLite:Scheduler:LogEveryPass` | `false` | Writes durable scheduler/worker diagnostic rows when enabled. |
| `KoLite:WorkerPool:MaxConcurrency` | `10` | Fixed local worker pool concurrency. |
| `KoLite:WorkerPool:IdleDelay` | `00:00:00.250` | Delay between idle dispatcher cycles. |
| `KoLite:WorkerPool:MaxDispatchStartsPerCycle` | `100` | Per-cycle dispatch start cap. |
| `KoLite:Kusto:AuthMode` | `AzureCli` | Supported values: `AzureCli`, `ManagedIdentity`. |
| `KoLite:Kusto:ManagedIdentityClientId` | Empty | Optional user-assigned managed identity client ID. |

Compatibility aliases `KoLite:Scheduler:WorkerConcurrency` and `KoLite:Scheduler:MaxWorkerIterations` are still accepted by the worker-pool options.

## Job catalog import and export

Use **Manage job catalog** -> **Import** to add or update jobs from schedule JSON. Imports accept either one schedule object or an array of schedule objects through paste or file upload.

Imports are additive and update-only: jobs with matching `activityId` values are updated, missing jobs are created, and jobs omitted from the payload are left untouched.

Use **Export all** on the home dashboard to export an import-compatible JSON array for every non-soft-deleted job in the local catalog. Individual job rows and job details pages also include single-job export links.

After a job has execution history, `activityId`, `queryWindowSize`, and `startFrom` are read-only. The edit page marks those fields read-only, and the backend rejects raw JSON or import payloads that try to change them for a started job.

See `docs\schedule-json.md` for the schedule contract.

## Running from published output while editing

Running with `dotnet run` can lock `bin` output on Windows. To keep the app running while editing and rebuilding, publish to an isolated local folder:

```powershell
.\scripts\Publish-KoLiteLocalApp.ps1
.\scripts\Start-KoLitePublishedApp.ps1 -SchedulerEnabled $false
```

For UI-only work without scheduler/worker dispatch:

```powershell
.\scripts\Start-KoLitePublishedUi.ps1
```

To request a graceful drain shutdown:

```powershell
.\scripts\Stop-KoLitePublishedApp.ps1
```

The publish/start scripts default to `%LOCALAPPDATA%\KoLite\run-app` and `%LOCALAPPDATA%\KoLite\ko-lite.db`. Stop the published app before publishing again because the published DLLs are locked while the app is running.

## Service-style deployment scripts

Operational scripts are dry-run safe by default:

```powershell
.\scripts\Publish-KoLiteLocalApp.ps1 -Configuration Release
.\scripts\Install-KoLiteLocalService.ps1 -DryRun -DatabasePath "$env:LOCALAPPDATA\KoLite\ko-lite.db"
.\scripts\Test-KoLiteLocalDiagnostics.ps1 -DryRun -DatabasePath "$env:LOCALAPPDATA\KoLite\ko-lite.db"
```

`Install-KoLiteLocalService.ps1` defaults to the published app at `%LOCALAPPDATA%\KoLite\run-app\KoLite.LocalApp.dll`. Run `Publish-KoLiteLocalApp.ps1` first, or pass `-AppDllPath` explicitly. Even with `-Apply`, service start/stop remains manual and fake/offline execution is unavailable.

Back up the SQLite database before destructive lifecycle operations or service upgrades. Hard-delete requires the exact confirmation text in application code and only purges local SQLite state.

## Rerunning historical slices

From a slice detail page, use **Rerun this slice** to open the rerun planner. The planner also accepts a UTC start/end range and shows every root and downstream slice whose local state will be reset.

Rerun execution is intentionally two-step:

1. Review the affected slices and suggested Kusto cleanup commands. KO Lite suggests `.delete table ... records <|` commands that use `StartTime` and `EndTime`; edit them if a job's output table uses different columns.
2. After manually handling Kusto cleanup, acknowledge it on the rerun batch page. KO Lite snapshots old local state, attempts, logs, queue rows, and events into the rerun report, deletes the current local rows for those slices, and lets the normal scheduler pick the missing work back up.

Rerun is blocked while any affected slice is queued, leased, or running.

## Troubleshooting

- **Port in use:** add `--KoLite:Urls=http://127.0.0.1:5058`.
- **Unexpected live work:** restart with `--KoLite:Scheduler:Enabled=false`, pause jobs, or use `scripts\Stop-KoLitePublishedApp.ps1` for a graceful drain.
- **Kusto auth failures:** verify Azure CLI sign-in, managed identity settings, target cluster/database, and Kusto permissions.
- **Locked publish output:** stop the published app before republishing.
- **SQLite inspection:** use the database path shown by `/status/health`; runtime sidecar files such as `*.db-wal` and `*.db-shm` are local artifacts.

## Internal repo readiness

Before extracting or sharing, run the checklist in `docs\release-readiness-checklist.md`. Do not copy ignored `bin`, `obj`, `TestResults`, `.playwright-mcp`, SQLite, log, or publish artifacts into the standalone repo.
