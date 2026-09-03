# KO Lite HTTP surfaces

KO Lite exposes three deliberately separate HTTP boundaries:

- **Agent API:** `/api/v1`, loopback-only JSON, generated OpenAPI, named contracts,
  Problem Details, and safe agent actions.
- **Browser/UI support:** canonical `/jobs` Razor pages plus the internal
  `/ui-api/v1` failure-analysis polling surface.
- **Health/control:** minimal `/healthz` and loopback-only `/control/v1` graceful drain.

The generated document at
`GET http://127.0.0.1:5057/api/v1/openapi/v1.json` is the source of truth for
request and response schemas, operation IDs, parameters, and status codes. This
document focuses on workflows and safety.

## Local request boundary

The agent API, generated OpenAPI document, detailed system status, UI-support
endpoints, and control endpoints use the same injectable local-request policy.
Production fails closed when a remote address is missing and accepts only loopback
addresses. `/healthz` is intentionally minimal and contains no database path or
runtime configuration.

KO Lite still binds to `http://127.0.0.1:5057` by default. Do not widen
`KoLite:Urls` as a substitute for authentication.

## Contract conventions

- Job path identity is always the permanent GUID. Exact `activityId` lookup is a
  `GET /api/v1/jobs?activityId=...` filter.
- Job summaries expose `activityId` and one lifecycle state: `active`, `paused`,
  or `softDeleted`. They do not expose the old overlapping `displayName` and
  lifecycle booleans.
- Single-job reads return `ETag: "catalog-N"`. `PUT` and lifecycle actions require
  that value in `If-Match`.
- Missing `If-Match` returns `428`; a stale ETag returns `412`.
- Errors use `application/problem+json` with a stable `code` extension and optional
  structured evidence such as `dependents`, `currentVersion`, or repair-preview
  conflict fields.
- Inbound instants require an explicit UTC offset (`Z` or `+/-HH:mm`). KO Lite
  emits UTC `Z` values.
- Schedule JSON may opt into distributed Kusto output with `"distributed": true`.
  The field defaults to `false`, is preserved by API create/update/import/export,
  and does not change producer function arguments.
- Large operational collections use `limit` plus an opaque `cursor`, returning
  `{ "items": [...], "nextCursor": "..." }`. A cursor is bound to its endpoint and
  filters and is rejected if reused with a different query.

## Agent API route map

All routes are under `/api/v1`.

### Jobs

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/jobs` | List jobs; optional exact `activityId` filter. |
| POST | `/jobs` | Create one job from `{ "schedule": { ... } }`; returns `201`, `Location`, and ETag. |
| GET | `/jobs/{jobId}` | Read one GUID-keyed job and canonical schedule; returns ETag. |
| PUT | `/jobs/{jobId}` | Replace one schedule using `{ "schedule": { ... } }` and `If-Match`. |
| POST | `/jobs/{jobId}/actions/pause` | Pause using `If-Match`; optional `{ "reason": "..." }`. |
| POST | `/jobs/{jobId}/actions/resume` | Resume using `If-Match`. |
| POST | `/jobs/{jobId}/actions/soft-delete` | Reversible soft delete; optional `force`; dependent guard retained. |
| POST | `/jobs/{jobId}/actions/restore` | Restore a soft-deleted job. |
| POST | `/jobs/import` | Additive/update-only batch import with `{ "schedules": [ ... ] }`; a soft-deleted target must be restored first. |
| GET | `/jobs/export` | Import-compatible array of non-soft-deleted schedules. |
| GET | `/jobs/{jobId}/status` | Catalog, slice-state, queue, and parallelism status. |
| GET | `/jobs/{jobId}/catalog-revisions` | Named catalog revision contracts. |
| GET | `/jobs/{jobId}/dependencies` | Declared dependencies and blocked-slice samples. |

Create/update/import all use the existing strict schedule parser and started-job
mutation policy. Import remains additive: omitted jobs are never deleted.

### Repair

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/jobs/{jobId}/repair-previews` | Read-only failed/dead-lettered preview. |
| POST | `/jobs/{jobId}/repairs` | Queue exactly the approved preview; returns `202` and a durable repair location. |

Repair preserves the existing count/token safety:

- `expectedSliceCount` is always required.
- `previewToken` is always required and binds approval to the previewed slice identities and
  versions.
- Chunked jobs also require the preview's `expectedExecutionCount`.
- Raw chunk IDs remain 0-based.
- Successful siblings and chunks with active automatic retry work are untouched.
- Paused or soft-deleted jobs are rejected.

The agent API does **not** expose whole-slice rerun, Kusto cleanup, hard delete,
or mark-complete operations.

### Operations

| Route | Notes |
| --- | --- |
| `/operations/worker-pool` | Current dispatcher/pool snapshot. |
| `/operations/queue` | Storage-bounded, cursor-paged queue rows; optional `jobId`, `queueName`, and `state`. |
| `/operations/slices` | Paged logical slice states; optional job/state/time filters. |
| `/operations/running-slices` | Bounded running projection with lease and timing evidence. |
| `/operations/chunks` | One slice's naturally bounded 0-32 child states/events. |
| `/operations/attempts` | Paged execution attempts. |
| `/operations/events` | Paged slice state events. |
| `/operations/logs` | Paged durable logs. |
| `/operations/throughput` | Bounded throughput buckets; not cursor-paged. |
| `/operations/failures` | Paged terminal failures plus summary-run evidence. |
| `/operations/audit-events` | Paged audit trail. |
| `/operations/reruns` and `/operations/reruns/{batchId}` | Separate list/detail resources. |
| `/operations/repairs` and `/operations/repairs/{batchId}` | Separate list/detail resources. |

Use the permanent GUID in `jobId` filters. Invalid enum values, instants, limits,
and cursors return Problem Details instead of being silently coerced.

### Kusto lineage and system

- `POST /dependency-graphs/kusto-lineage` performs the existing on-demand,
  read-only Kusto metadata lookup. It persists nothing and maps upstream failures
  to `502` Problem Details.
- `GET /system/status` returns detailed scheduler, worker-pool, retention, update,
  Kusto-auth, database, shutdown, and supported-API-version state.

## Browser routes

The dashboard remains `/`. Job pages and forms use one canonical hierarchy:

```text
/jobs
/jobs/new
/jobs/import
/jobs/soft-deleted
/jobs/{jobId}
/jobs/{jobId}/edit
/jobs/{jobId}/copy
/jobs/{jobId}/history
/jobs/{jobId}/slices
/jobs/{jobId}/rerun
/jobs/{jobId}/hard-delete
/jobs/{jobId}/soft-delete-confirm
```

Pause/resume/soft-delete/restore browser posts are under
`/jobs/{jobId}/actions/...` and remain antiforgery-protected. Hard delete and
whole-slice rerun remain browser-only with their existing confirmations.

Failure analysis uses
`/ui-api/v1/jobs/{jobId}/failure-analyses`; Razor renders that URL into a data
attribute and `site.js` does not hard-code it.

## Health and shutdown

- `GET /healthz` returns only `{ "status": "healthy" }` after a SQLite readiness
  check.
- `GET /api/v1/system/status` is the detailed local status resource.
- `GET /control/v1/shutdown` reads drain state.
- `POST /control/v1/shutdown/drain` accepts `{ "reason": "..." }`, stops new
  scheduling/claims, waits for active work to record final state, then stops the app.

Use `scripts\Stop-KoLiteApp.ps1` instead of calling control routes by hand.

## PowerShell helper

The `ko-lite-job-manager` skill wraps the v1 API:

```powershell
$helper = '.\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1'

& $helper -Action System-Status
& $helper -Action Get-Jobs
& $helper -Action Create -Path .\job.json
& $helper -Action Update -JobId $jobId -Path .\job.json
& $helper -Action Pause -JobId $jobId -Reason 'maintenance'
& $helper -Action Get-Logs -JobId $jobId -Query @{ limit = 200 } -AllPages
```

The helper captures ETags automatically, sends `If-Match`, parses Problem Details,
normalizes import files into the `schedules` envelope, follows cursors only when
`-AllPages` is requested, and reports API-version mismatch before writes.

## Compatibility and upgrades

The v1 redesign intentionally removes the former `/api/jobs`, `/api/diagnostics`,
`/status/health`, `/status/shutdown`, and `/catalog` routes. Upgrade the application,
root operational scripts, and `.github\skills` directory together from the same
release or repository commit.

- An old helper or script pointed at a v1 app fails because its legacy route returns
  `404`; it cannot silently fall through to a different operation.
- The v1 job-manager helper checks `/api/v1/system/status` before every write and
  refuses to mutate an older or unsupported app.
- Copilot sessions can retain already-loaded skill and agent instructions. Start a
  fresh session after upgrading, or explicitly reload the project skill, before
  managing jobs.
- Do not copy a new executable over an old release folder while retaining the old
  scripts or `.github\skills`. Extract the complete archive into a new or cleaned
  application folder.
