---
name: ko-lite-job-manager
description: "Use when the user wants an agent to read KO Lite jobs and create or update job schedules directly in a running KO Lite app (instead of clicking through the dashboard). Drives the KO Lite localhost JSON API and only reads or upserts schedules - it never enables/disables, deletes, runs Kusto, or reruns. Requires the KO Lite app to be running locally."
metadata:
  author: Azure Core Team
  version: "1.0.0"
---

# KO Lite job manager

Use this skill to **manage KO Lite jobs from an agent session** by talking to a
running KO Lite app's localhost JSON API. The deliverable is a real catalog
change in the local SQLite database, applied through the same validated import
path the dashboard uses.

This skill is the read/write counterpart to `ko-lite-schedule-json` (which only
authors a JSON file and never uploads). Use `ko-lite-schedule-json` when the user
just wants a file; use **this** skill when the user wants the change applied to a
running instance.

## What this skill will and will not do

It **will**:

- Read jobs: list summaries, fetch one job's canonical schedule, export all jobs.
- Create new jobs and update existing job schedules via upsert import.

It **will not** (these stay manual / dashboard-only on purpose):

- Enable/disable (pause/resume) jobs.
- Soft-delete, restore, or hard-delete jobs.
- Execute Kusto, run reruns, run cleanup, or run repair.
- Touch the SQLite file directly. All writes go through the API.

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

The API is **loopback-only** and intended for same-machine use.

**Execution awareness:** creating or updating an *enabled, unpaused* job means the
running scheduler may start scheduling and executing it - exactly like a dashboard
import. If the user wants to stage a schedule without running it, author the job
with `"isPaused": true`.

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
| GET | `/api/jobs/{jobId}` | One summary plus `schedule` (the canonical, import-compatible schedule object, including its `id`). `{jobId}` is the permanent GUID. 404 if missing. |
| GET | `/api/jobs/export` | Import-compatible JSON array of all non-soft-deleted jobs. |
| POST | `/api/jobs/import` | Body is schedule JSON (single object or array). Returns `{ created, updated, total, items[] }`. 400 with `{ error }` on validation/mutation failure. |

## Authoring the schedule JSON

The schedule contract is identical to the one enforced by
`ko-lite-schedule-json`. Treat `docs\schedule-json.md` as the source of truth and
the sibling skill at `.github\skills\ko-lite-schedule-json\SKILL.md` as the
detailed field-by-field guide. Key points:

- Required: `activityId`, `functionName`, `outputTable`, `queryWindowSize`,
  `delayFromUtcNow`, `maxParallelism`, `queryTimeout`, `startFrom`, `target`
  (`clusterUri` + `database`).
- Optional: `id` (GUID permanent identity — omit when creating; KO Lite mints it),
  `endOn`, `isPaused`, `folder`, `tags`, `dependsOn`, `jobSettings`.
- Unknown top-level, `target`, or `dependsOn` fields are rejected. `dependsOn`
  entries reference an upstream by `id` and/or `activityId`.
- For an **update**, fetch the current job first (`Get-Job`), keep its `id`, edit
  the `schedule` object, and re-import it. Never change `id`; `queryWindowSize` and
  `startFrom` are rejected on a job whose `hasStarted` is `true`. To **rename**,
  keep the same `id` and change `activityId` (allowed even after the job started).

## Workflow

1. **Confirm intent and scope.** Is this a create or an update? Confirm the
   target `activityId` and the specific fields to change. If the request implies a
   "will not" action, stop.
2. **Confirm the app is reachable.** Run the `Health` action; note `databasePath`
   so the user knows which instance you are changing.
3. **Read current state.** For an update, run `Get-Job -JobId <id>` and edit the
   returned `schedule`. For a broad change, `Export` everything first.
4. **Author/edit the JSON** per the contract above. Write it to a temp file or a
   path the user names.
5. **Validate locally.** The Import action validates automatically with the
   schedule-json validator before sending. Do not pass `-SkipValidation` unless
   the user explicitly asks.
6. **Import.** Run the `Import` action with `-Path` or `-Json`. Report the
   returned `created`/`updated`/`total` counts and remind the user nothing was
   deleted.
7. **Verify.** Re-read with `Get-Job` (or `Get-Jobs`) and confirm the change.

## Using the helper script

```powershell
$skill = '.\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1'

# Confirm the app and see where its DB lives
& $skill -Action Health

# List jobs
& $skill -Action Get-Jobs

# Inspect one job (summary + canonical schedule)
& $skill -Action Get-Job -JobId 'CopilotUsage.GhcpReportingUserDaily'

# Export all active jobs (import-compatible)
& $skill -Action Export

# Create or update from a file (validated locally first)
& $skill -Action Import -Path .\my-job.json

# Point at a non-default instance
& $skill -Action Get-Jobs -BaseUrl 'http://127.0.0.1:5099'
```

The script uses `Set-StrictMode -Version Latest` and `$ErrorActionPreference =
'Stop'`. On a 400 it surfaces the API's `error` message verbatim; if the app is
unreachable it tells you to start it.

## Hard constraints

- **Schedules only.** Read and upsert schedules. Never call any enable/disable,
  delete, Kusto, rerun, cleanup, or repair surface, and never edit the SQLite file
  directly.
- **Validate before writing.** Always validate the JSON locally before POSTing
  (the Import action does this by default).
- **Respect the identity model.** Never change the permanent `id`. `queryWindowSize`
  and `startFrom` are read-only on started jobs. `activityId` is a mutable display
  label and may be renamed (keep the same `id`).
- **No secrets.** The JSON describes a job, not credentials.
- **Confirm before writing.** Summarize the exact create/update you will apply and
  get the user's go-ahead before importing.

## Stop and ask conditions

- The KO Lite app is not reachable and the user has not provided a `-BaseUrl`.
- The requested action is outside scope (enable/disable, delete, Kusto, rerun).
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
