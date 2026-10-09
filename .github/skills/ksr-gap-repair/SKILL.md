---
name: ksr-gap-repair
description: "Recover historical Kusto Slice Runner Failed/DeadLettered gaps after a job is healthy again. Use for cleanup, failed-slice backfill, or dependency-aware recovery of selected jobs or groups. Requires three recent successful logical slices for windows up to one hour, or one for longer windows. healthPolicy=recent jobs are report-only unless explicitly overridden. Reuses ksr-job-manager; never replays successful slices or performs Kusto cleanup."
metadata:
  author: Azure Core Team
  version: "1.0.0"
---

# Kusto Slice Runner gap repair

Fill terminal execution gaps, not replace successful output. Use the installed
[ksr-job-manager](../ksr-job-manager/SKILL.md) and its existing
`scripts\Invoke-KsrJobApi.ps1` helper. This skill adds a recovery workflow, not
another API client, scheduler, or definition of dashboard health.

The generated `/api/v1/openapi/v1.json` is the endpoint-shape authority.
Repository references: [API contracts](../../../docs/local-api.md),
[schedule fields](../../../docs/schedule-json.md), and
[repair operations](../../../docs/operations-runbook.md#requeuing-slices-that-already-dead-lettered).
In an extracted release without these docs, use the bundled job-manager skill
and the running app's OpenAPI; never assume a missing API capability exists.

## Boundary and authorization

The only app mutation in this workflow is **Repair** of terminal failed work.
Never perform whole-slice rerun, successful-slice replay, Kusto cleanup or other
Kusto writes, SQLite edits, hard delete, mark-complete, producer/schema changes,
job updates, or automatic pause/resume/restore. Do not start, upgrade, stop or
reconfigure the app. If replacing successful output is actually required,
explain that the separate operator-only rerun workflow is outside this skill.

Normally present one combined, exact repair manifest for approval. An explicit
prompt instruction such as **"do the work without waiting for approval"**
preauthorizes the bounded cleanup for that invocation: record that instruction,
summarize the manifest and proceed without an extra approval question. Follow
any stricter applicable environment or repository authorization rules.

Preauthorization does **not** waive health checks, previews, scope limits, or
the `healthPolicy: "recent"` exclusion below. Unclear job scope still requires
clarification. Newly added targets, broadened scope, or changed output bindings
require renewed authorization; they are not covered by an earlier manifest.

## 1. Select jobs and inventory gaps

Confirm `/healthz`, API v1, and execution-enabled system status. Resolve the
user's selected jobs or explicit tag/group from the live catalog to permanent
GUIDs. Never silently select the entire app. Read current canonical schedules,
target clusters/databases, functions, output tables, lifecycle and dependencies.
Report paused/soft-deleted jobs rather than changing their lifecycle.

By default, scan **all retained terminal gaps** in the selected jobs, with
bounded pages and opaque cursor continuation. An optional repair date range uses
half-open UTC data-window bounds. If the user instead asks when failures
occurred, distinguish execution/event timestamps from data-window timestamps;
collection delay can put an older data slice's failure inside a recent period.

Keep these populations separate:

- Terminal logical `Failed`/`DeadLettered` slices, identified by GUID/start/end.
- Terminal child chunks, with raw **0-based** IDs, inside those logical slices.
- Failed attempts and transient `Failed` events that recovered: not gaps.
- `DependencyBlocked` slices: downstream consequences, not execution failures.

Use `Get-Failures`, current slices, chunks, attempts and queue evidence as
needed. `Failed` alone does not prove automatic retries have ended. Read-only
repair previews determine which failed work is actually repairable and exclude
active retries. Do not count the same logical window once per failed chunk.
Inspect the error before selecting repair; a healthy streak does not prove every
historical input exists or a persistent semantic error was fixed.

## 2. Apply the job's completeness policy

Normalize the live `healthPolicy` value case-insensitively:

| Policy                                          | Historical-gap handling                               |
| ----------------------------------------------- | ----------------------------------------------------- |
| `complete`, or omitted (the documented default) | Assess health and repair eligibility                  |
| `recent`                                        | **Report-only by default**, even if currently healthy |
| Unknown/unreadable value                        | Report and skip; never assume `complete`              |

This applies to **roots and every downstream job**. General cleanup wording,
selection by job name, approval of a cleanup plan, and "do not wait for
approval" do not override `recent`. The user must explicitly follow up asking to
repair the named recent-policy job or gaps. Record that scoped exception; keep
its policy unchanged and still apply the health and preview gates.

If an excluded recent-policy upstream prevents a complete-policy descendant from
recovering, report the blocker. Do not repair it implicitly or alter
dependencies. Ordinary scheduler release of a downstream blocked slice after an
authorized upstream repair is not a manual repair of that downstream job.

## 3. Prove recent health

Use the job's `queryWindowSize` as a **TimeSpan**, not a string comparison:

| Window size                                    | Required health evidence                                         |
| ---------------------------------------------- | ---------------------------------------------------------------- |
| At most `01:00:00`, including exactly one hour | The newest **three resolved logical slices** are all `Completed` |
| Longer than `01:00:00`                         | The newest **one resolved logical slice** is `Completed`         |

A resolved slice is Completed or terminally failed after automatic work has
ended. Sort by logical UTC slice start/end, newest first. Do not filter to
Completed first or skip a newer terminal failure to find older successes. A
chunked parent counts **once**, only after every chunk completes. Recovered
retries are allowed. Normal queued/running or retry-pending work is not a
resolved success or a terminal failure and is not manually repaired.

**API ordering trap:** `Get-Slices` pages by `updatedAtUtc`, not slice time. A
recently repaired old window can be first. Fully page a bounded recent
slice-time band, widen it when needed, then sort by logical window. Do not infer
the newest outcomes from the first page. Health evidence is independent of any
historical failure-search range.

An unhealthy job, insufficient history, uncertain page coverage, disabled
execution, or concrete stalled/expired-lease evidence means **report and skip
that job**. Missing errors or an idle worker pool are not proof of recovery.
Continue independent eligible jobs; do not monitor an initially ineligible job
until it becomes healthy and then repair it automatically.

Record the witness slice identities and outcomes. Recheck health, policy,
lifecycle, catalog version and target bindings immediately before each batch. Do
not let a prior approval or a successful repair override a newly failed health
gate.

## 4. Build the bounded dependency plan

Read live canonical `dependsOn` GUID bindings and reverse the edges to find
transitive downstream jobs. `Get-Dependencies` returns upstream references and
**bounded blocked samples**, not a complete downstream graph or inventory. Use
canonical schedules plus paged slice reads for the full affected set. Report
unknown references or cycles instead of guessing an execution order.

Map actual required intervals, not equal timestamps: downstream windows may be
larger or smaller and use a different `startFrom` grid. For each edge, enumerate
the downstream windows whose required upstream windows intersect the repaired
set, then carry that set forward transitively. Use live readiness evidence and
the upstream's window size/start anchor; include upstream prerequisites outside
the failed parent's own interval when needed.

Check readiness for the proposed repair ranges. An unhealthy, excluded, missing
or out-of-scope prerequisite blocks that branch; it does not authorize an extra
repair. Include any necessary additional targets in a revised, authorized
manifest before proceeding.

The manifest records GUID/label, target cluster/database/function/table, aligned
UTC ranges, failed slice/chunk identities, preview counts, successful siblings,
health witnesses, policy exclusions and affected downstream windows. Any planned
terminal-gap repair in a downstream job needs its **own** policy and health
checks. DependencyBlocked work normally releases through the scheduler; do not
submit it as failed-work repair.

Preview all proposed writes and apply the authorization rule above. Snapshot
completed sibling/skip states, attempt counters and timestamps for preservation
checks. Keep catalog/readiness changes visible rather than silently using a
stale local job definition.

## 5. Repair upstream-first

These skills are siblings in the supported Kusto Slice Runner skill bundle.
Before using the recipes, resolve
`..\ksr-job-manager\scripts\Invoke-KsrJobApi.ps1` relative to this skill's base
directory and keep its resolved path in `$helper`. Do not resolve it from the
current working directory or an assumed checkout. The recipes assume `$jobId`
is a selected permanent GUID and the other variables come from the reviewed
scope:

```powershell
$jobs = & $helper -Action Get-Jobs
$job = & $helper -Action Get-Job -JobId $jobId
$failures = & $helper -Action Get-Failures -JobId $jobId `
    -Query @{ limit = 200 } -AllPages
$states = & $helper -Action Get-Slices -JobId $jobId `
    -Query @{ from = $healthFromUtc; to = $healthToUtc; limit = 200 } -AllPages
```

`Get-Jobs` returns an **array**; operational paged reads return
`items`/`nextCursor`. Normalize returned timestamps with their offsets to UTC.
`Get-Slices` accepts slice-start `from`/`to` ranges; `Get-Attempts` and
`Get-Events` use `start`/`end` to select an exact slice. `Get-Failures` has no
date-range filter. Do not invent common filters across these actions.

After current policy/health/readiness checks and authorization:

```powershell
$preview = & $helper -Action Preview-Repair -JobId $jobId `
    -From $manifestFromUtc -To $manifestToUtc
```

Compare the fresh preview's **exact slice/chunk identities**, not just counts,
with the authorized manifest. If none remain, report a no-op. Otherwise, only
when the preview is within that manifest:

```powershell
$receipt = & $helper -Action Repair -JobId $jobId `
    -From $manifestFromUtc -To $manifestToUtc -Reason $repairReason `
    -ExpectedSliceCount $preview.repairableSliceCount `
    -ExpectedExecutionCount $preview.repairableExecutionCount `
    -PreviewToken $preview.previewToken
$receipt | ConvertTo-Json -Depth 50 | Set-Content -LiteralPath $receiptPath
& $helper -Action Get-Repair -BatchId $receipt.repairBatchId
```

Repair preserves successful siblings and stable Kusto ingestion identities;
never clear output or reset successful history. Establish success with the first
small approved batch before adding more. Advance upstream-first, with downstream
terminal repairs only after their required upstreams complete.

On `repair-preview-conflict`, refresh state, health and preview, never force
counts or a token. Already-completed targets may be removed from the manifest;
new targets cannot be added silently. On an uncertain write response, inspect
`Get-Repairs`/`Get-Repair` and current state before doing anything else; do not
blindly submit the repair again. A failed repair is investigated and reported,
not repeatedly requeued until it works.

## 6. Follow through and verify

Keep session-local evidence of scope, policy decisions, health witnesses,
authorization, before-images, previews, durable batch IDs and outstanding
job/window conditions. On continuation, read these records and live batch state
instead of resubmitting completed work.

Use short bounded reads for initial recovery. Once successful initial slices
establish health, use **one available session follow-up at a 15-minute cadence**
if the authorized repair/downstream closure still needs time. Do not poll or
sleep between scheduled checks, invent unavailable scheduling tools, or alter
the Kusto Slice Runner schedule. If continuation is unavailable, report pending
batch IDs and conditions rather than claiming to monitor.

Stop adding work to a branch on a new terminal fault or loss of eligibility, and
remove that branch from the wait with a failure report. Independent healthy
branches may continue within the manifest. Clear the single follow-up when all
remaining work completes, all branches stop, the task is cancelled, or it is
abandoned; never leave a monitor waiting indefinitely on a terminal fault.

Completion requires completed repair batches and parent slices/chunks, unchanged
successful siblings/skipped completed slices, and completion of the affected
eligible downstream windows. Verify actual window identities, not just a lower
global failure count. Distinguish fresh ordinary dependency waits from the
historical gaps and identify downstream windows not yet due. Paused, unhealthy,
excluded or unrelated blocking prerequisites mean partial recovery, not success
or permission to repair them.

Report repaired logical slices and executions separately, downstream recovery,
preserved work, recent-policy exclusions, unhealthy/insufficient-evidence skips,
remaining blockers and monitor state. This verifies **Kusto Slice Runner
execution-state recovery**, not business-data correctness. Do not invent
nonempty-output requirements, dashboard checks or domain-specific Kusto audits.

## Optional troubleshooting reference

Ordinary recovery needs local API evidence, not external research. For an
unexplained Kusto error, search official Learn documentation for the exact error
and `Kusto ingest-by ingestIfNotExists` before making claims about retry or
ingestion behavior. This does not authorize a query or write.

If Learn MCP is unavailable, use `npx @microsoft/learn-cli <command>`:

| Learn tool                                                         | CLI equivalent                            |
| ------------------------------------------------------------------ | ----------------------------------------- |
| `microsoft_docs_search(query="...")`                               | `search "..."`                            |
| `microsoft_docs_fetch(url="...")`                                  | `fetch "..."`                             |
| `microsoft_code_sample_search(query="...", language="powershell")` | `code-search "..." --language powershell` |
