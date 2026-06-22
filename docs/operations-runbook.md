# KO Lite operations runbook

This runbook covers safe local operation for the internal standalone KO Lite repo.

## Safe local review

Run with scheduler dispatch disabled when inspecting the UI or reviewing imported jobs:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-review.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057/status/health` and confirm the database path, scheduler settings, Kusto auth mode, shutdown state, and worker-pool snapshot.

If port `5057` is busy, add an explicit URL:

```powershell
--KoLite:Urls=http://127.0.0.1:5058
```

## Live local execution

Before enabling scheduler dispatch:

1. Confirm the Azure CLI user or managed identity has the intended Kusto permissions.
2. Confirm every enabled job points to the intended cluster, database, function, and output table.
3. Confirm `queryWindowSize`, `delayFromUtcNow`, `startFrom`, and `maxParallelism` are safe for the target workload.
4. Keep unreviewed jobs paused.

Then run with scheduler dispatch enabled:

```powershell
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=true --KoLite:Kusto:AuthMode=AzureCli
```

## Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `ConnectionStrings:KoLiteSqlite` | Empty | Preferred explicit local SQLite path. |
| `KoLite:DatabasePath` | `%LOCALAPPDATA%\KoLite\ko-lite.db` | Fallback database path when no connection string is supplied. |
| `KoLite:Urls` | `http://127.0.0.1:5057` | Local bind URL. |
| `KoLite:Scheduler:Enabled` | `true` | Disable for UI-only or safe first-run review. |
| `KoLite:Scheduler:TickInterval` | `00:00:10` | Scheduler cadence. Must be greater than zero. |
| `KoLite:Scheduler:LogEveryPass` | `false` | Writes durable scheduler/worker diagnostic rows when enabled. |
| `KoLite:WorkerPool:MaxConcurrency` | `Unbounded` | Global worker-pool concurrency cap. Unbounded by default so total concurrency equals the sum of each job's `maxParallelism` (enforced per job at claim time); set a positive integer to impose a global cap. |
| `KoLite:WorkerPool:IdleDelay` | `00:00:00.250` | Delay between idle dispatcher cycles. |
| `KoLite:WorkerPool:MaxDispatchStartsPerCycle` | `100` | Per-cycle dispatch start cap. |
| `KoLite:Kusto:AuthMode` | `AzureCli` | Supported values: `AzureCli`, `ManagedIdentity`. |
| `KoLite:Kusto:ManagedIdentityClientId` | Empty | Optional user-assigned managed identity client ID. |
| `KoLite:UpdateCheck:Enabled` | `true` | Periodically checks GitHub for newer KO Lite commits. Set `false` to disable. |
| `KoLite:UpdateCheck:Interval` | `01:00:00` | How often to poll GitHub. Must be greater than zero. |
| `KoLite:UpdateCheck:Repository` | `microsoft/kusto-slice-runner` | `owner/repo` to compare against. |
| `KoLite:UpdateCheck:Branch` | `main` | Branch whose HEAD is compared to the running build. |

Compatibility aliases `KoLite:Scheduler:WorkerConcurrency` and `KoLite:Scheduler:MaxWorkerIterations` are still accepted by the worker-pool options.

### Console log verbosity

Console verbosity uses per-category log-level filters. The default level is `Warning`, which keeps framework and host `info:` lines (for example `Microsoft.Hosting.Lifetime` "Now listening on…" / "Application started") out of the console. The `KoLite.LocalApp` category is raised to `Information`, so KO Lite's own progress lines — scheduler enqueue, per-slice worker start/finish, graceful-drain completion, and update-check transitions — remain visible. Warnings, errors, and dead-letter lines always remain visible. The two lower-value worker-dispatcher lines (dispatcher start and in-flight cancellation during shutdown) are emitted at `Debug`, so they stay quiet even at `Information`. To see everything, raise the level — for example set `Logging:LogLevel:Default` to `Debug` or `KoLite.LocalApp` to `Debug` in `appsettings.json`, or pass `--Logging:LogLevel:KoLite.LocalApp=Debug`. Durable scheduler/worker diagnostic rows are still controlled separately by `KoLite:Scheduler:LogEveryPass`.

## Update checks

KO Lite stamps the git commit it was built from into the app at build time and, when
update checks are enabled, periodically compares that commit against the remote branch
HEAD. The check shells out to the **GitHub CLI (`gh`)**, so it reuses whatever GitHub
account is already signed in on the machine — no token configuration is required.

A status badge on the right of the top bar shows one of:

- **Up to date** — the running build matches the remote branch HEAD.
- **Update available** — the remote branch is ahead; the badge popover links to the GitHub
  compare view and shows how many commits behind the build is.
- **Ahead of published** — the build contains commits the remote branch does not (you are
  developing ahead of the published branch, or the build commit hasn't been pushed yet).
- **Diverged** — both the build and the remote branch have commits the other lacks.
- **Updates: unavailable** — the check could not run. Opening the badge shows targeted
  guidance for the cause, for example:
  - GitHub CLI not installed → install `gh` and restart KO Lite.
  - Not signed in → run `gh auth login` for github.com.
  - No access to the repository → request access to `microsoft/kusto-slice-runner`.
  - The build was produced without a git checkout, so it carries no commit to compare.

Failures are non-fatal and never affect scheduling or Kusto execution. Full detail
(status, reason, built/remote SHA, commits-behind, last-checked time, and any error) is
also exposed under `updateCheck` in `/status/health`.

## Job catalog import and export

Use **Import** to add or update jobs from schedule JSON. Imports accept either one schedule object or an array of schedule objects through paste or file upload. Optional schedule `tags` are preserved as local job organization metadata and can be used to filter the dashboard and catalog.

Imports are additive and update-only: jobs with matching `activityId` values are updated, missing jobs are created, and jobs omitted from the payload are left untouched.

Use **Export all** on the home dashboard to export an import-compatible JSON array for every non-soft-deleted job in the local catalog. Individual job rows and job details pages also include single-job export links. Multi-job exports are sorted in ascending `activityId` (job id) order, so the output is deterministic and diff-stable.

After a job has execution history, `activityId`, `queryWindowSize`, and `startFrom` are read-only. The edit page marks those fields read-only, and the backend rejects raw JSON or import payloads that try to change them for a started job.

See [schedule-json.md](schedule-json.md) for the schedule contract.

## Local management API

KO Lite hosts a localhost-only JSON API so a same-machine agent or tool can read jobs and create/update schedules without using the dashboard. It starts and stops with the app. Every write goes through the same validated, additive/update-only import path as the dashboard, and the API exposes no enable/disable, delete, Kusto, rerun, or repair surface. Reads are `GET /api/jobs`, `GET /api/jobs/{jobId}`, and `GET /api/jobs/export`; writes are `POST /api/jobs/import`. All `/api` routes are loopback-only. See [local-api.md](local-api.md) for the full contract and the `ko-lite-job-manager` skill that drives it.

## Bulk actions on the dashboard

The home dashboard supports multi-select bulk actions on the **Active jobs** and **Completed jobs** sections. Use the per-row checkboxes or a section's header checkbox to select jobs; a contextual action bar appears only while at least one job is selected and offers **Pause**, **Resume**, **Soft delete**, and **Export** for the current selection. The Soft-deleted section is not selectable. Selection respects the dashboard text filter — filtered-out rows are excluded — and collapsing the Inactive jobs group keeps the current selection.

Bulk Pause/Resume/Soft delete apply with no extra confirmation (soft delete is reversible from the Soft-deleted section via **Restore**). They honor the same optimistic-concurrency model as the single-row actions: each selected row carries the catalog version shown on the page, and any job that changed since the page loaded — or is already in the requested state, soft-deleted, or missing — is **skipped** rather than forced. After the action, a summary banner reports how many jobs changed and how many were skipped. Bulk **Export** downloads an import-compatible JSON array (`ko-lite-jobs.json`) containing only the selected jobs, sorted in ascending `activityId` (job id) order.

## Published output

Publish to an isolated local folder when you want to run from compiled output instead of `dotnet run`:

```powershell
$publishDir = "$env:LOCALAPPDATA\KoLite\run-app"
dotnet publish .\src\KoLite.LocalApp\KoLite.LocalApp.csproj --configuration Release --output "$publishDir" --nologo
dotnet "$publishDir\KoLite.LocalApp.dll" --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Stop the running process before publishing again because published DLLs can be locked while the app is running.

This checkout does not include service install, publish helper, or diagnostics helper scripts. If service hosting is needed, publish first, use your service manager's normal process registration, and pass the same safety flags shown above.

## Catch-up estimate

The **Job details** page (`/jobs/{jobId}`) shows a catch-up estimate card above the tabs **only when the estimate is useful**: the job must be enabled and not paused, it must have a real eligible backlog (more than a couple of slices behind, so a job that just became eligible for its next slice is not flagged), and the projected catch-up time must be at least 30 minutes. A job running at its normal cadence shows no card.

- **Calculation.** The backlog is the eligible-but-incomplete slices (those whose window ends at or before `now - delayFromUtcNow`, capped by `endOn`) multiplied by `queryWindowSize`. The processing rate `R` (data-time completed per wall-clock time) is measured from recent successful slice completions, then the projected catch-up time is `backlog / (R - 1)` — the `- 1` accounts for "now" continuing to advance while the job works. The card reports the backlog, the recent rate (as a multiple of real time), and the estimated catch-up time and ETA. A job never catches up to the literal current time; it converges to its configured `delayFromUtcNow` lag.
- **Throughput window.** Only completions since the last schedule change are sampled (capped at the last 6 hours), so editing a job's definition does not skew the rate with executions that ran under the previous definition. Right after a change there may be too little data to estimate, in which case no card is shown until enough completions accumulate.
- **Not keeping up.** If the backlog is real but the recent rate is at or below real time (`R ≤ 1`), the card switches to a **Not keeping up** warning instead of an ETA — at the current rate the job will not catch up, so investigate failures, throughput, or `maxParallelism`.

## Diagnostics

Use `/status/health` to confirm the database path, scheduler options, Kusto auth mode, worker-pool state, and shutdown state.

Enable per-pass scheduler/worker diagnostics temporarily with:

```powershell
--KoLite:Scheduler:LogEveryPass=true
```

Inspect recent scheduler pass gaps with:

```sql
WITH scheduler_passes AS (
    SELECT
        recorded_at_utc,
        properties_json,
        LAG(recorded_at_utc) OVER (ORDER BY recorded_at_utc) AS previous_recorded_at_utc
    FROM operational_logs
    WHERE category = 'scheduler-pass'
)
SELECT
    recorded_at_utc,
    ROUND((julianday(recorded_at_utc) - julianday(previous_recorded_at_utc)) * 86400.0, 3) AS seconds_since_previous,
    json_extract(properties_json, '$.configuredTickInterval') AS configured_tick_interval,
    json_extract(properties_json, '$.durationMs') AS duration_ms,
    json_extract(properties_json, '$.enqueued') AS enqueued,
    json_extract(properties_json, '$.dependencyBlocked') AS dependency_blocked,
    json_extract(properties_json, '$.skippedMaxParallelism') AS skipped_max_parallelism
FROM scheduler_passes
ORDER BY recorded_at_utc DESC
LIMIT 30;
```

## Rerun and cleanup

From a slice detail page, use **Rerun this slice** to open the rerun planner. The planner also accepts a UTC start/end range and shows every root and downstream slice whose local state will be reset.

Rerun execution is intentionally two-step:

1. Review the affected slices and suggested Kusto cleanup commands. KO Lite suggests `.delete table ... records <|` commands that use `StartTime` and `EndTime`; edit them if a job's output table uses different columns.
2. After manually handling Kusto cleanup, acknowledge it on the rerun batch page. KO Lite snapshots old local state, attempts, logs, queue rows, and events into the rerun report, deletes the current local rows for those slices, and lets the normal scheduler pick the missing work back up.

Rerun is blocked while any affected slice is queued, leased, or running.

Back up the SQLite database before service upgrades, hard deletes, repair experiments, or large reruns.

## GUID identity upgrade (schema v6)

Schema v6 is a one-time, in-place re-key that makes each job's permanent identity an opaque GUID
(`job_definitions.job_id`) and turns `activityId` into a mutable, unique display label. No jobs are
deleted or recreated; existing rows are migrated in place when the app next opens the database.

Run the upgrade with the app **stopped and gracefully drained** so no slices are in-flight:

1. **Drain and stop** the running instance with `scripts\Stop-KoLiteApp.ps1` (graceful drain — lets
   active slices record final state). Do not hard-kill; an in-flight slice that ingested but did not
   complete could re-execute after the re-key.
2. **Back up** the SQLite file (copy it, or `VACUUM INTO` a dated copy).
3. **Rehearse first on a copy**: point a throwaway instance/connection at the backup copy and confirm
   the migration applies and the verification queries pass (per-table row counts unchanged except the
   re-keyed columns; no orphan foreign keys; no NULL `activity_id`).
4. **Apply** to the real database by starting the app (or running the migrator) once; verify again.
5. **Restart** KO Lite normally.

The upgrade is one-way: to roll back, restore the pre-migration backup.

**Kusto idempotency note.** The re-key changes the `ingestIfNotExists`/`ingest-by` tag basis from
`activityId` to the GUID. Output already in Kusto keeps its old activityId-based extent tag, which the
SQLite migration cannot rewrite. Draining before the upgrade removes the crash-retry path. The one
remaining case to avoid: a **repair with the `ExecuteNoCleanup` strategy on a slice that completed
before the upgrade** can double-ingest, because the new GUID-based tag will not match the old extent.
For such slices use `CleanSliceOutputThenExecute` or the rerun cleanup flow instead.

## Troubleshooting

- **Port in use:** add `--KoLite:Urls=http://127.0.0.1:5058`.
- **Unexpected live work:** restart with `--KoLite:Scheduler:Enabled=false`, pause jobs, or stop the local process and wait for active work to drain.
- **Kusto auth failures:** verify Azure CLI sign-in, managed identity settings, target cluster/database, and Kusto permissions.
- **Locked publish output:** stop the published app before republishing.
- **SQLite inspection:** use the database path shown by `/status/health`; runtime sidecar files such as `*.db-wal` and `*.db-shm` are local artifacts.
- **Local API unreachable:** the `/api/*` routes only exist while the app is running and only accept loopback callers; confirm the app is up via `/status/health` and use the loopback base URL.
- **Crash recovery:** long `queryTimeout` values also lengthen queue lease windows, so recovery after a hard crash can take longer for long-running jobs.
