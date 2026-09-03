---
name: ko-lite-job-manager
description: "Use when the user wants an agent to read KO Lite jobs, inspect read-only operational diagnostics, create/update/pause/resume/soft-delete/restore jobs, or repair Failed/DeadLettered work in a running local KO Lite app. Drives the loopback-only /api/v1 JSON surface with ETag and repair-preview safeguards. Never hard-deletes, whole-slice reruns, runs Kusto cleanup, or performs arbitrary Kusto writes."
metadata:
  author: Azure Core Team
  version: "2.0.0"
---

# KO Lite job manager

Use this skill only against a running local KO Lite app. It drives the versioned
agent API through:

```powershell
.\.github\skills\ko-lite-job-manager\scripts\Invoke-KoLiteJobApi.ps1
```

The generated OpenAPI document at `/api/v1/openapi/v1.json` is the endpoint-shape
source of truth. `docs\local-api.md` explains workflows and safety.

## Safety boundary

Allowed:

- read jobs, detailed system status, queue/slice/chunk/attempt/event/log/audit
  diagnostics, throughput, failures, rerun history, repair history, and dependencies;
- create or replace one validated schedule;
- additive/update-only batch import and compatible export;
- explicit pause/resume;
- reversible soft-delete/restore;
- repair only `Failed`/`DeadLettered` slices or chunks after preview approval.

Never:

- hard-delete a job;
- whole-slice rerun or acknowledge/execute Kusto cleanup;
- mark work complete without execution;
- run arbitrary Kusto commands or write Kusto directly;
- widen the loopback binding or bypass ETag/preview safeguards;
- mutate the SQLite database directly.

## Core workflow

1. Confirm the app and API version:

   ```powershell
   & $helper -Action Health
   & $helper -Action System-Status
   ```

2. Read before writing:

   ```powershell
   $jobs = & $helper -Action Get-Jobs
   $job = & $helper -Action Get-Job -JobId $id
   ```

3. Use first-class single-job actions for ordinary changes:

   ```powershell
   & $helper -Action Create -Path .\job.json
   & $helper -Action Update -JobId $id -Path .\job.json
   & $helper -Action Pause -JobId $id -Reason 'maintenance'
   & $helper -Action Resume -JobId $id -Reason 'maintenance complete'
   ```

   The helper reads the current ETag and sends `If-Match`. Missing/stale
   preconditions are not retried blindly.

4. Use Import only for a schedule file or multi-job batch:

   ```powershell
   & $helper -Action Import -Path .\jobs.json
   ```

   A single object or array is validated locally and normalized to the API's
   `{ schedules: [...] }` envelope. Import is additive/update-only.

5. For large operational reads, request bounded pages and opt in to continuation:

   ```powershell
   & $helper -Action Get-Logs -JobId $id -Query @{ limit = 200 } -AllPages
   & $helper -Action Get-Slices -JobId $id -Query @{ state = 'Running'; limit = 100 }
   ```

   The helper follows only server-provided opaque `nextCursor` values and never
   reuses them with different filters.

## Job identity and lifecycle

- The permanent identity is the GUID `jobId`.
- `activityId` is a mutable, unique label. `Get-Job` may resolve one exact
  `activityId`, but every path call is GUID-keyed.
- Summary lifecycle is `active`, `paused`, or `softDeleted`.
- Create returns `201`; repair returns `202` and a durable `Location`.
- Single-job reads/mutations return an ETag derived from `catalogVersion`.
- Update, pause, resume, soft-delete, and restore require `If-Match`.
- Update and batch import reject soft-deleted targets; restore them explicitly first.
- Preserve the optional schedule `distributed` boolean on edits. It defaults to
  `false`; `true` opts only that job into Kusto distributed output.

Soft-delete retains the active-dependent guard. Use `-Force` only after naming
the dependent jobs and confirming the user accepts the break.

## Repair workflow

Repair fills failed gaps without Kusto cleanup and preserves successful siblings.
Always preview first:

```powershell
$preview = & $helper -Action Preview-Repair -JobId $id `
    -From '2026-01-01T00:00:00Z' -To '2026-01-02T00:00:00Z'

& $helper -Action Repair -JobId $id `
    -From '2026-01-01T00:00:00Z' -To '2026-01-02T00:00:00Z' `
    -Reason 'Requeue transient failures' `
    -ExpectedSliceCount $preview.repairableSliceCount `
    -ExpectedExecutionCount $preview.repairableExecutionCount `
    -PreviewToken $preview.previewToken
```

Preserve raw 0-based chunk IDs. A preview conflict means state changed; preview
again rather than altering counts/tokens.

## Problem Details

Errors are `application/problem+json` with stable `code` values, including:

- `job-not-found`
- `if-match-required`
- `etag-mismatch`
- `active-dependents`
- `invalid-cursor`
- `invalid-filter`
- `repair-preview-conflict`
- `job-not-active`

Report the HTTP status, `code`, `detail`, and relevant extensions. Do not parse
human text to infer conflict type.

## Action reference

Job actions:

```text
Health, System-Status, Get-Jobs, Get-Job, Create, Update, Export, Import,
Pause, Resume, Soft-Delete, Restore, Get-JobStatus,
Get-CatalogRevisions, Get-Dependencies
```

Repair:

```text
Preview-Repair, Repair
```

Operations:

```text
Get-WorkerPool, Get-Queue, Get-Slices, Get-RunningSlices, Get-Chunks,
Get-Attempts, Get-Events, Get-Logs, Get-Throughput, Get-Failures,
Get-Audit, Get-Reruns, Get-Rerun, Get-Repairs, Get-Repair
```

Use `-BatchId` for rerun/repair detail and `-Query` for filters such as `jobId`,
`state`, `from`, `to`, `level`, `category`, `limit`, and `cursor`.
