# <img src="src/KoLite.LocalApp/wwwroot/favicon.svg" alt="KO Lite icon" width="32" height="32"> KO Lite

KO Lite is a local, develop-desktop system for scheduled Kusto set-or-append jobs. It is designed for an authenticated user or service identity that already has permission to execute the configured Kusto functions and append to the configured output tables.

The local app owns the job catalog, queue, slice history, operational logs, rerun reports, and repair state in SQLite. Kusto is contacted only when scheduler/worker execution is enabled and a worker executes a slice.

This is intended for non-production scenarios. For example, maybe you have a private dashboard that you want to schedule jobs for or you are working on a prototype that requires a bunch of backfilling to build up sample datasets. Those are great use cases for this tool. Down the road, it's plausible that the arch could be adjusted to be deployed to an Azure subscription. Feel free to contact me (23546948+benmartens@users.noreply.github.com) if you want to chat about that.

## Comparison with scheduled Kusto jobs

If you're familiar with [scheduled Kusto jobs](https://learn.microsoft.com/kusto/) here's a quick diff:
- The set of job features is simplified.
- Everything runs locally against a SQLite database. This is not meant for production scenarios.
- You can now add tags to your jobs and then filter them in the UI. This helps you handle multiple workstreams in a single instance.
- You can rerun slices! Click on any slice in the colorful window history view and then click "Rerun this slice" to get into that experience. This will properly handle dependent jobs too, but you'll need to make sure the Kusto tables are ready to accept the new data. KO Lite only reruns the jobs, it doesn't delete old data.
- You can both soft delete a job (keep the history to be resurrected in the future) or hard delete a job (permanently remove it and its history). Hard-delete avoids any problems around re-creating a job with the same id as a previous one.
- Pause immediately blocks any future scheduling from happening. This includes retry loops! So when you pause a job, it will continue any in-flight set-or-append command but if that fails, it won't retry. After you unpause, it will pick up where it left off in the retry logic.

## What it does

- Imports and exports strict schedule JSON for Kusto output jobs, including optional job organization tags.
- Shows active, completed, and soft-deleted jobs in a local dashboard.
- Visualizes job dependencies as a graph (colored by current job status) from a job's details page or by multi-selecting jobs on the dashboard and choosing "Dependencies". On demand, the graph can also resolve each job's Kusto lineage — the downstream functions/materialized views that consume its output, the upstream tables/functions it reads (including cross-cluster sources), and **implicit** (undeclared) dependencies where a job reads another KO job's output without declaring it.
- Schedules due time slices from enabled jobs into a local SQLite queue.
- Executes live Kusto `.set-or-append` commands for each claimed slice.
- Tracks queue state, slice history, attempts, logs, failures, and success-rate charts.
- Shows an **Activity** page (`/activity`) with how many slices are running right now and how many have been processed over time — a throughput chart plus succeeded vs. failed/dead-lettered totals for the last day, 7 days, 30 days, and all time.
- Bounds local database growth: a retention service prunes old operational telemetry (logs, terminal queue rows, old attempts) on a schedule while preserving the full slice window-history, so reruns and scheduling stay intact.
- Detects Kusto ingestion-capacity throttling (429), shows how bad it is (the % of attempts throttled, with a trend chart), highlights slices lost to throttling, and recommends per-job `maxParallelism` reductions that never starve a job below the parallelism it needs to keep up (and only trim a backfilling job to what still clears its backlog in time); operators apply them explicitly.
- Plans historical reruns and local state repair while leaving destructive Kusto cleanup to the operator.
- Exposes a localhost-only JSON API so a same-machine agent can read jobs and create/update schedules through the same validated import path the dashboard uses.
- Periodically checks GitHub (via the `gh` CLI) for newer KO Lite commits and shows an update badge in the top bar.

## Quick start

From the repository root:

```powershell
npm ci

dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj
```

Open `http://127.0.0.1:5057` and check `http://127.0.0.1:5057/status/health`

`/status/health` reports the in-use `databasePath`; `scripts\Get-KoLiteDatabase.ps1` prints it directly (and makes a best-effort guess when the app is stopped).

You should be able to kill it at any point and it will restart without duplicating data (thanks to ingest-by tags) but to avoid any chance of issues, execute scripts\Stop-KoLiteApp.ps1. It will wait for the workers to drain and then shut down gracefully.

## Run from a deployed copy

Running `dotnet run` from the repository locks the build output, so `dotnet build` and `dotnet test` fail while the app is running. To keep the repository free for build/test, deploy the latest build to an isolated folder and run it from there:

```powershell
.\scripts\Publish-KoLiteApp.ps1            # publish (Release) to %LOCALAPPDATA%\KoLite\run-app and copy the start/stop scripts in
cd "$env:LOCALAPPDATA\KoLite\run-app"
.\Start-KoLiteApp.ps1                      # run the deployed copy (Ctrl+C to stop)
```

`Publish-KoLiteApp.ps1` prints the full deployed path when it finishes. Use `-OutputDirectory` to deploy elsewhere and `-StopRunning` to gracefully drain a running instance before re-publishing. See the [operations runbook](docs/operations-runbook.md#published-output) for the full options.

## Screenshots

Job overview:

![KO Lite job overview dashboard](docs/images/job-overview.png)

Job detail:

![KO Lite job detail page](docs/images/job-detail.png)

## Documentation

| Topic | Link |
| --- | --- |
| Architecture and component responsibilities | [Local-first architecture](docs/local-first-architecture.md) |
| Schedule JSON contract and import/export behavior | [Schedule JSON](docs/schedule-json.md) |
| Localhost API for agent-driven job management | [Local management API](docs/local-api.md) |
| Safe local runs, configuration, diagnostics, and reruns | [Operations runbook](docs/operations-runbook.md) |
| Repository layout, restore, build, and test commands | [Development guide](DEVELOPMENT.md) |
| Standalone repo validation checklist | [Release readiness checklist](docs/release-readiness-checklist.md) |

## Support and security

See [SUPPORT.md](SUPPORT.md), [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
