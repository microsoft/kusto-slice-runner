# KO Lite local management API

KO Lite exposes a small **localhost-only JSON API** so a same-machine agent or
tool can read jobs and create/update schedules without clicking through the
dashboard. The API is hosted by the running app, so it starts and stops with the
dashboard, scheduler, and worker.

The companion agent skill `ko-lite-job-manager` drives this API; the file-only
authoring skill `ko-lite-schedule-json` does not upload.

## Scope and safety

- **Schedules only.** The API can read jobs and create/update schedules. It
  exposes **no** enable/disable, soft/hard delete, Kusto execution, rerun,
  cleanup, or repair surface.
- **Validated path.** Every write goes through the same
  `SqliteJobCatalogRepository.Import` path the dashboard import uses, so strict
  schedule parsing, started-job mutation policy (immutable `activityId`,
  `queryWindowSize`, `startFrom`), catalog versioning, audit events, and tag
  normalization all apply, inside one transaction.
- **Additive and update-only.** Matching `activityId`s are updated, new ones are
  created, and omitted jobs are never deleted.
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
| GET | `/api/jobs` | `{ "jobs": [ ... ] }` of summaries: `jobId`, `displayName`, `isEnabled`, `isSoftDeleted`, `hasStarted`, `isPaused`, `tags`, `target { clusterUri, database }`, `catalogVersion`, `createdAtUtc`, `updatedAtUtc`. |
| GET | `/api/jobs/{jobId}` | `{ "job": { ...summary }, "schedule": { ...canonical import-compatible object } }`. `404` with `{ "error" }` when the job does not exist. |
| GET | `/api/jobs/export` | Import-compatible JSON **array** of every non-soft-deleted job (same payload as the dashboard **Export all**). |
| POST | `/api/jobs/import` | Body is schedule JSON (single object **or** array). Returns `{ "created", "updated", "total", "items": [ { "jobId", "action", "catalogVersion" } ] }`. `400` with `{ "error" }` on JSON, validation, or mutation-policy failure. |

The database path is also reported as `databasePath` by `GET /status/health`,
which an agent can read to confirm which instance it is talking to.

## Examples

```powershell
# Confirm the app is up and learn where its database lives.
Invoke-RestMethod http://127.0.0.1:5057/status/health |
    Select-Object status, databasePath, jobCount

# List jobs.
Invoke-RestMethod http://127.0.0.1:5057/api/jobs | Select-Object -Expand jobs

# Inspect one job's canonical schedule.
Invoke-RestMethod http://127.0.0.1:5057/api/jobs/Demo.SkillTest |
    Select-Object -Expand schedule

# Create or update from a file (single object or array).
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5057/api/jobs/import `
    -ContentType 'application/json' -InFile .\my-job.json
```

Prefer the skill helper, which validates the schedule JSON locally before
sending and surfaces API errors clearly:

```powershell
$skill = '.\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1'
& $skill -Action Health
& $skill -Action Get-Jobs
& $skill -Action Import -Path .\my-job.json
```

## Related

- [schedule-json.md](schedule-json.md) — the schedule contract the import path enforces.
- [operations-runbook.md](operations-runbook.md) — safe local runs and diagnostics.
- [local-first-architecture.md](local-first-architecture.md) — component responsibilities and safety boundaries.
