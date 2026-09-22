# Kusto Slice Runner operations runbook

This runbook covers safe local operation for Kusto Slice Runner.

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
| `KoLite:UpdateCheck:Enabled` | `true` | Periodically checks GitHub for a newer published Kusto Slice Runner release. Set `false` to disable. |
| `KoLite:UpdateCheck:Interval` | `01:00:00` | How often to poll GitHub. Must be greater than zero. |
| `KoLite:UpdateCheck:Repository` | `microsoft/kusto-slice-runner` | `owner/repo` whose latest published release is compared to the running build. |
| `KoLite:Retention:Enabled` | `true` | Periodically prunes old operational telemetry so the local database stops growing without bound. Set `false` to disable (the database then grows unbounded). |
| `KoLite:Retention:WindowDays` | `30` | Operational telemetry older than this is eligible for pruning. Must be greater than zero. The slice window-history is never pruned. |
| `KoLite:Retention:Interval` | `06:00:00` | How often the retention pass runs. Must be greater than zero. |
| `KoLite:Retention:InitialDelay` | `00:02:00` | Delay after startup before the first retention pass. |
| `KoLite:Retention:BatchSize` | `2000` | Rows deleted per batch; each batch commits separately to keep write locks short on the live database. |

Compatibility aliases `KoLite:Scheduler:WorkerConcurrency` and `KoLite:Scheduler:MaxWorkerIterations` are still accepted by the worker-pool options.

### Console log verbosity

Console verbosity uses per-category log-level filters. The default level is `Warning`, which keeps framework and host `info:` lines (for example `Microsoft.Hosting.Lifetime` "Now listening on…" / "Application started") out of the console. The `KoLite.LocalApp` category is raised to `Information`, so Kusto Slice Runner's own progress lines — scheduler enqueue, per-slice worker start/finish, graceful-drain completion, and update-check transitions — remain visible. Warnings, errors, and dead-letter lines always remain visible. The two lower-value worker-dispatcher lines (dispatcher start and in-flight cancellation during shutdown) are emitted at `Debug`, so they stay quiet even at `Information`. To see everything, raise the level — for example set `Logging:LogLevel:Default` to `Debug` or `KoLite.LocalApp` to `Debug` in `appsettings.json`, or pass `--Logging:LogLevel:KoLite.LocalApp=Debug`. Durable scheduler/worker diagnostic rows are still controlled separately by `KoLite:Scheduler:LogEveryPass`.

## Update checks

Kusto Slice Runner stamps the git commit it was built from into the app at build time and, when
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
  - GitHub CLI not installed → install `gh` and restart Kusto Slice Runner.
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
Kusto Slice Runner deletes **non-authoritative operational telemetry** older than `KoLite:Retention:WindowDays`:

- `operational_logs` — routine scheduler/worker log rows.
- `work_queue` — only **terminal** rows (`Completed`/`DeadLettered`); `Queued`/`Leased` rows are
  never pruned, so claimable and in-flight work is untouched.
- `slice_attempts` — per-attempt detail for finished slices (in-progress `Started` attempts are kept).
- `performance_attempts` — bounded attempt-performance facts and resource measurements, using the protected chart-retention window rather than deleting them when a slice is rerun.
- `scheduled_slices` — the scheduling ledger.

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
(their maximum range is 30 days), the chart-backing `slice_attempts` and
`performance_attempts` tables are never pruned more aggressively than 30 days, even if a shorter
`WindowDays` is configured.

The latest retention outcome (enabled, window, interval, last-run time, and rows deleted) is exposed
under `retention` in `/api/v1/system/status`.
Its existing `attemptsDeleted` subtotal covers both attempt-detail and performance-fact rows.
Retention logs and the durable cleanup details distinguish the two counts without adding a separate performance counter to status.

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

Kusto Slice Runner hosts a loopback-only `/api/v1` agent API with generated OpenAPI at `/api/v1/openapi/v1.json`. It provides first-class GUID-keyed job create/replace/pause/resume, ETag concurrency, batch import/export, safe soft-delete/restore, failed-work repair, cursor-paged operational reads, and opt-in read-only Kusto lineage. It never exposes hard delete, whole-slice rerun, Kusto cleanup, or arbitrary Kusto writes. See [local-api.md](local-api.md) for workflows and the generated document for exact schemas.

## Dashboard success statistics

The dashboard success charts use complete UTC-aligned buckets and omit the current open bucket. The
UI shows the exact **complete through** boundary, so a 1-hour view can lag by less than one minute, a
1-day view by less than one hour, and a 30-day view by less than one UTC day. Dashboard status pills
and recent-failure rows remain current; only the historical chart buckets lag.

The first chart counts completed **execution-unit attempts**: each chunk attempt for a chunked job,
or each slice attempt for an unchunked job. Retryable failures, terminal failures, dead letters, and
lease loss count as unsuccessful attempts; in-flight attempts are excluded. The second chart counts
terminal logical slices and shows whether each slice eventually succeeded after retries. A chunked
slice therefore does not enter the second chart until all of its child work has resolved.

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
- **Downstream consumers.** The non-job **functions and materialized views** that read each job's output table are added. Consumers that are themselves Kusto Slice Runner jobs map onto the existing job node (that link is already shown); pass-through tables (e.g. update-policy targets) are bridged through but not drawn.
- **Upstream sources.** The tables/functions each job's function **directly reads** are added as source nodes — **including cross-cluster sources**, which are named directly in the function's dependencies (and labelled `@cluster`), so no second cluster is queried.
- **Implicit (undeclared) dependencies.** When a job's function reads **another KO job's output table that is not in its `dependsOn`**, a **dashed amber** edge is drawn (and noted in the legend). This is informational — it does **not** change scheduler readiness, which still uses the declared `dependsOn`. Consider adding the edge to `dependsOn` if the ordering matters.
- Scope is the charted jobs' own cluster(s); one call returns lineage across every accessible database on that cluster. A downstream consumer hosted on a *different* cluster than the job is not discovered (cross-cluster *sources*, named directly, are). Failures (auth, permission, unreachable cluster, timeout) surface as an inline message beside the button and leave the job graph intact.

## Bulk actions on the dashboard

The home dashboard supports multi-select bulk actions only in the **Active jobs** and **Completed jobs** sections. Use the per-row checkboxes or a section's header checkbox to select jobs; the contextual action bar offers **Pause**, **Resume**, **Soft delete**, and **Export**. The **Soft-deleted jobs** section is not selectable, so permanent deletion cannot be reached from the dashboard's normal bulk-action flow. Selection respects the dashboard text filter — filtered-out rows are excluded — and collapsing the Inactive jobs group keeps the current selection.

Bulk Pause/Resume/Soft delete apply with no extra confirmation (soft delete is reversible from the Soft-deleted section via **Restore**). They honor the same optimistic-concurrency model as the single-row actions: each selected row carries the catalog version shown on the page, and any job that changed since the page loaded — or is already in the requested state, soft-deleted, or missing — is **skipped** rather than forced. After the action, a summary banner reports how many jobs changed and how many were skipped. Bulk **Export** downloads an import-compatible JSON array (`ko-lite-jobs.json`) containing only the selected jobs, sorted in ascending `activityId` (job id) order.

Bulk **Hard delete** is intentionally isolated on **Manage soft-deleted jobs**, linked from the Soft-deleted jobs section header. That page contains only currently soft-deleted jobs and is the only surface with bulk hard-delete checkboxes. After selection it opens a separate review page that lists every selected activity ID and permanent GUID, explains which local rows will be removed, and requires the exact count phrase `DELETE 1 JOB` or `DELETE N JOBS`. Final submission revalidates every job's catalog version, soft-delete state, disabled state, and active leases/running slices inside one SQLite transaction. If any selected job is missing, changed, restored, enabled, or still executing, the entire batch is rejected and **no jobs are deleted**. A successful batch records one purge run and an audit event for each deleted job. Hard delete removes local Kusto Slice Runner state only; it never deletes Kusto data.

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

## Automatic startup at Windows sign-in

Automatic startup is opt-in and Windows-only. From a **stable published folder**,
register a per-user Task Scheduler task:

```powershell
.\Register-KoLiteStartup.ps1 -DryRun
.\Register-KoLiteStartup.ps1                         # first registration: visible console
.\Register-KoLiteStartup.ps1 -WindowMode Background  # change the next start to background
.\Register-KoLiteStartup.ps1 -WindowMode Console     # change back to a visible console
.\Get-KoLiteStartup.ps1
.\Unregister-KoLiteStartup.ps1 -DryRun
.\Unregister-KoLiteStartup.ps1
```

The task runs as the registering user at limited privileges, only after that user
signs in. It stores no Windows password and does not require Windows Terminal.
Windows can host the visible PowerShell console in the configured default
terminal; selecting a specific Terminal profile/tab is not supported.
Registration, updates, and removal never launch or stop the app immediately.
Publishing/extracting a package never opts you in automatically.

**Settings and job execution.** The app uses the published folder as its working
directory and resumes existing enabled jobs according to its normal settings.
Paused/deleted jobs stay paused/deleted; a disabled scheduler is not enabled by
registration. Preserve custom database, URL, or other launch overrides explicitly:

```powershell
.\Register-KoLiteStartup.ps1 -AppDirectory 'D:\Kusto Slice Runner' -AppArguments @(
    '--ConnectionStrings:KoLiteSqlite=D:\Kusto Slice Runner data\catalog.db',
    '--KoLite:Urls=http://127.0.0.1:5058'
)
```

On updates, omitted arguments and window mode retain their previous values.
Use `-AppArguments @()` to clear saved overrides. The app directory is resolved
from an explicit `-AppDirectory`, an app adjacent to the registration script,
the existing registration, or `%LOCALAPPDATA%\KoLite\run-app`, in that order.
Settings are encoded **data, not executable commands**, in the task definition.
Encoding is not encryption: never put credentials or secrets in `-AppArguments`.
The task and settings are replaced together, avoiding separate settings-file
updates. The scripts refuse unrelated tasks with the same name.

**Lifecycle.** Console mode shows live app output; background mode displays no
console. Minimize the console to leave the app running. Closing it is not a
hide-to-background action and can interrupt work; use `Stop-KoLiteApp.ps1` for
a graceful drain. The startup console closes when the app exits. A deliberate
stop or crash stays stopped until another sign-in or an explicit manual start;
there is no crash watchdog or periodic restart. Removing startup removes the
owned task/settings, not a running process, the database, or its logs.

The task ignores duplicate triggers, has no execution time limit, and can start
and continue on battery power. It continues while the workstation is locked,
but is not an always-on service across sign-out. No work runs while the machine
is off or asleep, and startup does not change power settings or wake the PC.

**Authentication and prerequisites.** The task uses the existing Azure CLI
identity without prompting. Azure CLI must be available on the normal Windows
user/machine PATH; the framework-dependent package also needs the .NET 10
ASP.NET Core runtime. PowerShell profiles are not loaded. Make custom environment
settings such as `AZURE_CONFIG_DIR` available in the user's persistent Windows
environment rather than only in a terminal session. Registration does not copy
environment variables, credentials, or token caches. MFA, conditional access,
expired/revoked credentials, and VPN/network readiness can still require
operator action (`az login` in a separate terminal). Successful app startup is
not proof of Kusto access. Pre-login hosting would require a separate unattended
identity/hosting design; the desktop task does not implement it.

**Diagnostics.** `Get-KoLiteStartup.ps1` reports the task name, enabled/state
values, selected mode, arguments, last run/result, and log directory.
`%LOCALAPPDATA%\KoLite\startup\startup.log` records startup, stdout/stderr, and
exit codes in either mode. It keeps three rotated files (`startup.log.1` through
`.3`), each at most 1 MiB; individual messages are truncated after 4,096
characters. Registration itself does not create these logs. A nonzero task
result or missing logs can indicate a launcher, execution-policy, or missing-file
failure before the app started. Inspect the Task Scheduler history and use the
matching start script manually for diagnosis. `/healthz` checks local health;
`/api/v1/system/status` exposes the database and scheduler configuration, not a
Kusto authentication test.

The helpers respect Windows execution policy and organization restrictions;
they do not bypass policy, elevate, or fall back to another account when
registration is denied. Review downloaded scripts and follow your organization's
policy for allowing them to run.

**Upgrades.** Replacing application files at the same stable path preserves the
task; moving to another folder requires re-registration from the new folder or
with `-AppDirectory`. Keep `Start-KoLiteApp.ps1`, `KoLite.Startup.psm1`, and the
registration helpers from the same release. Follow the existing backup, drain,
and schema-upgrade precautions before replacing a deployed version. Startup
registration is not permission to upgrade a running app or open an older app's
live database from a new build.

## Catch-up estimate

The **Job details** page (`/jobs/{jobId}`) shows a catch-up estimate card above the tabs **only when the estimate is useful**: the job must be enabled and not paused, and it must have a real *actionable* backlog — more than a couple of eligible-but-incomplete slices that the job can work off itself. Slices that are merely waiting on an upstream dependency are excluded, so a dependent job that is only blocked on upstream (for its most recent slices) shows no card. Once a real backlog exists the card stays visible until it is actually worked off; it is **not** hidden as the job nears its frontier. When there is a real backlog but not yet enough fresh data to project a rate (typically right after a definition change), the card stays visible in a **collecting data** state. A job running at its normal cadence, or one whose only lag is upstream-blocked, shows no card.

- **Calculation.** The backlog is the eligible-but-incomplete slices (those whose window ends at or before `now - delayFromUtcNow`, capped by `endOn`), **minus any slices currently blocked on an upstream dependency**, multiplied by `queryWindowSize`. The processing rate `R` (data-time completed per wall-clock time) is measured from recent successful slice completions, then the projected catch-up time is `backlog / (R - 1)` — the `- 1` accounts for "now" continuing to advance while the job works. The card reports four metrics — **slices behind**, **data-time behind**, the **average processing rate** (slices per hour, also shown as a multiple of real time), and the **estimated time remaining** — plus the absolute ETA and the completed-through frontier. When recent slices are excluded because they are upstream-blocked, the note says how many. A job never catches up to the literal current time; it converges to its configured `delayFromUtcNow` lag.
- **Throughput window.** Only completions since the last schedule change are sampled (capped at the last 6 hours), so editing a job's definition does not skew the rate with executions that ran under the previous definition. Right after a change there is briefly too little data to project a rate. Instead of hiding the card, Kusto Slice Runner keeps the **Catching up** card in a *collecting data* state that still shows the current backlog (slices and data-time behind) plus a two-item **requirements checklist** beneath the explanation. The checklist tracks the two gates an estimate waits on, each with a green checkmark once it clears: **successful completions** (the sample gathered so far versus the required count, for example "2 of 3 completions") and **time collected** (the span between the first and last sampled completion versus the required minimum, with the remaining time shown while pending, for example "~4 min of ~10 min (~6 min to go)"). An estimate appears only once **both** clear — at least `MinThroughputSamples` (default 3) successful completions spanning at least `MinThroughputSpan` (default 10 minutes) — at which point the message and checklist are replaced by the projection. When the short sample is caused by a recent definition change the note says so; otherwise it gives a generic "collecting recent data" explanation.
- **Not keeping up.** If the backlog is real but the recent rate is at or below real time (`R ≤ 1`), the card switches to a **Not keeping up** warning instead of an ETA — at the current rate the job will not catch up, so investigate failures, throughput, or `maxParallelism`.

## Activity page

The **Activity** page (`/activity`, linked in the top nav) answers "how much work is flowing through
Kusto Slice Runner right now and over time?". It distinguishes a logical **slice** (one time window) from an
**execution unit** (one chunk for a chunked job, or the slice itself for an unchunked job).
The **Live** subview retains these operational counts and charts; **Performance** compares
historical resource use and attempt reliability.

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
- **Executions processed.** Succeeded vs. failed/dead-lettered totals for the **last day**, **last 7
  days**, **last 30 days**, and **all time** count execution units: one chunk for a chunked job, or
  one unchunked slice. Sixteen successful chunks therefore contribute 16 succeeded executions.
  Retryable and lease-lost attempts do not add processed executions; retries and repairs resolve to
  each execution unit's latest terminal outcome. Trailing totals and the chart group retained
  `slice_attempts` by execution identity, and the latest terminal completion places that outcome in a
  time window or chart bucket. The **all time** card uses current child rows (or parents with no child
  rows) to identify execution units and their never-pruned terminal state-event history to retain the
  prior outcome while a retry or repair is queued/running. Old-attempt retention therefore does not
  change the total.
- **Executions processed over time.** The chart shows execution outcomes per interval, split into
  succeeded and failed/dead-lettered, over 1 hour / 1 day / 7 days / 30 days. It uses complete
  UTC-aligned buckets and displays the exact boundary through which chart data is complete; current
  open-bucket completions still contribute to the live trailing totals. **Refresh** re-reads the
  read-only snapshot and recalculates relative times and ETAs.

### Performance comparison

Open **Activity -> Performance** (`/activity?view=performance`). Select the last **1 hour**,
**24 hours**, **7 days** (default), or **30 days**. The selected interval is an exact trailing
UTC completion-time interval, including recent completions in a chart's still-open bucket.
Existing Live/dashboard chart defaults and bucket boundaries are unchanged.

Each job row pools its successful query attempts. Expand a chunked job to see raw 0-based chunk IDs
with the same metrics; IDs are not interpreted as regions. Successful chunks contribute even if
their parent window is incomplete. Paused and completed jobs with history remain useful comparison
subjects. Sorting/filtering keeps each job and its children together.
The initial order is job name ascending. Selecting a different column header sorts descending first;
subsequent clicks on that header toggle ascending/descending. Explicit sort URLs remain supported.

| Columns | Meaning |
| --- | --- |
| Attempt success | Percentage plus successful/total completed-attempt counts, not eventual logical-window success. Sixteen successful chunks plus one failed retry shows 16 / 17 and approximately 94.1%. Completed attempts include failures and lease loss; running and unknown outcomes are excluded. |
| CPU P50/P90/P95 | Kusto command `TotalCpu` in seconds. CPU time can exceed elapsed time. |
| Duration P50/P90/P95 | Server-side `.set-or-append` duration in seconds, never worker elapsed time, scheduling wait, or whole-window latency. |
| Memory peak P50/P90/P95 | Kusto-reported `MemoryPeak` in GiB (`bytes / 1024^3`), not inferred concurrent job memory. |

Hover a metric value for its exact value and the number of valid samples out of successful attempts.
Each resource family can have a different sample count; these counts are distinct from the
successful/total fraction in Attempt success. The same context is available to table accessibility tools.

Percentiles use exact nearest rank over the underlying samples. Job totals are not averages of
chunk percentiles or percentages. Missing measurements show `n/a`, not zero; one sample legitimately
has identical P50/P90/P95. Known idempotent duplicate-suppression successes do not create artificial
zero-cost samples. Resource gaps do not remove known attempts from the reliability denominator.

**Coverage warning.** The prominent warning appears only when **at least 20%** of eligible successful
attempts are missing statistics **and at least five attempts** are missing. It is scoped to the
selected completion period and the visible job-ID, tag, and name filters. The newest five minutes
of completions and known duplicate-suppressed attempts are excluded from this warning population.
Each attempt missing any required resource family counts once, not once per missing metric.
For example, five missing out of 25 eligible attempts shows the warning; four out of 20 does not.

This grace period does not remove recent attempts from the table or change its percentiles or
success rates. Name filtering recomputes the warning locally; sorting and chunk expansion do not
change its population. Smaller gaps remain available in metric-value sample-count tooltips. Individual lookup,
authentication, network, and historical errors are retained under **Collection details**, labeled
as global retained-history diagnostics, and do not independently trigger the prominent warning.
Collection and retries continue even when the warning is hidden.

**Collection details** and **How these statistics are calculated** are always-visible sections below
the report, not collapsible panels. They remain available for empty or initializing reports.

**Automatic collection.** In a normal execution-enabled instance, a built-in background service
reads bounded `.show commands-and-queries` metadata from the recorded job targets and stores the
selected statistics in SQLite. There is no feature opt-in or off switch. It runs independently of
worker slots, including while jobs are paused or workers are idle. Page loads, refreshes, sorting,
filtering, and chunk disclosure never request Kusto statistics directly.

The collector processes small batches sequentially, limits request duration, backs off on delayed
visibility/authentication/throttling errors, and stops new work during drain. Failures are visible
through collection status and logs and cannot cause a job retry or change a slice outcome.

**Upgrade and coverage.** Recent current attempts and retained rerun snapshots are reconciled
automatically. Initial local-history indexing and subsequent resource backfill are shown separately
from complete measurements. Kusto history is available for 30 days and is scoped by the configured
identity's permissions. Older reused client request IDs require unambiguous target/timing evidence;
inaccessible, expired, or ambiguous history stays unavailable rather than being guessed. Whole-slice
rerun does not erase earlier attempts from Performance; hard deletion of the job does.

**UI-only mode.** `KoLite:Scheduler:Enabled=false` suppresses collection and backfill as well as
scheduling/worker dispatch. The Performance view still displays already stored statistics. An
alternate port/database, Development environment, `AllowMultipleInstances`, or disabling retention
alone is not a no-execution mode. `Start-KoLiteUi.ps1` supplies the required scheduler override.

## Upgrading after throttling-advisor retirement

The standalone advisor, recommendations/apply workflow, banner, chart, dedicated
detection, and observation statistics have been removed. `/throttling` and
`/throttling/apply` return 404. Remove unused `KoLite:Throttling:*` configuration;
those settings no longer have any effect.

The first upgraded startup drops `ingestion_throttle_observations` and its
indexes. Back up the database using the normal safe backup process before
upgrading, and stop the older app before the new build opens that database.
Do not start a new UI-only build against an older app's live database: startup
applies schema changes even with scheduling and retention disabled. Use a
separate copy for review. Do not copy only the main database file while WAL
writes are active; use a SQLite-consistent backup or copy the stopped database
and its sidecars together.

Only dedicated observation data is discarded. Ordinary attempts, errors, logs,
slice/chunk state, repair/rerun history, and existing audit/retention JSON remain
intact. Generic Kusto retry/backoff and general failure analysis are unchanged.
An older binary may recreate an empty observation table but cannot restore the
discarded data.

`/api/v1/system/status` no longer returns
`retention.ingestionThrottlesDeleted`; callers must not require that field.
There is no replacement counter or zero-valued compatibility property.

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

From a job's **Operations** tab, **Analyze failures** asks Copilot to explain the job's recent failures. Kusto Slice Runner sends secret-sanitized failure evidence (error codes and messages, attempts, and recent slice states) to **GitHub Copilot CLI** through a non-interactive `copilot -p` child process, then renders the returned Markdown in the panel. It writes nothing to Kusto and Kusto Slice Runner does not persist the analysis.

Install GitHub Copilot CLI so the `copilot` command is on `PATH`, then run `copilot login` once. Failure analysis uses that CLI's Copilot subscription and sign-in; it does not reuse the separate GitHub CLI (`gh`) update-check token. When Copilot CLI is missing, signed out, or cannot access Copilot, the panel shows targeted install or login guidance.

The child process is deliberately non-agentic: Kusto Slice Runner disables all tools, built-in MCP servers, repository custom instructions, remote control/export, and automatic CLI updates for the invocation. Failure messages are treated as untrusted evidence, not instructions. Only the final text response is accepted.

Configure it under `KoLite:CopilotAnalysis`:

| Key | Default | Purpose |
| --- | --- | --- |
| `Enabled` | `true` | Set `false` to disable the feature. |
| `Executable` | `copilot` | Copilot CLI executable name or explicit path. |
| `Model` | `auto` | Copilot model selection; `auto` lets Copilot choose, or set a supported model id. |
| `TimeoutSeconds` | `120` | Per-request timeout. |

GitHub Models and its inference API were retired on July 30, 2026, so Kusto Slice Runner does not call `models.github.ai`.

## Rerun and cleanup

From a slice detail page, use **Rerun this slice** to open the rerun planner. The planner also accepts a UTC start/end range and shows every root and downstream slice whose local state will be reset.

Rerun execution is intentionally two-step:

1. Review the affected slices and suggested Kusto cleanup commands. Kusto Slice Runner suggests `.delete table ... records <|` commands that use `StartTime` and `EndTime`; edit them if a job's output table uses different columns.
2. After manually handling Kusto cleanup, acknowledge it on the rerun batch page. Kusto Slice Runner snapshots old local state, attempts, logs, queue rows, and events into the rerun report, deletes the current local rows for those slices, and lets the normal scheduler pick the missing work back up.

Rerun is blocked while any affected slice is queued, leased, or running.

**Rerun vs. repair.** Rerun exists to *replace* output that is already in Kusto, which is why it needs
the manual cleanup step. To simply *re-run slices that failed* (nothing was successfully written, so
there is nothing to clean up), use the repair API instead — see
[Requeuing slices that already dead-lettered](#requeuing-slices-that-already-dead-lettered). Repair
cannot overwrite a `Completed` slice; the `ingest-by` dedup tag would discard the repeat ingestion.

Back up the SQLite database before service upgrades, hard deletes, repair experiments, or large reruns.

## Retry classification and dead-letters

When a slice fails, Kusto Slice Runner asks the Kusto .NET SDK whether the error was **permanent**
(`KustoException.IsPermanent`) and normally uses that answer to decide whether to retry. Two
narrow overrides handle cross-cluster failures: an explicit non-permanent signal in a recognized
remote error envelope, and a specific remote-schema resolution failure reported as a semantic
error without that envelope. Both use the existing bounded retry budget.

- **Permanent** (local missing-table errors, syntax errors, bad input — typically HTTP 400) — the
  request will not succeed unchanged, so the slice dead-letters on the first attempt without
  scheduling a retry.
- **Not permanent** (low memory conditions, internal service errors, transport faults, throttling) —
  the slice gets up to `MaxAttempts` (3 total attempts, not 3 additional retries) with exponential
  backoff (1 min, then 2 min, capped at 5 min). Retries are safe to repeat because output is
  idempotent via `ingest-by`.
- **Remote non-permanent failure wrapped as permanent** (a cross-cluster error whose nested payload
  explicitly says `"@permanent": false`) — treated as not permanent and given the same bounded
  retry budget. The original outer exception type, message, and failure codes remain in diagnostics.
- **Remote-schema semantic failure** — a Kusto SDK `SemanticException` reporting
  `Errors occurred while resolving remote entities`, followed by the failed-name/scopes structure
  and an explicit HTTPS `$Cluster` scope, gets the same bounded retries. A temporary remote schema
  lookup outage can produce this error even when the SDK marks it permanent. Kusto Slice Runner also
  recognizes the same typed semantic failure inside an exception chain, but does not use this
  text-only fallback to override a recognized remote error envelope. A genuinely missing remote
  entity can produce the same message and will consume the normal retry budget before
  dead-lettering. The original exception type, message, and failure codes remain unchanged;
  `isPermanent` records Kusto Slice Runner's effective classification.
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
> remote payload reports `"@permanent": false`. Kusto Slice Runner recognizes that envelope and explicit field;
> ordinary messages that merely contain similar text remain permanent. Likewise, the remote-schema
> override requires the specific SDK semantic failure and remote-scope structure, not just
> "could not be resolved" or a cluster URL. It does not make all semantic errors retryable.

These retries do not guarantee recovery from an outage that outlasts the budget. The policy change
does not increase that budget, clear remote schema caches, or automatically repair historical
dead letters.

### Requeuing slices that already dead-lettered

For agent-guided historical cleanup, use the
[`ko-lite-gap-repair` skill](../.github/skills/ko-lite-gap-repair/SKILL.md).
It requires the newest three resolved logical slices to be Completed for
windows up to and including one hour, or the newest one for longer windows.
Recovered retries are allowed; a newer terminal failure is not skipped to
find older successes. This workflow gate does not change dashboard health.
Unhealthy jobs are reported and skipped. `healthPolicy: "recent"` gaps,
including downstream gaps, are report-only unless the user explicitly
follows up requesting their repair. One bounded manifest approval normally
covers the work; an explicit prompt to proceed without waiting supplies that
authorization, but does not waive policy, health or preview safeguards.
The skill uses failed-work repair, preserves successful history and checks
affected downstream completion; it never performs whole-slice rerun or cleanup.

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

How Kusto Slice Runner handles this:

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
