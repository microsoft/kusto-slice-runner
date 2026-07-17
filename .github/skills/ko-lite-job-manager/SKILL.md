---
name: ko-lite-job-manager
description: "Use when the user wants an agent to read KO Lite jobs, inspect read-only operational diagnostics (slice states, leases, throughput, catalog history, logs, audit), or create/update job schedules directly in a running KO Lite app (instead of clicking through the dashboard). Drives the KO Lite localhost JSON API; it reads state, upserts schedules - including pausing or resuming a job via the schedule's isPaused field - and can soft-delete or restore a job (reversible); it never hard-deletes jobs, runs Kusto, reruns, or repairs. Requires the KO Lite app to be running locally."
metadata:
  author: Azure Core Team
  version: "1.3.2"
---

# KO Lite job manager

Use this skill to **manage KO Lite jobs from an agent session** by talking to a
running KO Lite app's localhost JSON API. The deliverable is a real catalog
change in the local SQLite database, applied through the same validated import
path the dashboard uses. It can also **read operational diagnostics** (slice
states, leases, throughput, history, logs, audit) to investigate a job — or the
whole app — without leaving the session.

This skill is the read/write counterpart to `ko-lite-schedule-json` (which only
authors a JSON file and never uploads). Use `ko-lite-schedule-json` when the user
just wants a file; use **this** skill when the user wants the change applied to a
running instance.

## What this skill will and will not do

It **will**:

- Read jobs: list summaries, fetch one job's canonical schedule, export all jobs.
- Create new jobs and update existing job schedules via upsert import.
- Pause or resume a job by importing its schedule with `isPaused: true` (pause)
  or `isPaused: false` (resume); a paused job stays in the catalog but does not
  schedule or claim queued retries.
- Soft-delete a job (reversible) and restore it, via
  `POST /api/jobs/{id}/soft-delete` and `POST /api/jobs/{id}/restore`. Both require
  the job's current `expectedVersion`. Soft-delete hides the job (flips `is_enabled`
  off + records a lifecycle event; purges nothing) and is **blocked** when active
  downstream jobs depend on the target unless you pass `force: true`.
- Read **operational diagnostics** (read-only): per-job slice states and leases,
  attempts, events, logs, queue, catalog-version history with diffs, throughput,
  and dependency readiness; and cross-job worker-pool state, in-flight/expired
  leases, throughput, queue, logs, failures, audit trail, and rerun/repair
  **history**.

It **will not** (these stay manual / dashboard-only on purpose):

- **Hard-delete** (permanently purge) a job. Hard-delete stays a dashboard action;
  the API exposes no hard-delete surface. (Soft-delete and restore *are* supported —
  see above.)
- Toggle a job's `isEnabled` lifecycle directly. Pausing is supported (via `isPaused`
  above) and soft-delete/restore flip `is_enabled` with lifecycle semantics, but
  there is no generic enable/disable schedule field, and an import **re-activates** an
  enabled job — so use `isPaused: true` to pause a schedule rather than run it.
- Execute Kusto, run reruns, run cleanup, or run repair. (Reading the rerun/repair
  **history** via diagnostics is fine; *triggering* a rerun/repair is not.)
- Touch the SQLite file directly. All reads and writes go through the API.

If the user asks for any of the "will not" actions, stop and tell them to use the
dashboard; do not attempt a workaround.

## Safety model

Every write goes to `POST /api/jobs/import`, which calls the same
`SqliteJobCatalogRepository.Import` path as the dashboard. That gives you, for
free: strict schedule parsing (unknown fields rejected), started-job mutation
policy (immutable permanent `id`; `queryWindowSize` and `startFrom` read-only
once a job has started; `activityId` is a mutable display label that may be
renamed), catalog versioning, audit events, and tag normalization - all in one
transaction. Import is **additive and update-only**: an item matches an existing
job by `id` when present (this is how a **rename** is applied — same `id`, new
`activityId`), else by `activityId`; new ones are created (a supplied `id` is
preserved, else minted); omitted jobs are never deleted.

**Soft-delete and restore** go to `POST /api/jobs/{id}/soft-delete` and
`POST /api/jobs/{id}/restore`, which call the same `SqliteJobLifecycleService` the
dashboard uses. Both require the job's current `expectedVersion` (optimistic
concurrency; a mismatch is a `409`). Soft-delete is reversible — it flips
`is_enabled` off and records a lifecycle event, purging nothing — and is **blocked by
default** when active downstream jobs depend on the target (a `409` listing them);
pass `force: true` to override. Hard-delete is not exposed.

**Definition update vs. historical replay:** importing an updated definition keeps
the job's permanent `id` and completed slice history. Future or otherwise-missing
slices use the new definition, but slices that already completed do not run again.
Soft-delete/restore also preserves that identity and history. Before updating a
started job, ask whether the user expects previously completed slices to be
recomputed. If yes, use the replacement workflow below; do not present an upsert as
replay.

Dependencies make replacement a coordinated operation. Soft-delete is blocked by
active downstream dependents unless explicitly forced; hard-delete has **no**
dependency check — it only requires the job to be disabled and have no actively
leased or running work. Inert queued retries and expired leases do not block it. If
an upstream is force-soft-deleted or hard-deleted, downstream jobs keep referencing
its old permanent GUID and their future work becomes `DependencyBlocked` until the
edge is rebound.

The **diagnostics** endpoints are strictly **read-only**: they perform no writes,
no Kusto, and no scheduler/rerun/repair mutation — they only surface existing
local state (the same read models the dashboard renders). They are bounded by
default (capped `take`, recent-time windows) so a call never scans the whole
local store.

The API is **loopback-only** and intended for same-machine use.

**Execution awareness:** creating or updating an *enabled, unpaused* job means the
running scheduler may start scheduling and executing it - exactly like a dashboard
import. To pause (or stage) a job, import it with `"isPaused": true`; import it
again with `"isPaused": false` to resume. An import always re-activates an
`isEnabled` job, so use `isPaused` - not enable/disable - to control whether it runs.

## Discovering the endpoint

- Default base URL is `http://127.0.0.1:5057` (the app's loopback default).
- Confirm the app is up and learn where its database lives with the health
  endpoint, which returns `status`, `databasePath`, `jobCount`, and more:

  ```powershell
  .\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1 -Action Health
  ```

- If the app is not running, ask the user to start it
  (`dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj`) or to give
  you the correct `-BaseUrl`. Do not proceed against an unreachable app.

## API surface

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/jobs` | Array of job summaries (`jobId` = permanent GUID, `displayName` = the activityId label, `isEnabled`, `isSoftDeleted`, `hasStarted`, `isPaused`, `tags`, `target`, `catalogVersion`, timestamps). To find a job by its human `activityId`, match `displayName`. |
| GET | `/api/jobs/{jobId}` | One summary plus `schedule` (the canonical, import-compatible schedule object, including its `id`). The API route requires the permanent GUID. The helper prefers that GUID but can resolve an exact `activityId` through `GET /api/jobs` when the caller does not know it. 404 if missing. |
| GET | `/api/jobs/export` | Import-compatible JSON array of all non-soft-deleted jobs. |
| POST | `/api/jobs/import` | Body is schedule JSON (single object or array). Returns `{ created, updated, total, items[] }`. 400 with `{ error }` on validation/mutation failure. |
| POST | `/api/jobs/{jobId}/soft-delete` | Soft-delete (hide) a job — reversible. `{jobId}` is the permanent GUID. Body `{ expectedVersion (required), reason?, force? }`. Returns `{ job }`. 400/404/409; 409 `{ error, dependents[] }` when active dependents block it and `force` is not set. |
| POST | `/api/jobs/{jobId}/restore` | Restore a soft-deleted job. Body `{ expectedVersion (required), reason? }`. Returns `{ job }`. 400/404/409 (no dependents check). |

## Read-only diagnostics

A strictly **read-only** family of endpoints surfaces the operational read models
that power the dashboard, so you can investigate a job (or the whole app) without
touching the SQLite file. All are GET, loopback-only, perform no mutation, and are
bounded by default. Pass filters via the script's `-Query` hashtable (or as a URL
query string). Common filters: `take`, `from`/`to` (ISO-8601 UTC), `bucket`
(`5m`/`30m`/`1h`/seconds), `groupBy=job`, `state`, `level`, `category`, `action`,
`subjectType`, `subjectId`, `jobId`, `batchId`. See `docs\local-api.md` for the
full contract.

Per-job — `GET /api/jobs/{jobId}/…` (the script's per-job actions require `-JobId`;
it accepts the GUID **or** the `activityId`):

| Script action | Route | Returns |
| --- | --- | --- |
| `Get-JobStatus` | `…/status` | Slice-state counts + `maxParallelism`/paused/started + queue counts. |
| `Get-Slices` | `…/slices` | Slice states **with lease owner/expiry/attempt** (`-Query @{ state='Running' }`). |
| `Get-Attempts` | `…/attempts` | Recent attempts (incl. in-flight `Started` rows with no completion). |
| `Get-Events` | `…/events` | Slice-state event timeline. |
| `Get-JobLogs` | `…/logs` | Operational logs (`-Query @{ level='Warning' }`). |
| `Get-JobQueue` | `…/queue` | Work-queue items incl. `lockedBy`/`lockedUntilUtc`. |
| `Get-History` | `…/history` | Catalog version history **with a JSON diff** (e.g. a `maxParallelism` change). |
| `Get-JobThroughput` | `…/throughput` | Per-job completion series + sample (`-Query @{ bucket='30m' }`). |
| `Get-Dependencies` | `…/dependencies` | Declared upstreams + a sample of `DependencyBlocked` slices with their missing upstream slices. |

Cross-job / global — `GET /api/diagnostics/…`:

| Script action | Route | Returns |
| --- | --- | --- |
| `Get-WorkerPool` | `…/worker-pool` | Worker-pool snapshot: in-flight workers, queued/leased/**expired-lease** counts, saturation. |
| `Get-RunningSlices` | `…/running-slices` | All `Running` slices with lease owner/expiry, **oldest first** — the fingerprint of a job stalled by hung leases. |
| `Get-Throughput` | `…/throughput` | Global completion series; `-Query @{ groupBy='job' }` answers "is the whole app stalled or just one job?". |
| `Get-Queue` | `…/queue` | Queue status summary (queued/leased/expired). |
| `Get-Logs` | `…/logs` | Operational logs across all jobs. |
| `Get-Failures` | `…/failures` | Recent failed/dead-lettered slices + failure-summary runs. |
| `Get-Audit` | `…/audit` | System audit trail (rerun planned/executed, lifecycle, …). |
| `Get-Reruns` | `…/reruns` | Rerun-batch **history**; `-Query @{ batchId='…' }` for one batch's slices. |
| `Get-Repairs` | `…/repairs` | Repair-batch **history**; `-Query @{ batchId='…' }` for one batch's repair slices. |

**When to use:** if a job "stopped running", start with `Get-RunningSlices` (look
for old/expired leases), `Get-WorkerPool` (in-flight vs. expired leases), and
`Get-Throughput -Query @{ groupBy='job' }` (app-wide vs. one job); then drill in
with `Get-JobStatus`, `Get-Slices -Query @{ state='Running' }`, and `Get-History`
(did a setting like `maxParallelism` change?). For dependency stalls use
`Get-Dependencies`.

## Authoring the schedule JSON

The schedule contract is identical to the one enforced by
`ko-lite-schedule-json`. Treat `docs\schedule-json.md` as the source of truth and
the sibling skill at `.github\skills\ko-lite-schedule-json\SKILL.md` as the
detailed field-by-field guide. Key points:

- Required: `activityId`, `functionName`, `outputTable`, `queryWindowSize`,
  `delayFromUtcNow`, `maxParallelism`, `queryTimeout`, `startFrom`, `target`
  (`clusterUri` + `database`).
- Optional: `id` (GUID permanent identity — omit when creating; KO Lite mints it),
  `endOn`, `isPaused`, `description` (Markdown catalog metadata, maximum 65,536
  characters, never passed to Kusto), `folder`, `tags`, `dependsOn`, `jobSettings`,
  `healthPolicy` (`complete` default, or `recent`).
- Unknown top-level, `target`, or `dependsOn` fields are rejected. `dependsOn`
  entries reference an upstream by `id` and/or `activityId`.
- For an **update**, fetch the current job first (`Get-Job`), keep its `id`, edit
  the `schedule` object, and re-import it. Never change `id`; `queryWindowSize` and
  `startFrom` are rejected on a job whose `hasStarted` is `true`. To **rename**,
  keep the same `id` and change `activityId` (allowed even after the job started).

## Workflow (create or update a schedule)

1. **Confirm intent and scope.** Is this a create or an update? Confirm the
   target `activityId` and the specific fields to change. For a started job, ask
   whether completed slices must run under the new definition. If the request implies
   a "will not" action, stop.
2. **Confirm the app is reachable.** Run the `Health` action; note `databasePath`
   so the user knows which instance you are changing.
3. **Read current state.** For an update, get the permanent GUID from `Get-Jobs`,
   then run `Get-Job -JobId <guid>` and edit the returned `schedule`. If only the
   exact `activityId` is known, `Get-Job` can resolve it once; use the returned
   `job.jobId` for subsequent reads and writes. For a broad change, `Export`
   everything first.
4. **Author/edit the JSON** per the contract above. Write it to a temp file or a
   path the user names.
5. **Validate locally.** The Import action validates automatically with the
   schedule-json validator before sending. Do not pass `-SkipValidation` unless
   the user explicitly asks.
6. **Import.** Run the `Import` action with `-Path` or `-Json`. Report the
   returned `created`/`updated`/`total` counts and remind the user nothing was
   deleted.
7. **Verify.** Re-read with `Get-Job` (or `Get-Jobs`) and confirm the change.

## Replacing a job to replay completed history

Use this workflow only when completed slices must be planned again under a new
definition. Hard-delete is permanently destructive local state and is always a
manual dashboard action.

1. **Scope the replacement.** Read the current job, its permanent GUID, the desired
   replay floor/range, and its direct/transitive downstream dependents. Decide which
   downstream jobs also need their completed slices replayed. Separately call out
   how existing Kusto output will be cleaned up, replaced, or deduplicated; this skill
   never performs that Kusto work.
2. **Pause and drain.** With approval, import the root and every affected downstream
   schedule with `isPaused: true`. Use `Get-JobStatus`, `Get-Slices`, and
   `Get-JobQueue` to verify no actively leased or running work remains. Inert queued
   retries may remain after pause and will be purged by hard-delete.
3. **Choose a safe dependency path.**
   - Replacing only the upstream: keep downstream jobs paused. Warn that soft-delete
     is initially blocked by active dependents, while hard-delete will not warn and
     will leave their GUID edges stale.
   - Replacing the whole closure: delete manually in reverse topological order
     (downstream first) and recreate in topological order (upstream first).
4. **Stop for the user.** Show the exact job names and permanent GUIDs. Ask the user
   to use the KO Lite dashboard to soft-delete each job (choosing **Soft delete
   anyway** where blocked) so it is disabled, then hard-delete it in the stated
   order. Never call or invent a hard-delete API, automate the dashboard, script the
   action, or edit SQLite directly. Do not continue until the user explicitly
   confirms completion.
5. **Create new identities while paused.** Remove each deleted job's old `id` from
   its schedule, keep its `activityId` unless the user wants a new logical name, and
   import it with `isPaused: true`. Verify the response reports `created`, then
   `Get-Job` and record the newly minted permanent GUID.
6. **Rebind every dependency.** Before resuming anything, update each paused
   downstream schedule so `dependsOn.id` references the replacement GUID. Re-read
   the live definitions and search the maintained schedule artifact for every old
   GUID. Recreating an upstream with the same `activityId` does not automatically
   repair an existing edge that is stored by GUID.
7. **Resume and verify.** Recreate/replay downstream jobs whose completed history
   must also run again, resume in dependency order, and verify new slices are
   progressing without failed, dead-lettered, or `DependencyBlocked` work.

## Using the helper script

```powershell
$skill = '.\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1'

# Confirm the app and see where its DB lives
& $skill -Action Health

# List jobs
$jobs = @(& $skill -Action Get-Jobs)

# Inspect one job (summary + canonical schedule)
$summary = $jobs | Where-Object { $_.displayName -ceq 'CopilotUsage.GhcpReportingUserDaily' }
$job = & $skill -Action Get-Job -JobId $summary.jobId

# Export all active jobs (import-compatible)
& $skill -Action Export

# Create or update from a file (validated locally first)
& $skill -Action Import -Path .\my-job.json

# Soft-delete a job (reversible), then restore it. Read its current version first.
& $skill -Action Soft-Delete -JobId $job.job.jobId -ExpectedVersion $job.job.catalogVersion
# ...and restore it later (use the catalogVersion returned by the soft-delete).
& $skill -Action Restore     -JobId $job.job.jobId -ExpectedVersion <version>

# --- Read-only diagnostics (no mutation) ---

# Triage a "job stopped running" report: lease-pinned slices, worker pool, app-vs-job throughput.
& $skill -Action Get-RunningSlices
& $skill -Action Get-WorkerPool
& $skill -Action Get-Throughput -Query @{ bucket = '30m'; groupBy = 'job' }

# Drill into one job (JobId accepts the GUID or the activityId).
& $skill -Action Get-JobStatus -JobId 'SampleAnalytics.BuildEcu5MinProfile'
& $skill -Action Get-Slices    -JobId 'SampleAnalytics.BuildEcu5MinProfile' -Query @{ state = 'Running' }
& $skill -Action Get-History   -JobId 'SampleAnalytics.BuildEcu5MinProfile'   # did maxParallelism change?
& $skill -Action Get-JobLogs   -JobId 'SampleAnalytics.BuildEcu5MinProfile' -Query @{ level = 'Warning' }

# Rerun/repair history (read-only) and the audit trail.
& $skill -Action Get-Reruns
& $skill -Action Get-Audit -Query @{ action = 'RerunExecuted' }

# Point at a non-default instance
& $skill -Action Get-Jobs -BaseUrl 'http://127.0.0.1:5099'
```

The script uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference =
'Stop'`. On a 400 it surfaces the API's `error` message verbatim; if the app is
unreachable it tells you to start it.

## Hard constraints

- **Writes are schedule upsert plus soft-delete/restore.** Schedule create/update
  goes through Import (including pause/resume via the `isPaused` field); soft-delete
  and restore go through their own endpoints and always require `expectedVersion`.
  Never **hard-delete** a job (no API surface — that stays a dashboard action), and
  never call any Kusto, rerun, cleanup, or repair surface, or edit the SQLite file
  directly. Reading diagnostics (including rerun/repair/audit **history**) is fine;
  *triggering* those actions is not.
- **Validate before writing.** Always validate the JSON locally before POSTing
  (the Import action does this by default).
- **Respect the identity model.** For a normal update, never change the permanent
  `id`; `queryWindowSize` and `startFrom` are read-only on started jobs, while
  `activityId` may be renamed with the same `id`. A replacement is a separate
  delete-then-create boundary: only after the user confirms manual hard-delete,
  omit the old `id` so KO Lite mints a new one. Never reuse or invent the deleted
  GUID.
- **No secrets.** The JSON describes a job, not credentials.
- **Confirm before writing.** Summarize the exact create/update you will apply and
  get the user's go-ahead before importing.

## Stop and ask conditions

- The KO Lite app is not reachable and the user has not provided a `-BaseUrl`.
- The requested action is outside scope (hard-delete, generic enable/disable of a
  job's `isEnabled` lifecycle, Kusto, rerun, repair). Pausing/resuming via `isPaused`,
  and soft-delete/restore, are in scope.
- A started-job update is requested but it is unclear whether completed slices
  should remain completed or be replayed under the new definition.
- A replacement is waiting on the user's manual hard-delete confirmation, or an
  affected downstream job is not paused/rebound to the replacement GUID.
- An update would change the permanent `id`, or change `queryWindowSize`/`startFrom`
  on a started job.
- `activityId` collides with an existing job. A **rename** (same `id`, new
  `activityId`) is supported; stop only if the intent (rename vs. a distinct new job
  vs. editing the existing one) is unclear.
- A requested schedule field is not in the supported contract.

## Related

- `docs\local-api.md` - the local API contract this skill drives.
- `docs\schedule-json.md` - authoritative schedule contract.
- `.github\skills\ko-lite-schedule-json\SKILL.md` - file-only schedule authoring.
- `README.md` - KO Lite overview.
