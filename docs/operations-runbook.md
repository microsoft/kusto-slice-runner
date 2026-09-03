# KO Lite operations runbook

This runbook covers safe local operation for the internal standalone KO Lite repo.

## Safe local review

Run with scheduler dispatch disabled when inspecting the UI or reviewing imported jobs:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-review.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057/healthz`, then inspect `http://127.0.0.1:5057/api/v1/system/status` to confirm the database path, scheduler settings, Kusto auth mode, shutdown state, and worker-pool snapshot.

If port `5057` is busy, add an explicit URL:

```powershell
--KoLite:Urls=http://127.0.0.1:5058
```

To view the **live** database (`%LOCALAPPDATA%\KoLite\ko-lite.db`) while your app keeps running, start a second UI-only instance with `.\scripts\Start-KoLiteUi.ps1` (defaults to the live database on port 5099). A second instance on the same database is otherwise refused by the single-instance guard; the script bypasses it with `KoLite:AllowMultipleInstances=true` and keeps the scheduler, worker, and retention disabled so the viewer makes no background writes. Startup still applies the current schema to whatever database it opens, so pass `-UseCopy` when your branch changes the schema.

## Live local execution

Before enabling scheduler dispatch:

1. Confirm the Azure CLI user or managed identity has the intended Kusto permissions.
2. Confirm every enabled job points to the intended cluster, database, function, and output table.
3. Confirm `queryWindowSize`, `delayFromUtcNow`, `startFrom`, and `maxParallelism` are safe for the target workload.
4. Keep unreviewed jobs paused.
5. For chunked jobs, confirm the function accepts `chunkId:long` and `chunks:long` before optional `jobSettings:dynamic`, and confirm `chunks * query cost` is safe for the cluster.

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
| `KoLite:AllowMultipleInstances` | `false` | Bypasses the single-instance guard so a UI-only viewer can run alongside the live app against the same database. Only for that intentional case: keep `KoLite:Scheduler:Enabled=false` and `KoLite:Retention:Enabled=false`, and note startup still applies the current schema to whatever database it opens. See [Safe local review](#safe-local-review). |
| `KoLite:Scheduler:Enabled` | `true` | Disable for UI-only or safe first-run review. |
| `KoLite:Scheduler:TickInterval` | `00:00:10` | Scheduler cadence. Must be greater than zero. |
| `KoLite:Scheduler:LogEveryPass` | `false` | Writes durable scheduler/worker diagnostic rows when enabled. |
| `KoLite:WorkerPool:MaxConcurrency` | `Unbounded` | Global execution-unit concurrency cap across all jobs. Unbounded by default (`int.MaxValue` internally), so total concurrency is governed by the sum of each job's `maxParallelism`; set any positive integer to impose a global cap. There is no hard product maximum. |
| `KoLite:WorkerPool:IdleDelay` | `00:00:00.250` | Delay between idle dispatcher cycles. |
| `KoLite:WorkerPool:MaxDispatchStartsPerCycle` | `100` | Maximum execution units started in one dispatcher cycle. This controls start rate, not total in-flight concurrency; the default can launch all 32 chunks of one window in a cycle. |
| `KoLite:Kusto:AuthMode` | `AzureCli` | Supported values: `AzureCli`, `ManagedIdentity`. |
| `KoLite:Kusto:ManagedIdentityClientId` | Empty | Optional user-assigned managed identity client ID. |
| `KoLite:Throttling:Enabled` | `true` | Surfaces the ingestion-throttling advisory page and dashboard banner. Detection/recording is always on; this only gates the advisory surface. |
| `KoLite:Throttling:WindowMinutes` | `20` | Rolling window over which the throttled-attempt rate and distinct-slice count are measured. |
| `KoLite:Throttling:MinThrottledSlices` | `3` | Distinct throttled slices on a cluster within the window required before the rate-based advisory shows (a volume floor). |
| `KoLite:Throttling:RateThresholdPercent` | `5` | Throttled-attempt rate (throttled ÷ all attempts) over the window required to surface a cluster. |
| `KoLite:Throttling:MinAttemptsForRate` | `20` | Minimum total attempts in the window before the rate gate can trip (avoids a noisy percentage from a tiny denominator). |
| `KoLite:Throttling:CleanPeriodMinutes` | `15` | A surfaced cluster clears after this long with no new ingestion-throttle observation (hysteresis). |
| `KoLite:Throttling:TerminalFailureLookbackMinutes` | `60` | How far back a still-unresolved slice that dead-lettered on throttling forces the advisory/banner to show. |
| `KoLite:Throttling:CatchUpTargetHours` | `24` | Target time within which a backfilling job should clear its backlog; sizes its catch-up floor. |
| `KoLite:Throttling:DurationLookbackHours` | `6` | How far back successful slice durations are sampled for the keep-up floor. |
| `KoLite:Throttling:MinDurationSamples` | `5` | Minimum successful samples before a keep-up floor is estimated. |
| `KoLite:Throttling:DurationPercentile` | `0.75` | Percentile of successful slice durations used as the robust duration estimate. |
| `KoLite:Throttling:KeepUpSafetyFactor` | `1.5` | Margin above the bare keep-up parallelism (`1.0` = exactly keep up). |
| `KoLite:UpdateCheck:Enabled` | `true` | Periodically checks GitHub for a newer published KO Lite release. Set `false` to disable. |
| `KoLite:UpdateCheck:Interval` | `01:00:00` | How often to poll GitHub. Must be greater than zero. |
| `KoLite:UpdateCheck:Repository` | `microsoft/kusto-slice-runner` | `owner/repo` whose latest published release is compared to the running build. |
| `KoLite:Retention:Enabled` | `true` | Periodically prunes old operational telemetry so the local database stops growing without bound. Set `false` to disable (the database then grows unbounded). |
| `KoLite:Retention:WindowDays` | `30` | Operational telemetry older than this is eligible for pruning. Must be greater than zero. The slice window-history is never pruned. |
| `KoLite:Retention:Interval` | `06:00:00` | How often the retention pass runs. Must be greater than zero. |
| `KoLite:Retention:InitialDelay` | `00:02:00` | Delay after startup before the first retention pass. |
| `KoLite:Retention:BatchSize` | `2000` | Rows deleted per batch; each batch commits separately to keep write locks short on the live database. |

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
also exposed under `update` in `/api/v1/system/status`.

## Database growth and retention

The local SQLite database is the durable source of truth for the catalog, queue, slice
window-history, operational logs, and rerun/repair records. Left unmanaged it would grow without
bound, because every scheduled slice appends operational telemetry (logs, queue rows, attempts,
state events). A retention background service keeps that growth in check.

### What is pruned vs. preserved

On each pass (every `KoLite:Retention:Interval`, after an initial `KoLite:Retention:InitialDelay`),
KO Lite deletes **non-authoritative operational telemetry** older than `KoLite:Retention:WindowDays`:

- `operational_logs` — routine scheduler/worker log rows.
- `work_queue` — only **terminal** rows (`Completed`/`DeadLettered`); `Queued`/`Leased` rows are
  never pruned, so claimable and in-flight work is untouched.
- `slice_attempts` — per-attempt detail for finished slices (in-progress `Started` attempts are kept).
- `scheduled_slices` — the scheduling ledger.
- `ingestion_throttle_observations` — throttle samples.

It **never** touches the authoritative window-history or catalog state: `current_slice_state`,
`slice_state_events`, `job_definitions`, lifecycle/audit rows, and `rerun_*`/`repair_*` records are
always preserved.

### Impact on functionality

Because the scheduler, rerun, dependency readiness, and the started-job field guard all read the
preserved `current_slice_state`/`slice_state_events`, pruning telemetry does **not** affect any
functional capability:

- **Rerunning old slices still works.** Rerun eligibility reads `current_slice_state`, and the rerun
  reset rebuilds the slice's rows; only the captured pre-rerun snapshot is thinner for a slice whose
  telemetry has aged out.
- **Scheduler idempotency is intact.** A completed slice is never re-enqueued, because the scheduler
  decides from `current_slice_state`, not from queue or scheduled-slice rows.
- **The colored window-history view is unchanged.**

What you lose for data older than the window is **historical operational detail**: old log lines,
per-attempt rows on the slice-detail page, and chart depth. To keep the dashboard charts whole
(their maximum range is 30 days), the chart- and advisor-backing tables (`slice_attempts`,
`ingestion_throttle_observations`) are never pruned more aggressively than 30 days, even if a shorter
`WindowDays` is configured.

The latest retention outcome (enabled, window, interval, last-run time, and rows deleted) is exposed
under `retention` in `/api/v1/system/status`.

### Reclaiming file space (manual VACUUM)

Retention bounds growth, but SQLite does not return freed pages to the operating system on its own:
deleted pages are reused for future growth, so the file size plateaus rather than dropping. To
physically reclaim space after a large backlog has been pruned, stop the app and run a one-off
VACUUM:

```powershell
.\scripts\Invoke-KoLiteVacuum.ps1 -DryRun     # report the in-use database path and current size
.\scripts\Stop-KoLiteApp.ps1                  # VACUUM needs exclusive access
.\scripts\Invoke-KoLiteVacuum.ps1             # rewrite the database and report reclaimed space
```

VACUUM rewrites the whole database and needs free disk for a temporary copy. The script refuses to
run while the app is responding (pass `-Force` to override, not recommended) and never creates or
deletes a database file.

## Job catalog import and export

Use **Import** to add or update jobs from schedule JSON. Imports accept either one schedule object or an array of schedule objects through paste or file upload. Optional schedule `tags` are preserved as local job organization metadata and can be used to filter the dashboard and catalog.

Imports are additive and update-only: jobs with matching `activityId` values are updated, missing jobs are created, and jobs omitted from the payload are left untouched.

Use **Export all** on the home dashboard to export an import-compatible JSON array for every non-soft-deleted job in the local catalog. Individual job rows and job details pages also include single-job export links. Multi-job exports are sorted in ascending `activityId` (job id) order, so the output is deterministic and diff-stable.

After a job has execution history, `activityId`, `queryWindowSize`, and `startFrom` are read-only. The edit page marks those fields read-only, and the backend rejects raw JSON or import payloads that try to change them for a started job.

See [schedule-json.md](schedule-json.md) for the schedule contract.

### Chunked jobs

`chunks` is an optional integer from 1 through 32. It changes the Kusto function signature and is
therefore read-only after a job starts. Create chunked jobs paused, review the function and target,
then resume explicitly.

One colored history cell remains one logical time window. Open it to see child chunk state,
attempts, leases, queue rows, and errors. `maxParallelism` limits concurrent child executions.
Pausing lets in-flight chunks finish but prevents new chunks and retries from starting. Resume
continues the incomplete children without rerunning successful siblings.

Hover or focus a chunked job's slice-history cell to see **Chunks: completed/total** (for example,
`Chunks: 3/16`). Configured windows that have not materialized child rows yet show `0/total`.

`maxParallelism` counts execution units, not parent windows: each chunk consumes one slot, while
an unchunked slice consumes one slot. It has no upper limit beyond the minimum of 1. Full fan-out
of a 32-chunk window requires `maxParallelism >= 32` and at least 32 free global worker slots.
The global pool is unbounded by default; if `KoLite:WorkerPool:MaxConcurrency` is configured, all
jobs share that finite cap. `MaxDispatchStartsPerCycle` is separate and defaults to 100.

Repair requeues only failed/dead-lettered chunks and reuses their stable ingest-by identities.
Rerun still resets every chunk in the selected logical window because cleanup is time-window based.

Open a red slice and choose **Repair or rerun this slice**:

- **Repair failed chunks** is selected by default when terminal failed work exists. The preview names
  the raw 0-based chunk IDs and errors, excludes chunks with an automatic retry already queued or
  leased, and queues every remaining terminal failed chunk after confirmation. No Kusto cleanup is
  required; successful sibling chunks remain complete.
- **Rerun whole slice** resets every chunk in the logical window and affected downstream slices after
  you run/acknowledge the suggested time-window cleanup.
- **Recover expired lease** remains a separate stalled-worker operation. It is neither repair nor rerun.

Slice detail and localhost diagnostics expose the same chunk IDs in current state, child events,
attempts, durable logs, queue rows, recent failure summaries, and repair history.

## Local management API

KO Lite hosts a loopback-only `/api/v1` agent API with generated OpenAPI at `/api/v1/openapi/v1.json`. It provides first-class GUID-keyed job create/replace/pause/resume, ETag concurrency, batch import/export, safe soft-delete/restore, failed-work repair, cursor-paged operational reads, and opt-in read-only Kusto lineage. It never exposes hard delete, whole-slice rerun, Kusto cleanup, or arbitrary Kusto writes. See [local-api.md](local-api.md) for workflows and the generated document for exact schemas.

## Dashboard status model

Every job on the dashboard shows a compact, **color-only status pill** — two colored halves with no text, so it never truncates. It answers, at a glance, the only two questions that usually matter: **is something wrong right now?** and **is this job's history complete?** Hover either half for a plain-language explanation (each half also carries an `aria-label`); everything else lives on the job details page.

**Left half — recent health.** Derived from the outcomes of the job's most recent slice windows (the last 10 that have a recorded state), so old failures don't dominate a job that is healthy now:

- **Green — Healthy** — no failures among the recent slices. (**Waiting on upstream** — behind a healthy upstream — is also green.)
- **Amber — Warning** — some recent failures, but the job is not broken now (a minority of recent slices failed, or it is recovering). **Blocked (upstream)** — an upstream job is itself unhealthy/paused — is also amber.
- **Red — Attention** — broken now: the most recent resolved slice failed and at least half of the recent slices failed. A slice that is merely retry-pending does **not**, by itself, turn a job red — only a sustained failing pattern (or dead-lettering) does.
- **Grey** — **Paused** or **Completed** (intentional/terminal lifecycle states).

Running or queued work is noted in the health half's hover tooltip (the pill itself carries no separate indicator).

**Right half — historical completeness.** Controlled per job by `healthPolicy` (see [schedule-json.md](schedule-json.md)):

- `complete` (default, strict) — the job wants every slice eventually filled, so the right half is **red** when there are **unaddressed terminal gaps** (dead-lettered slices) and **green** when there are none. It is binary: any gap breaks the "every slice must pass" contract, so there is no in-between amber state. Gaps show even when recent health is green, so "working now but the backfill is incomplete" is unambiguous; hover the half for the count. Rerun or repair the dead-lettered slices to close the gaps.
- `recent` — the operator only cares about the recent trend, so there is no completeness half: the pill is a **single solid capsule** in the health color. Use this for jobs where backfilling the past is impossible or unnecessary.

Because old dead-lettered slices no longer force a broadly-healthy job to show red, a job that "did well over the last few days but failed a while back" now reads **green** (with a red completeness half under the strict policy) instead of a blanket red **Failed**. The dependency-graph node colors use the same recent-health tiers.

## Dependency graph

Open a job's **Dependencies** tab, or multi-select jobs on the dashboard and choose **Dependencies**, to see the job's full dependency chain (transitive upstream and downstream `dependsOn` edges) as a graph colored by current job status. This view is a pure, read-only projection of local state and contacts no Kusto.

The graph's **Resolve Kusto lineage** button additionally fetches, from Kusto, both directions of each charted job's data lineage. This is the only graph action that contacts Kusto:

- It runs a single read-only `.show databases entities with (resolveDependencies = true, resolveFunctionsSchema = true)` per distinct cluster in the chain, using the same Kusto auth (`KoLite:Kusto:AuthMode`) as live execution. It performs no writes and persists nothing — each click re-queries live.
- **Downstream consumers.** The non-job **functions and materialized views** that read each job's output table are added. Consumers that are themselves KO Lite jobs map onto the existing job node (that link is already shown); pass-through tables (e.g. update-policy targets) are bridged through but not drawn.
- **Upstream sources.** The tables/functions each job's function **directly reads** are added as source nodes — **including cross-cluster sources**, which are named directly in the function's dependencies (and labelled `@cluster`), so no second cluster is queried.
- **Implicit (undeclared) dependencies.** When a job's function reads **another KO job's output table that is not in its `dependsOn`**, a **dashed amber** edge is drawn (and noted in the legend). This is informational — it does **not** change scheduler readiness, which still uses the declared `dependsOn`. Consider adding the edge to `dependsOn` if the ordering matters.
- Scope is the charted jobs' own cluster(s); one call returns lineage across every accessible database on that cluster. A downstream consumer hosted on a *different* cluster than the job is not discovered (cross-cluster *sources*, named directly, are). Failures (auth, permission, unreachable cluster, timeout) surface as an inline message beside the button and leave the job graph intact.

## Bulk actions on the dashboard

The home dashboard supports multi-select bulk actions only in the **Active jobs** and **Completed jobs** sections. Use the per-row checkboxes or a section's header checkbox to select jobs; the contextual action bar offers **Pause**, **Resume**, **Soft delete**, and **Export**. The **Soft-deleted jobs** section is not selectable, so permanent deletion cannot be reached from the dashboard's normal bulk-action flow. Selection respects the dashboard text filter — filtered-out rows are excluded — and collapsing the Inactive jobs group keeps the current selection.

Bulk Pause/Resume/Soft delete apply with no extra confirmation (soft delete is reversible from the Soft-deleted section via **Restore**). They honor the same optimistic-concurrency model as the single-row actions: each selected row carries the catalog version shown on the page, and any job that changed since the page loaded — or is already in the requested state, soft-deleted, or missing — is **skipped** rather than forced. After the action, a summary banner reports how many jobs changed and how many were skipped. Bulk **Export** downloads an import-compatible JSON array (`ko-lite-jobs.json`) containing only the selected jobs, sorted in ascending `activityId` (job id) order.

Bulk **Hard delete** is intentionally isolated on **Manage soft-deleted jobs**, linked from the Soft-deleted jobs section header. That page contains only currently soft-deleted jobs and is the only surface with bulk hard-delete checkboxes. After selection it opens a separate review page that lists every selected activity ID and permanent GUID, explains which local rows will be removed, and requires the exact count phrase `DELETE 1 JOB` or `DELETE N JOBS`. Final submission revalidates every job's catalog version, soft-delete state, disabled state, and active leases/running slices inside one SQLite transaction. If any selected job is missing, changed, restored, enabled, or still executing, the entire batch is rejected and **no jobs are deleted**. A successful batch records one purge run and an audit event for each deleted job. Hard delete removes local KO Lite state only; it never deletes Kusto data.

## Soft-delete and downstream dependencies

Soft-deleting a job that other **active** (non-soft-deleted) jobs depend on would silently strand those downstream slices in a `DependencyBlocked` state, so soft delete now warns first:

- **Single soft delete** (dashboard row or job details). If the job has active downstream dependents, the Soft delete action redirects to `/jobs/{jobId}/soft-delete-confirm`, which lists each dependent job. From there you can **Soft delete anyway** or **Cancel**.
- **Bulk soft delete.** Any selected job with active downstream dependents is **skipped** (never force-deleted) and named in the summary banner alongside the dependents that need it. Force a specific blocked job from its own confirmation page if that is really what you want.

Dependents are matched by the upstream job's durable `id`; a dependent that is itself soft-deleted does not block, because it is not scheduling. Add or remove these edges with the dependency picker in the job editor (the **Job definition** tab on the details page, or the create/copy editors).

## Published output

Running `dotnet run` from the repository locks the build output, so `dotnet build` and `dotnet test` fail while the app is running. To keep the repository free for build/test, publish to an isolated folder and run from there.

Use the helper scripts (recommended):

```powershell
.\scripts\Publish-KoLiteApp.ps1            # dotnet publish (Release) to %LOCALAPPDATA%\KoLite\run-app,
                                           # then copy Start-/Stop-KoLiteApp.ps1 into that folder
.\scripts\Start-KoLiteApp.ps1              # run the deployed copy in the foreground (Ctrl+C to stop)
```

`Publish-KoLiteApp.ps1` prints the full deployed path when it finishes. Pass `-OutputDirectory` to deploy elsewhere, `-Clean` to clear the target first, and `-StopRunning` to gracefully drain a running instance (via `Stop-KoLiteApp.ps1`) before re-publishing — a published DLL cannot be overwritten while an instance is running from the same folder.

`Start-KoLiteApp.ps1` can be invoked from the repository or from inside the deployed folder. It changes the child process working directory to the deployed folder so ASP.NET Core can resolve the published `wwwroot` assets in either case. It runs with no extra flags by default, matching a no-parameters run (scheduler enabled/live, Kusto `AzureCli`, default database `%LOCALAPPDATA%\KoLite\ko-lite.db`). Pass overrides through `-AppArguments`, for example a disposable database with the scheduler disabled:

```powershell
.\Start-KoLiteApp.ps1 -AppArguments '--ConnectionStrings:KoLiteSqlite=...','--KoLite:Scheduler:Enabled=false','--KoLite:Kusto:AuthMode=AzureCli'
```

The same script is included in GitHub Release downloads. It prefers `KoLite.LocalApp.exe` in the self-contained Windows x64 package and falls back to `dotnet KoLite.LocalApp.dll` in the framework-dependent package. The framework-dependent package requires the .NET 10 runtime.

Release ZIPs are replaceable application files; the default durable SQLite database remains at `%LOCALAPPDATA%\KoLite\ko-lite.db`. Extract a new release to a new or cleaned application folder rather than copying it over a running version. Gracefully drain the old instance first, then start the new release with scheduling disabled to inspect `/healthz`, `/api/v1/system/status`, and the configured targets before enabling live scheduling.

The equivalent manual commands are:

```powershell
$publishDir = "$env:LOCALAPPDATA\KoLite\run-app"
dotnet publish .\src\KoLite.LocalApp\KoLite.LocalApp.csproj --configuration Release --output "$publishDir" --nologo
Push-Location $publishDir
try {
    dotnet .\KoLite.LocalApp.dll --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
} finally {
    Pop-Location
}
```

Stop the running process before publishing again because published DLLs can be locked while the app is running. If service hosting is needed, publish first, use your service manager's normal process registration, and pass the same safety flags shown above.

## Catch-up estimate

The **Job details** page (`/jobs/{jobId}`) shows a catch-up estimate card above the tabs **only when the estimate is useful**: the job must be enabled and not paused, and it must have a real *actionable* backlog — more than a couple of eligible-but-incomplete slices that the job can work off itself. Slices that are merely waiting on an upstream dependency are excluded, so a dependent job that is only blocked on upstream (for its most recent slices) shows no card. Once a real backlog exists the card stays visible until it is actually worked off; it is **not** hidden as the job nears its frontier. When there is a real backlog but not yet enough fresh data to project a rate (typically right after a definition change), the card stays visible in a **collecting data** state. A job running at its normal cadence, or one whose only lag is upstream-blocked, shows no card.

- **Calculation.** The backlog is the eligible-but-incomplete slices (those whose window ends at or before `now - delayFromUtcNow`, capped by `endOn`), **minus any slices currently blocked on an upstream dependency**, multiplied by `queryWindowSize`. The processing rate `R` (data-time completed per wall-clock time) is measured from recent successful slice completions, then the projected catch-up time is `backlog / (R - 1)` — the `- 1` accounts for "now" continuing to advance while the job works. The card reports four metrics — **slices behind**, **data-time behind**, the **average processing rate** (slices per hour, also shown as a multiple of real time), and the **estimated time remaining** — plus the absolute ETA and the completed-through frontier. When recent slices are excluded because they are upstream-blocked, the note says how many. A job never catches up to the literal current time; it converges to its configured `delayFromUtcNow` lag.
- **Throughput window.** Only completions since the last schedule change are sampled (capped at the last 6 hours), so editing a job's definition does not skew the rate with executions that ran under the previous definition. Right after a change there is briefly too little data to project a rate. Instead of hiding the card, KO Lite keeps the **Catching up** card in a *collecting data* state that still shows the current backlog (slices and data-time behind) plus a two-item **requirements checklist** beneath the explanation. The checklist tracks the two gates an estimate waits on, each with a green checkmark once it clears: **successful completions** (the sample gathered so far versus the required count, for example "2 of 3 completions") and **time collected** (the span between the first and last sampled completion versus the required minimum, with the remaining time shown while pending, for example "~4 min of ~10 min (~6 min to go)"). An estimate appears only once **both** clear — at least `MinThroughputSamples` (default 3) successful completions spanning at least `MinThroughputSpan` (default 10 minutes) — at which point the message and checklist are replaced by the projection. When the short sample is caused by a recent definition change the note says so; otherwise it gives a generic "collecting recent data" explanation.
- **Not keeping up.** If the backlog is real but the recent rate is at or below real time (`R ≤ 1`), the card switches to a **Not keeping up** warning instead of an ETA — at the current rate the job will not catch up, so investigate failures, throughput, or `maxParallelism`.

## Activity page

The **Activity** page (`/activity`, linked in the top nav) answers "how much work is flowing through
KO Lite right now and over time?". It distinguishes a logical **slice** (one time window) from an
**execution unit** (one chunk for a chunked job, or the slice itself for an unchunked job).

- **Running now counts.** **Logical slices running/queued** count parent time windows, so four running
  chunks in one window contribute one running logical slice. **Executions running** counts active
  chunks plus active unchunked slices. **Executions queued/retry-pending** counts durable queued work
  rows; a paused job's queued work remains counted even though workers will not claim it. Missing
  chunks that have not been released to the queue are not queued executions.
- **Running logical-slice table.** The table stays at one row per running logical window. A chunked row
  shows completed/total progress, exceptional child-state badges, and every running raw 0-based chunk
  ID with its worker. An expired child lease is flagged inline. **Highest attempt** is the maximum
  child attempt. **Started** is the earliest active child start (or the parent state-change fallback
  when attempt detail is unavailable); an unchunked row continues to show its single execution and
  worker. The table is capped at 100 logical rows, while headline counts remain exact.
- **ETA.** ETA is *earliest active start + the median of that job's recent successful whole-window
  durations*. A whole-window sample runs from its earliest attempt start, including automatic retry
  time, until every configured chunk succeeds. Manually repaired windows are excluded because
  operator delay would distort the sample. A job with no usable history shows **No history yet**.
- **Slices processed.** Succeeded vs. failed/dead-lettered totals for the **last day**, **last 7
  days**, **last 30 days**, and **all time** remain logical-window metrics: 16 successful chunks
  contribute one succeeded slice. Trailing totals and the chart group retained `slice_attempts` into
  one logical outcome per window; the latest terminal execution completion places that outcome in a
  time window or chart bucket. The **all time** card instead reads each parent slice's current outcome
  from never-pruned `current_slice_state`, so it remains accurate after old attempts are pruned.
- **Processed over time.** The chart shows logical slice outcomes per interval, split into succeeded
  and failed/dead-lettered, over 1 hour / 1 day / 7 days / 30 days. **Refresh** re-reads the read-only
  snapshot and recalculates relative times and ETAs.

## Ingestion throttling advisor

When a slice fails because Kusto throttled its `.set-or-append` against the cluster's **ingestion capacity policy** (HTTP 429, `Origin: 'CapacityPolicy/Ingestion'`, `CommandType: 'TableSetOrAppend'`), KO Lite records the event and surfaces how bad the throttling is — plus advisory `maxParallelism` reductions — on the **Throttling** page (`/throttling`). The dashboard shows a banner linking there while any cluster is throttled or has recently lost a slice to throttling. The advisor is read-only: it only recommends, and nothing changes until an operator clicks **Reduce to N**.

- **Detection.** Every retryable/dead-lettered slice whose error is an ingestion-capacity throttle is recorded as an observation (cluster, slice, attempt, reported capacity, time, and whether it was the slice's terminal dead-letter) in `ingestion_throttle_observations`. Recording is best-effort and isolated, so it never destabilizes a worker, and it is independent of the advisory surface. Other 429s (query/export capacity, or a workload group's request-rate-limit policy) are intentionally **not** treated as ingestion throttles.
- **Severity.** The page leads with "in the last `WindowMinutes`, **Y%** of slice attempts failed with throttling" (throttled attempts ÷ all attempts) and a time chart of that rate, so you can see how bad it is and whether it is trending up or clearing.
- **When it shows / clears.** A cluster is surfaced when, over the last `WindowMinutes` (default 20), the throttled-attempt rate is at least `RateThresholdPercent` (default 5%) **and** there are at least `MinThrottledSlices` (default 3) *distinct* throttled slices and `MinAttemptsForRate` (default 20) total attempts. Once surfaced it stays until the cluster has been clean for a continuous `CleanPeriodMinutes` (default 15) with no new throttle (hysteresis, so it does not flap). The distinct-slice and minimum-attempt floors keep a single self-healing 429 or a noisy tiny sample from tripping it.
- **Lost slices (always shown).** A slice that dead-lettered after consecutive throttled attempts is the worst outcome — a data gap needing a rerun. Any such still-unresolved slice within `TerminalFailureLookbackMinutes` (default 60) **forces** the page/banner to show regardless of the rate gate, and is listed (job, slice window, throttled attempts, state) so you can rerun it.
- **Keep-up floor (the safety check).** For each active job the advisor estimates `minParallelism = max(1, ceil((D / W) * KeepUpSafetyFactor))`, where `W` is `queryWindowSize` and `D` is a robust recent **successful** slice duration (the `DurationPercentile`, default p75, over `DurationLookbackHours`). Successful-only sampling keeps retry backoff from inflating the floor. A recommendation never drops a job below this floor — and the apply action re-checks it server-side and refuses when it cannot be verified — so a job can always keep up with real time.
- **Backfill-aware catch-up floor.** A job that is **behind real time** (a real eligible backlog) is intentionally running fast to catch up, so it is *not* trimmed down to the keep-up floor. Instead the advisor sizes a **catch-up floor** = `ceil((D / W) * (1 + B / T) * KeepUpSafetyFactor)`, where `B` is the backlog data-time and `T` is `CatchUpTargetHours` (default 24), and only trims excess above that. The recommendation shows the rough **catch-up ETA trade-off** (current → suggested), holding single-slice execution time constant, so you can see how much a reduction would lengthen the backfill.
- **Ranking and targeting.** The advisor lists the enabled jobs that are active on the throttled cluster (currently in-flight, themselves throttled in the window, or with a lost slice) and ranks them by **headroom** = current `maxParallelism` − the governing floor, most over-provisioned first. The throttled slice's own job is *not* assumed to be the culprit; jobs already at/below their floor are shown but not offered a reduction, and jobs without enough samples show **Insufficient data**.
- **Applying.** **Reduce to N** updates only that job's `maxParallelism` through the normal validated catalog update path (audited as `throttle-advisor`, recorded as a definition change). Because it is a definition change, it resets the job's catch-up throughput window briefly. Concurrent edits are detected via optimistic concurrency and reported so you can re-read.
- **Shared-cluster caveat.** Ingestion capacity is **cluster-wide** and shared across every KO Lite job *and every other tool/user* on that cluster. KO Lite cannot see non-KO-Lite load, so trimming KO Lite may not clear throttling if external load dominates. When throttling persists, also consider the cluster's ingestion **capacity policy** (`.show cluster policy capacity` / `.alter-merge cluster policy capacity`) or scaling the cluster out/up — see the throttling note on the page.

## Diagnostics

Run `.\scripts\Get-KoLiteDatabase.ps1` to print the in-use SQLite database path. While the app is running it reports the authoritative `database.path` from `/api/v1/system/status`; while the app is stopped it reports the default and flags the most likely live file. Pass `-BaseUrl` for a non-default endpoint.

Use `/api/v1/system/status` to confirm the database path, scheduler options, Kusto auth mode, worker-pool state, supported API versions, and shutdown state.

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

## Analyze failures with Copilot

From a job's **Operations** tab, **Analyze failures** asks Copilot to explain the job's recent failures. KO Lite sends secret-sanitized failure evidence (error codes and messages, attempts, and recent slice states) to **GitHub Copilot CLI** through a non-interactive `copilot -p` child process, then renders the returned Markdown in the panel. It writes nothing to Kusto and KO Lite does not persist the analysis.

Install GitHub Copilot CLI so the `copilot` command is on `PATH`, then run `copilot login` once. Failure analysis uses that CLI's Copilot subscription and sign-in; it does not reuse the separate GitHub CLI (`gh`) update-check token. When Copilot CLI is missing, signed out, or cannot access Copilot, the panel shows targeted install or login guidance.

The child process is deliberately non-agentic: KO Lite disables all tools, built-in MCP servers, repository custom instructions, remote control/export, and automatic CLI updates for the invocation. Failure messages are treated as untrusted evidence, not instructions. Only the final text response is accepted.

Configure it under `KoLite:CopilotAnalysis`:

| Key | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `true` | Set `false` to disable the feature. |
| `Executable` | `copilot` | Copilot CLI executable name or explicit path. |
| `Model` | `auto` | Copilot model selection; `auto` lets Copilot choose, or set a supported model id. |
| `TimeoutSeconds` | `120` | Per-request timeout. |

GitHub Models and its inference API were retired on July 30, 2026, so KO Lite does not call `models.github.ai`.

## Rerun and cleanup

From a slice detail page, use **Rerun this slice** to open the rerun planner. The planner also accepts a UTC start/end range and shows every root and downstream slice whose local state will be reset.

Rerun execution is intentionally two-step:

1. Review the affected slices and suggested Kusto cleanup commands. KO Lite suggests `.delete table ... records <|` commands that use `StartTime` and `EndTime`; edit them if a job's output table uses different columns.
2. After manually handling Kusto cleanup, acknowledge it on the rerun batch page. KO Lite snapshots old local state, attempts, logs, queue rows, and events into the rerun report, deletes the current local rows for those slices, and lets the normal scheduler pick the missing work back up.

Rerun is blocked while any affected slice is queued, leased, or running.

**Rerun vs. repair.** Rerun exists to *replace* output that is already in Kusto, which is why it needs
the manual cleanup step. To simply *re-run slices that failed* (nothing was successfully written, so
there is nothing to clean up), use the repair API instead — see
[Requeuing slices that already dead-lettered](#requeuing-slices-that-already-dead-lettered). Repair
cannot overwrite a `Completed` slice; the `ingest-by` dedup tag would discard the repeat ingestion.

Back up the SQLite database before service upgrades, hard deletes, repair experiments, or large reruns.

## Retry classification and dead-letters

When a slice fails, KO Lite asks the Kusto .NET SDK whether the error was **permanent**
(`KustoException.IsPermanent`) and normally uses that answer to decide whether to retry. One
bounded override handles cross-cluster failures: if a recognized remote Kusto error envelope
explicitly contains a nested `"@permanent": false`, that remote signal takes precedence over an
outer permanent HTTP 400 wrapper.

- **Permanent** (semantic errors, syntax errors, bad input — typically HTTP 400) — the request will
  never succeed as written, so the slice dead-letters on the first attempt without consuming its
  retry budget.
- **Not permanent** (low memory conditions, internal service errors, transport faults, throttling) —
  the slice is retried up to `MaxAttempts` (3) with exponential backoff (1 min, then 2 min, capped at
  5 min). Retries are safe to repeat because output is idempotent via `ingest-by`.
- **Remote non-permanent failure wrapped as permanent** (a cross-cluster error whose nested payload
  explicitly says `"@permanent": false`) — treated as not permanent and given the same bounded
  retry budget. The original outer exception type, message, and failure codes remain in diagnostics.
- **No Kusto exception to inspect** (an unclassified fault or timeout) — treated as retryable and
  bounded by the same `MaxAttempts`.

The dead-letter log message distinguishes the two cases, so you can tell them apart without
reconstructing the attempt history:

- `Slice dead-lettered without retry because the failure was classified as permanent.`
- `Slice dead-lettered after N of M attempts.`

The attempt's metrics JSON also records `isRetryable`, `isPermanent`, `kustoFailureCode`, and
`kustoFailureSubCode`.

> Do not infer retryability from general error keywords. A Kusto low-memory failure
> (`E_LOW_MEMORY_CONDITION`) may arrive inside a permanent cross-cluster wrapper while its structured
> remote payload reports `"@permanent": false`. KO Lite recognizes that envelope and explicit field;
> ordinary messages that merely contain similar text remain permanent.

### Requeuing slices that already dead-lettered

The classification change is forward-looking; it does not revisit slices that dead-lettered earlier.
To re-run those, use the **repair API** (`POST /api/v1/jobs/{jobId}/repairs`), which accepts an aligned
UTC range and re-queues the `Failed`/`DeadLettered` slices in it. Review the failures first so
genuinely permanent ones (a semantic error from a broken function, say) are fixed at the source
rather than retried:

```powershell
$base = 'http://127.0.0.1:5057'
$jobId = '11111111-2222-3333-4444-555555555555'   # permanent GUID

# Recent failures, newest first, with the attempt number they dead-lettered on.
Invoke-RestMethod "$base/api/v1/operations/failures?jobId=$jobId&limit=200" |
  Select-Object -ExpandProperty items |
  Where-Object status -eq 'DeadLettered' |
  Select-Object jobId, sliceStartUtc, attempt, reason

# Preview the repair (writes nothing), then enqueue it echoing the previewed count.
$range = @{ from = '2026-01-01T00:00:00Z'; to = '2026-01-02T00:00:00Z' }
$preview = Invoke-RestMethod -Method Post -Uri "$base/api/v1/jobs/$jobId/repair-previews" `
  -ContentType 'application/json' -Body ($range | ConvertTo-Json)

Invoke-RestMethod -Method Post -Uri "$base/api/v1/jobs/$jobId/repairs" -ContentType 'application/json' `
  -Body (($range + @{
      reason = 'Requeue transient failures'
      expectedSliceCount = $preview.repairableSliceCount
      expectedExecutionCount = $preview.repairableExecutionCount
      previewToken = $preview.previewToken
  }) | ConvertTo-Json)
```

Re-running is safe: output carries an `ingest-by` tag plus `ingestIfNotExists`, so Kusto dedupes a
repeat ingestion and no cleanup is needed. For the same reason repair only fills gaps — it cannot
overwrite an already-`Completed` slice, which is what the [rerun flow](#rerun-and-cleanup) is for. See
[local-api.md](local-api.md#repair) for the full contract and guards.

A slice that dead-lettered on **attempt 1** with a transient reason is a good requeue candidate; one
that dead-lettered on attempt 3 already exhausted its retries.

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

## Troubleshooting

- **Port in use:** add `--KoLite:Urls=http://127.0.0.1:5058`.
- **Unexpected live work:** restart with `--KoLite:Scheduler:Enabled=false`, pause jobs, or stop the local process and wait for active work to drain.
- **Kusto auth failures:** verify Azure CLI sign-in, managed identity settings, target cluster/database, and Kusto permissions.
- **Locked publish output:** stop the published app before republishing.
- **SQLite inspection:** use `database.path` from `/api/v1/system/status`; runtime sidecar files such as `*.db-wal` and `*.db-shm` are local artifacts.
- **Local API unreachable:** `/api/v1` routes exist only while the app is running and accept loopback callers; confirm `/healthz`, then inspect `/api/v1/system/status`.
- **Crash recovery:** long `queryTimeout` values also lengthen queue lease windows, so recovery after a hard crash can take longer for long-running jobs. A slice stuck as **Stalled (orphaned lease)** is recovered automatically on the next dispatch once its lease expires (for enabled jobs); resume a paused job, or use **Recover (re-queue) this slice** on the slice detail page, to recover it sooner. See [Orphaned leases and recovery](#orphaned-leases-and-recovery).
