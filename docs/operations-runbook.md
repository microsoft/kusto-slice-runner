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
| `KoLite:Throttling:Enabled` | `true` | Surfaces the ingestion-throttling advisory page and dashboard banner. Detection/recording is always on; this only gates the advisory surface. |
| `KoLite:Throttling:WindowMinutes` | `20` | Rolling window for the sustained-throttle trigger. |
| `KoLite:Throttling:MinThrottledSlices` | `3` | Distinct throttled slices on a cluster within the window before an advisory is shown. |
| `KoLite:Throttling:DurationLookbackHours` | `6` | How far back successful slice durations are sampled for the keep-up floor. |
| `KoLite:Throttling:MinDurationSamples` | `5` | Minimum successful samples before a keep-up floor is estimated. |
| `KoLite:Throttling:DurationPercentile` | `0.75` | Percentile of successful slice durations used as the robust duration estimate. |
| `KoLite:Throttling:KeepUpSafetyFactor` | `1.5` | Margin above the bare keep-up parallelism (`1.0` = exactly keep up). |
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

Running `dotnet run` from the repository locks the build output, so `dotnet build` and `dotnet test` fail while the app is running. To keep the repository free for build/test, publish to an isolated folder and run from there.

Use the helper scripts (recommended):

```powershell
.\scripts\Publish-KoLiteApp.ps1            # dotnet publish (Release) to %LOCALAPPDATA%\KoLite\run-app,
                                           # then copy Start-/Stop-KoLiteApp.ps1 into that folder
cd "$env:LOCALAPPDATA\KoLite\run-app"
.\Start-KoLiteApp.ps1                      # run the deployed copy in the foreground (Ctrl+C to stop)
```

`Publish-KoLiteApp.ps1` prints the full deployed path when it finishes. Pass `-OutputDirectory` to deploy elsewhere, `-Clean` to clear the target first, and `-StopRunning` to gracefully drain a running instance (via `Stop-KoLiteApp.ps1`) before re-publishing — a published DLL cannot be overwritten while an instance is running from the same folder.

`Start-KoLiteApp.ps1` runs with no extra flags by default, matching a no-parameters run (scheduler enabled/live, Kusto `AzureCli`, default database `%LOCALAPPDATA%\KoLite\ko-lite.db`). Pass overrides through `-AppArguments`, for example a disposable database with the scheduler disabled:

```powershell
.\Start-KoLiteApp.ps1 -AppArguments '--ConnectionStrings:KoLiteSqlite=...','--KoLite:Scheduler:Enabled=false','--KoLite:Kusto:AuthMode=AzureCli'
```

The equivalent manual commands are:

```powershell
$publishDir = "$env:LOCALAPPDATA\KoLite\run-app"
dotnet publish .\src\KoLite.LocalApp\KoLite.LocalApp.csproj --configuration Release --output "$publishDir" --nologo
dotnet "$publishDir\KoLite.LocalApp.dll" --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Stop the running process before publishing again because published DLLs can be locked while the app is running. If service hosting is needed, publish first, use your service manager's normal process registration, and pass the same safety flags shown above.

## Catch-up estimate

The **Job details** page (`/jobs/{jobId}`) shows a catch-up estimate card above the tabs **only when the estimate is useful**: the job must be enabled and not paused, and it must have a real *actionable* backlog — more than a couple of eligible-but-incomplete slices that the job can work off itself. Slices that are merely waiting on an upstream dependency are excluded, so a dependent job that is only blocked on upstream (for its most recent slices) shows no card. Once a real backlog exists the card stays visible until it is actually worked off; it is **not** hidden as the job nears its frontier. When there is a real backlog but not yet enough fresh data to project a rate (typically right after a definition change), the card stays visible in a **collecting data** state. A job running at its normal cadence, or one whose only lag is upstream-blocked, shows no card.

- **Calculation.** The backlog is the eligible-but-incomplete slices (those whose window ends at or before `now - delayFromUtcNow`, capped by `endOn`), **minus any slices currently blocked on an upstream dependency**, multiplied by `queryWindowSize`. The processing rate `R` (data-time completed per wall-clock time) is measured from recent successful slice completions, then the projected catch-up time is `backlog / (R - 1)` — the `- 1` accounts for "now" continuing to advance while the job works. The card reports four metrics — **slices behind**, **data-time behind**, the **average processing rate** (slices per hour, also shown as a multiple of real time), and the **estimated time remaining** — plus the absolute ETA and the completed-through frontier. When recent slices are excluded because they are upstream-blocked, the note says how many. A job never catches up to the literal current time; it converges to its configured `delayFromUtcNow` lag.
- **Throughput window.** Only completions since the last schedule change are sampled (capped at the last 6 hours), so editing a job's definition does not skew the rate with executions that ran under the previous definition. Right after a change there is briefly too little data to project a rate. Instead of hiding the card, KO Lite keeps the **Catching up** card in a *collecting data* state that still shows the current backlog and the sample gathered so far (for example "1 of 3 completions"), and explains that an estimate appears once at least `MinThroughputSamples` (default 3) slices complete successfully over at least `MinThroughputSpan` (default 10 minutes). When the short sample is caused by a recent definition change the note says so; otherwise it gives a generic "collecting recent data" explanation.
- **Not keeping up.** If the backlog is real but the recent rate is at or below real time (`R ≤ 1`), the card switches to a **Not keeping up** warning instead of an ETA — at the current rate the job will not catch up, so investigate failures, throughput, or `maxParallelism`.

## Ingestion throttling advisor

When a slice fails because Kusto throttled its `.set-or-append` against the cluster's **ingestion capacity policy** (HTTP 429, `Origin: 'CapacityPolicy/Ingestion'`, `CommandType: 'TableSetOrAppend'`), KO Lite records the event and — once throttling is *sustained* — surfaces advisory `maxParallelism` reductions on the **Throttling** page (`/throttling`). The dashboard shows a banner linking there while any cluster is throttled. The advisor is read-only: it only recommends, and nothing changes until an operator clicks **Reduce to N**.

- **Detection.** Every retryable/dead-lettered slice whose error is an ingestion-capacity throttle is recorded as an observation (cluster, slice, attempt, reported capacity, time) in `ingestion_throttle_observations`. Recording is best-effort and isolated, so it never destabilizes a worker, and it is independent of the advisory surface. Other 429s (query/export capacity, or a workload group's request-rate-limit policy) are intentionally **not** treated as ingestion throttles.
- **Sustained trigger.** A cluster is surfaced only after at least `MinThrottledSlices` (default 3) *distinct* throttled slices within the last `WindowMinutes` (default 20). Counting distinct slices means retries of a single hot slice do not look like broad cluster pressure, and a single self-healing 429 is ignored.
- **Keep-up floor (the safety check).** For each active job the advisor estimates `minParallelism = max(1, ceil((D / W) * KeepUpSafetyFactor))`, where `W` is `queryWindowSize` and `D` is a robust recent **successful** slice duration (the `DurationPercentile`, default p75, over `DurationLookbackHours`). Successful-only sampling keeps retry backoff from inflating the floor. A recommendation never drops a job below this floor — and the apply action re-checks it server-side and refuses when it cannot be verified — so a job can always keep up with real time.
- **Ranking and targeting.** The advisor lists the enabled jobs that are active on the throttled cluster (currently in-flight, or themselves throttled in the window) and ranks them by **headroom** = current `maxParallelism` − keep-up floor, most over-provisioned first. The throttled slice's own job is *not* assumed to be the culprit; jobs already at/below their floor are shown but not offered a reduction, and jobs without enough samples show **Insufficient data**.
- **Applying.** **Reduce to N** updates only that job's `maxParallelism` through the normal validated catalog update path (audited as `throttle-advisor`, recorded as a definition change). Because it is a definition change, it resets the job's catch-up throughput window briefly. Concurrent edits are detected via optimistic concurrency and reported so you can re-read.
- **Shared-cluster caveat.** Ingestion capacity is **cluster-wide** and shared across every KO Lite job *and every other tool/user* on that cluster. KO Lite cannot see non-KO-Lite load, so trimming KO Lite may not clear throttling if external load dominates. When throttling persists, also consider the cluster's ingestion **capacity policy** (`.show cluster policy capacity` / `.alter-merge cluster policy capacity`) or scaling the cluster out/up — see the throttling note on the page.

## Diagnostics

Run `.\scripts\Get-KoLiteDatabase.ps1` to print the in-use SQLite database path. While the app is running it reports the authoritative `databasePath` from `/status/health`; while the app is stopped it reports the default and flags the most likely live file (ignoring backup/copy files and `*.db-wal` / `*.db-shm` sidecars). Pass `-BaseUrl` for a non-default endpoint. `ko-lite.db` is only the default when no connection string is supplied — `/status/health` is the source of truth for the running instance, and the app also logs the resolved path at startup.

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

## Orphaned leases and recovery

A slice is **leased** while a worker holds it. If that worker faults or its process is killed after
taking the lease, the row can stay `Leased`/`Running` with an already-expired lock — an **orphaned
lease**. The output may already be in Kusto (the `.set-or-append` ran) even though the slice never
recorded a terminal result.

How KO Lite handles this:

- **Bounded execution.** Each attempt has a client-side execution deadline (`queryTimeout` plus a
  small buffer), kept below the lease duration. A hung Kusto call is cancelled, released, and retried
  instead of holding the lease open indefinitely.
- **Prompt release on fault.** An attempt that throws (or times out) immediately abandons/retries the
  queue item rather than waiting for the lease to expire. Re-runs are idempotent (`ingest-by`), so
  recovery never duplicates output.
- **Automatic recovery.** Workers reclaim an expired lease on the next dispatch cycle once it has been
  expired by a short grace margin — recovery no longer waits for the worker pool to be fully idle.
- **Paused/soft-deleted jobs are not auto-recovered.** Consistent with pause semantics ("in-flight
  finishes, failures are not retried"), an orphaned lease on a disabled job stays put and is shown as
  **Stalled (orphaned lease)** in the window history. Resume the job to let it recover.
- **Manual recovery.** On the slice detail page, an orphaned slice on an **enabled** job offers
  **Recover (re-queue) this slice**, which resets the slice to `Queued` for an idempotent re-run.

Find orphaned (expired-lease) queue rows with:

```sql
SELECT job_id, slice_start_utc, slice_end_utc, locked_by, locked_until_utc, attempts, max_attempts
FROM work_queue
WHERE state = 'Leased' AND locked_until_utc <= strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
ORDER BY slice_start_utc;
```

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
- **Crash recovery:** long `queryTimeout` values also lengthen queue lease windows, so recovery after a hard crash can take longer for long-running jobs. A slice stuck as **Stalled (orphaned lease)** is recovered automatically on the next dispatch once its lease expires (for enabled jobs); resume a paused job, or use **Recover (re-queue) this slice** on the slice detail page, to recover it sooner. See [Orphaned leases and recovery](#orphaned-leases-and-recovery).
