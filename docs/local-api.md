# KO Lite local management API

KO Lite exposes a small **localhost-only JSON API** so a same-machine agent or
tool can read jobs and create/update schedules without clicking through the
dashboard. The API is hosted by the running app, so it starts and stops with the
dashboard, scheduler, and worker.

A companion **read-only diagnostics API** (`/api/diagnostics/...` and
`/api/jobs/{jobId}/...`) surfaces the operational read models that otherwise only
render as dashboard HTML, so an agent can investigate a job (slice states, leases,
throughput, history, logs, audit, reruns/repairs) entirely over HTTP. See
[Read-only diagnostics](#read-only-diagnostics) below.

The companion agent skill `ko-lite-job-manager` drives this API; the file-only
authoring skill `ko-lite-schedule-json` does not upload.

## Scope and safety

- **Schedules, plus soft-delete / restore.** The API can read jobs, create/update
  schedules, and **soft-delete or restore** a job. Soft-delete is reversible (it
  flips `is_enabled` off and records a lifecycle event; no rows are purged). It
  exposes **no** hard-delete, generic enable/disable, Kusto execution, rerun,
  cleanup, or repair surface. (The separate, read-only
  `POST /api/dependency-graph/kusto-consumers` endpoint issues a read-only Kusto
  metadata query for the dependency graph — see the operations runbook.)
- **Guarded delete.** Soft-delete and restore require the job's current
  `expectedVersion` (optimistic concurrency; a mismatch is a `409`). Soft-delete is
  **blocked by default** when active downstream jobs depend on the target and
  returns `409` listing them; pass `"force": true` to override (mirrors the
  dashboard's "Soft delete anyway" confirm).
- **Validated path.** Every write goes through the same
  `SqliteJobCatalogRepository.Import` path the dashboard import uses, so strict
  schedule parsing, started-job mutation policy (immutable permanent `id`;
  `queryWindowSize`/`startFrom` read-only after a job starts; `activityId` is a
  mutable display label), catalog versioning, audit events, and tag normalization
  all apply, inside one transaction.
- **Additive and update-only.** An item matches an existing job by `id` when present
  (a **rename** is same `id`, new `activityId`), else by `activityId`; new ones are
  created (a supplied `id` is preserved, else minted); omitted jobs are never deleted.
- **Loopback-only.** Every `/api` route is restricted to loopback callers. The
  app also binds `http://127.0.0.1:5057` by default. There is no auth token; the
  loopback boundary is the control.
- **Execution awareness.** Importing an *enabled, unpaused* job lets the running
  scheduler start scheduling/executing it, exactly like a dashboard import. To
  stage a schedule without running it, set `"isPaused": true`.

## Endpoints

Base URL defaults to `http://127.0.0.1:5057`.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/jobs` | `{ "jobs": [ ... ] }` of summaries: `jobId` (permanent GUID), `displayName` (the activityId label), `isEnabled`, `isSoftDeleted`, `hasStarted`, `isPaused`, `tags`, `target { clusterUri, database }`, `catalogVersion`, `createdAtUtc`, `updatedAtUtc`. |
| GET | `/api/jobs/{jobId}` | `{ "job": { ...summary }, "schedule": { ...canonical import-compatible object, including its `id` and optional `description` } }`. `{jobId}` is the permanent GUID. `404` with `{ "error" }` when the job does not exist. |
| GET | `/api/jobs/export` | Import-compatible JSON **array** of every non-soft-deleted job (same payload as the dashboard **Export all**). |
| POST | `/api/jobs/import` | Body is schedule JSON (single object **or** array). Returns `{ "created", "updated", "total", "items": [ { "jobId", "action", "catalogVersion" } ] }`. `400` with `{ "error" }` on JSON, validation, or mutation-policy failure. |
| POST | `/api/jobs/{jobId}/soft-delete` | Soft-delete (hide) a job — reversible. `{jobId}` is the permanent GUID. Body `{ "expectedVersion": <current catalogVersion, required>, "reason"?, "force"? }`. Returns `{ "job": { ...summary, "isSoftDeleted": true } }`. Errors: `400` (missing/invalid body or absent `expectedVersion`), `404` (unknown job), `409` (version conflict), or `409` `{ "error", "dependents": [ { "jobId", "activityId" } ] }` when active downstream jobs depend on it and `force` is not `true`. |
| POST | `/api/jobs/{jobId}/restore` | Restore (un-hide) a soft-deleted job. `{jobId}` is the permanent GUID. Body `{ "expectedVersion": <required>, "reason"? }`. Returns `{ "job": { ...summary, "isEnabled": true } }`. Errors: `400`/`404`/`409` as above (no dependents check). |

The potentially large `description` value is not duplicated into `GET /api/jobs`
summaries. Read it from the single-job `schedule` object or an export. It is Markdown
catalog metadata only and is never forwarded to Kusto execution.

The database path is also reported as `databasePath` by `GET /status/health`,
which an agent can read to confirm which instance it is talking to. The
`scripts\Get-KoLiteDatabase.ps1` helper prints this path directly (and falls
back to a best-effort guess when the app is stopped).

## Examples

```powershell
# Confirm the app is up and learn where its database lives.
Invoke-RestMethod http://127.0.0.1:5057/status/health |
    Select-Object status, databasePath, jobCount

# List jobs and resolve the permanent GUID from the human activityId label.
$jobs = Invoke-RestMethod http://127.0.0.1:5057/api/jobs |
    Select-Object -Expand jobs
$jobId = ($jobs | Where-Object { $_.displayName -ceq 'Demo.SkillTest' }).jobId

# Inspect one job's canonical schedule.
Invoke-RestMethod "http://127.0.0.1:5057/api/jobs/$jobId" |
    Select-Object -Expand schedule

# Create or update from a file (single object or array).
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5057/api/jobs/import `
    -ContentType 'application/json' -InFile .\my-job.json

# Soft-delete a job (reversible). Read its current catalogVersion first.
$job = Invoke-RestMethod "http://127.0.0.1:5057/api/jobs/$jobId"
Invoke-RestMethod -Method Post `
    -Uri "http://127.0.0.1:5057/api/jobs/$($job.job.jobId)/soft-delete" `
    -ContentType 'application/json' `
    -Body (@{ expectedVersion = $job.job.catalogVersion } | ConvertTo-Json)

# ...then restore it (use the catalogVersion returned by the soft-delete).
Invoke-RestMethod -Method Post `
    -Uri "http://127.0.0.1:5057/api/jobs/$($job.job.jobId)/restore" `
    -ContentType 'application/json' `
    -Body (@{ expectedVersion = <version> } | ConvertTo-Json)
```

Prefer the skill helper, which validates the schedule JSON locally before
sending and surfaces API errors clearly:

```powershell
$skill = '.\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1'
& $skill -Action Health
$jobs = @(& $skill -Action Get-Jobs)
$job = & $skill -Action Get-Job -JobId $jobs[0].jobId
& $skill -Action Import -Path .\my-job.json
& $skill -Action Soft-Delete -JobId <jobId> -ExpectedVersion <catalogVersion>
& $skill -Action Restore     -JobId <jobId> -ExpectedVersion <catalogVersion>
```

The helper keeps the permanent GUID as the normal reference. For a read where
only the exact `activityId` is known, `Get-Job` resolves the matching
`displayName` through `GET /api/jobs` and then calls the GUID-keyed route. Use the
returned `job.jobId` for subsequent operations.

## Read-only diagnostics

A strictly **read-only** family of endpoints surfaces the operational read models
that power the dashboard, plus a few cross-job and time-bucketed queries that the
HTML pages do not expose. It performs **no** writes, no Kusto, and no
scheduler/rerun/repair mutation — it only reads existing local state. Every route
is **loopback-only** (same guard as the catalog API).

**Bounded by default.** List and time-series routes are capped and windowed so a
single call never scans the whole local store:

- `take` — row cap, clamped to `[1, 1000]` (default `100`; `slices`/`throughput`
  default higher but never exceed the cap).
- `from` / `to` — ISO-8601 UTC bounds. Log/throughput/audit routes default to the
  **last 24h** when omitted.
- `bucket` — time-series bucket size: `5m`, `30m`, `1h`, `90s`, or plain seconds
  (clamped to `[60s, 1d]`; default `30m`).
- Filters: `state`, `level`, `category`, `action`, `subjectType`, `subjectId`,
  `groupBy=job` (throughput), `jobId` (global routes), `batchId` (rerun/repair detail).

`{jobId}` accepts the permanent GUID **or** the mutable `activityId` (mirroring the
dashboard's bookmark redirect). Unknown jobs return `404` with `{ "error" }`.

### Per-job — `/api/jobs/{jobId}/…`

| Route | Returns |
| --- | --- |
| `GET …/status` | Identity + `maxParallelism`/paused/started + slice-state counts (`missing/queued/running/completed/failed/deadLettered/dependencyBlocked`) + queue counts. |
| `GET …/slices` | Materialized slice states **with lease fields** (`leaseOwner`, `leaseExpiresAtUtc`, `leaseExpired`, `attempt`, `lastError*`). Filters: `state`, `from`, `to`, `take`. |
| `GET …/attempts` | Recent slice attempts (incl. in-flight `Started` rows with no `completedAtUtc`). Optional exact slice via `start`/`end`; `take`. |
| `GET …/events` | Slice-state event timeline. Optional exact slice via `start`/`end`; `take`. |
| `GET …/logs` | Operational logs. Filters: `level`, `category`, `from`, `to`, `take`. |
| `GET …/queue` | Work-queue items for the job incl. `lockedBy`/`lockedUntilUtc`. |
| `GET …/history` | Catalog version history **with a computed JSON diff** per version (e.g. a `maxParallelism` change). |
| `GET …/throughput` | Succeeded-completion series bucketed over `[from, to)` + a throughput `sample`. Params: `from`, `to`, `bucket`. |
| `GET …/dependencies` | Declared upstreams (resolved) + a live-evaluated sample of `DependencyBlocked` slices with their missing upstream slices. |

### Cross-job / global — `/api/diagnostics/…`

| Route | Returns |
| --- | --- |
| `GET …/worker-pool` | Worker-pool snapshot (same shape as `GET /status/health.workerPool`): in-flight workers, queued/leased/**expired-lease** counts, saturation, cycle counters. |
| `GET …/running-slices` | **All** currently `Running` slices (optionally `jobId`) with lease owner/expiry, **oldest first** — the fingerprint of a stalled, lease-pinned job. |
| `GET …/throughput` | Global completion series; `groupBy=job` splits each bucket per job ("is the whole app stalled or just one job?"). |
| `GET …/queue` | Queue status summary (`queued/leased/completed/deadLettered/expiredLease`). |
| `GET …/logs` | Operational logs across all jobs. Filters: `jobId`, `level`, `category`, `from`, `to`, `take`. |
| `GET …/failures` | Recent failed/dead-lettered slices + persisted failure-summary runs. |
| `GET …/audit` | System audit trail (rerun planned/executed, lifecycle, …). Filters: `subjectType`, `subjectId`, `action`, `from`, `to`. |
| `GET …/reruns` | Rerun-batch listing (`jobId` filter); `?batchId=` returns one batch with its slices. |
| `GET …/repairs` | Repair-batch listing (`jobId` filter); `?batchId=` returns that batch's repair slices. |

### Examples

```powershell
$base = 'http://127.0.0.1:5057'

# Is the whole app stalled, or just one job? Compare global vs. per-job throughput.
Invoke-RestMethod "$base/api/diagnostics/throughput?bucket=30m&groupBy=job" |
    Select-Object -Expand buckets

# Find slices pinned by hung leases (oldest first) - the stall fingerprint.
Invoke-RestMethod "$base/api/diagnostics/running-slices" |
    Select-Object -Expand runningSlices |
    Format-Table jobId, leaseOwner, leaseExpired, updatedAtUtc

# Per-job status, lease-bearing slices, and the catalog diff that changed maxParallelism.
Invoke-RestMethod "$base/api/jobs/SampleAnalytics.BuildEcu5MinProfile/status"
Invoke-RestMethod "$base/api/jobs/SampleAnalytics.BuildEcu5MinProfile/slices?state=Running"
Invoke-RestMethod "$base/api/jobs/SampleAnalytics.BuildEcu5MinProfile/history" |
    Select-Object -Expand history
```

## Related

- [schedule-json.md](schedule-json.md) — the schedule contract the import path enforces.
- [operations-runbook.md](operations-runbook.md) — safe local runs and diagnostics.
- [local-first-architecture.md](local-first-architecture.md) — component responsibilities and safety boundaries.
