# KO Lite

KO Lite is a local-first dashboard and worker for running scheduled Kusto output jobs from a local SQLite catalog. It is designed for an authenticated user or service identity that already has permission to execute the configured Kusto functions and append to the configured output tables.

The local app owns the job catalog, queue, slice history, operational logs, rerun reports, and repair state in SQLite. Kusto is contacted only when scheduler/worker execution is enabled and a worker executes a slice.

If you're familiar with [scheduled Kusto jobs](https://learn.microsoft.com/kusto/) here's a quick diff:
- The set of job features is simplified.
- Everything runs locally against a sqllite database. This is not meant for production scenarios.
- You can now add tags to your jobs and then filter them in the UI. This helps you handle multiple workstreams in a single instance.
- You can rerun slices! Click on any slice in the colorful window history view and then click "Rerun this slice" to get into that experience. This will properly handle dependent jobs too, but you'll need to make sure the Kusto tables are ready to accept the new data. KO Lite only reruns the jobs, it doesn't delete old data.
- You can both soft delete a job (keep the history to be resurrected in teh future) or hard delete a job (permanently remove it and its history). Hard-delete avoids any problems around re-creating a job with the same id as a previous one.

## What it does

- Imports and exports strict schedule JSON for Kusto output jobs, including optional job organization tags.
- Shows active, completed, and soft-deleted jobs in a local dashboard.
- Schedules due time slices from enabled jobs into a local SQLite queue.
- Executes live Kusto `.set-or-append` commands for each claimed slice.
- Tracks queue state, slice history, attempts, logs, failures, and success-rate charts.
- Plans historical reruns and local state repair while leaving destructive Kusto cleanup to the operator.

## Safety first

- KO Lite can write to Kusto through `.set-or-append`; review every target cluster, database, function, output table, and permission before enabling scheduling.
- Start with `--KoLite:Scheduler:Enabled=false` for UI review or first-run setup.
- Keep imported or sample jobs paused until they have been reviewed.
- Rerun cleanup is manual: KO Lite suggests Kusto cleanup commands but does not execute them.
- SQLite files are local runtime state. Back them up before destructive operations and do not commit `*.db`, `*.db-wal`, or `*.db-shm` files.

## Quick start

From the repository root:

```powershell
npm ci

$db = "$env:LOCALAPPDATA\KoLite\ko-lite-review.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057` and check `http://127.0.0.1:5057/status/health`. If port `5057` is busy, add `--KoLite:Urls=http://127.0.0.1:5058` and use that URL instead.

When you are ready for live scheduling, review the jobs and Kusto permissions, keep only intentional jobs enabled, and restart with `--KoLite:Scheduler:Enabled=true`.

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
| Safe local runs, configuration, diagnostics, and reruns | [Operations runbook](docs/operations-runbook.md) |
| Repository layout, restore, build, and test commands | [Development guide](DEVELOPMENT.md) |
| Standalone repo validation checklist | [Release readiness checklist](docs/release-readiness-checklist.md) |

## Support and security

See [SUPPORT.md](SUPPORT.md), [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
