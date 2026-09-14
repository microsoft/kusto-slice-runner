# KO Lite local-first architecture

KO Lite runs as a local ASP.NET Core Razor Pages app with hosted background services. Local SQLite is the durable source of truth for catalog, queue, slice state, logs, repair, rerun history, and performance observations. Kusto is contacted for job execution, automatic command-statistics collection in execution-enabled instances, and explicitly requested lineage resolution. Performance reporting itself reads SQLite only.

## Components

| Component | Project | Responsibility |
| --- | --- | --- |
| Web dashboard | `src\KoLite.LocalApp` | Razor Pages UI under canonical `/jobs` routes for catalog management, dashboard views, slice history, the job dependency graph, rerun planning, repair, health, and shutdown. |
| HTTP/application boundary | `src\KoLite.LocalApp` | Versioned Minimal API modules under `/api/v1`, named contracts, generated OpenAPI, Problem Details, ETag/cursor helpers, local-request policy, and shared application handlers used by both agent and browser transports. |
| Scheduler service | `src\KoLite.LocalApp` + `src\KoLite.Local.Core` | Each pass tops up every enabled job's queue to its `maxParallelism` in execution units (chunks for chunked jobs, slices otherwise; dependency-ready work only). There is no global per-tick enqueue throttle; a pass is naturally bounded by the sum of per-job `maxParallelism`. |
| Worker pool | `src\KoLite.LocalApp` + `src\KoLite.Local.Core` | Claims queued execution units, extends leases, executes output writes, records progress, retries, and terminal state. Global concurrency is unbounded by default and can be limited with `KoLite:WorkerPool:MaxConcurrency`; the separate 100-start default is per dispatch cycle, not a concurrency ceiling. |
| SQLite persistence | `src\KoLite.Local.Sqlite` | Owns the SQLite schema, catalog, queue, state, operational read models, rerun snapshots, failure summaries, and repair services. |
| Kusto execution | `src\KoLite.Local.Kusto` | Builds `.set-or-append` commands, configures auth, executes live Kusto writes, and classifies Kusto errors. |
| Performance collection and reporting | `src\KoLite.LocalApp` + `src\KoLite.Local.Sqlite` + `src\KoLite.Local.Kusto` + `src\KoLite.Local.Core` | Mandatory bounded background reads of Kusto command history enrich per-attempt SQLite facts. Activity's Performance subview compares job/chunk resource percentiles and attempt reliability without contacting Kusto. Collection/backfill inherits the global execution-mode gate, not a feature toggle. |
| Update-check service | `src\KoLite.LocalApp` | Periodically compares the built git commit against the remote branch HEAD via the GitHub CLI and surfaces a top-bar badge plus the `update` block in `/api/v1/system/status`; read-only and failure-tolerant. |
| Failure analysis | `src\KoLite.LocalApp` | On-demand "Analyze failures with Copilot": sends secret-sanitized failure evidence to GitHub Copilot CLI in non-interactive mode and renders the returned Markdown. The child process has no tools, MCP servers, custom instructions, remote control, or file access available. Read-only, gated by `KoLite:CopilotAnalysis:Enabled`, and contacts no Kusto. |
| Retention service | `src\KoLite.LocalApp` + `src\KoLite.Local.Sqlite` | Periodic background service that prunes non-authoritative operational telemetry older than the configured window while preserving authoritative history; surfaces its last outcome under `retention` in `/api/v1/system/status`. |
| Local agent API | `src\KoLite.LocalApp` | Loopback-only `/api/v1` JSON API with first-class GUID-keyed job create/replace/pause/resume, ETag concurrency, batch import/export, safe soft-delete/restore, failed-work repair, cursor-paged operations, and read-only Kusto lineage. Exposes no hard delete, whole-slice rerun, cleanup execution, or arbitrary Kusto write. |
| Operational scripts | `scripts` | Publish, run, UI-only run, drain shutdown, in-use database reporting, manual database VACUUM (`Invoke-KoLiteVacuum.ps1`), service metadata, diagnostics, and crash-recovery inspection. |

## Data flow

1. A user creates or imports schedule JSON through the dashboard.
2. The catalog stores canonical schedule JSON and lifecycle metadata in SQLite.
3. The scheduler enumerates due logical slices. For a chunked job it materializes 0-based child executions for the oldest eligible window and inserts queue rows up to the job's remaining `maxParallelism`.
4. The worker pool claims execution-unit queue rows, enforcing each job's `maxParallelism` across normal slices, chunks, retries, repairs, and recovery. Each chunk consumes one slot. Per-job values have no upper limit; global worker concurrency is unbounded by default, so total in-flight work can reach the sum of each job's `maxParallelism`.
5. The Kusto executor runs the configured function for the slice window and appends results to the schedule output table.
6. Slice state, queue state, attempts, events, and operational logs are updated in SQLite.
7. Dashboard read models query SQLite to show job status, history, failures, and worker/scheduler health.
8. The job dependency graph is a read-only UI projection (`DependencyGraphQuery` in `src\KoLite.LocalApp`): it reuses the dashboard's per-job status projection and the stored `dependsOn` GUID edges, and the pure connected-component computation lives in `src\KoLite.Local.Core` (`Graph\DependencyGraphLayout.cs`). The browser renders the nodes/edges with the bundled Cytoscape.js (dagre layout) for pan/zoom/fit; the server emits no pixel geometry. It adds no persistence.
9. On demand only (the graph's "Resolve Kusto lineage" button), the graph can be enriched with both directions of each job's data lineage: the downstream Kusto functions/materialized views that consume its output table, the upstream tables/functions its function reads (including cross-cluster sources, named directly in the dependencies), and **implicit** job dependencies (a function reads another KO job's output that is not in its `dependsOn`, drawn distinctly). This runs one read-only `.show databases entities` per cluster (`IKustoEntityDependencyReader` in `src\KoLite.Local.Kusto`; pure lineage in `src\KoLite.Local.Core` `Graph\KustoLineage.cs`), is the only graph path that contacts Kusto, performs no writes, and persists nothing. Implicit-dependency flagging is informational and does not change scheduler readiness.
10. Before output execution, the worker records a per-attempt client request identity and the actual target. The background performance collector matches those identities to bounded `.show commands-and-queries` results and stores CPU, server duration, and peak-memory observations. It also reconciles recent retained attempts and rerun snapshots; ambiguous legacy matches stay unavailable. The collector does not participate in worker claims, retries, concurrency, or output success.

## Job identity model

Each job has an opaque, permanent **GUID `id`** (`job_definitions.job_id`, the primary key and
the foreign key used by every slice/queue/state/event/repair/rerun row, and the first segment of
every slice key and idempotency key). `activityId` is a separate, mutable, unique **display
label**; renaming it does not touch the durable `id`, slice history, dependency edges, or Kusto
output idempotency. Dependencies are stored by upstream `id`. Schedule JSON carries the `id`
(server-assigned); imports match by `id` first (which is how a rename is applied) then by
`activityId`. URLs are canonical on the `id` with an `activityId`→`id` redirect for usability.

## Local durability model

SQLite files are local runtime state and are not source artifacts. The main database plus sidecars such as `*.db-wal` and `*.db-shm` should be backed up before destructive operations and ignored by source control.

The database is bounded over time by the retention service, which prunes only non-authoritative operational telemetry (logs, terminal queue rows, old attempts, and scheduled-slice records) older than a configurable window while preserving the authoritative window-history (`current_slice_state`, `slice_state_events`) and all catalog/lifecycle/audit/rerun/repair rows. SQLite reuses freed pages rather than returning them to the OS, so the file size plateaus instead of shrinking; reclaiming disk space is an explicit, app-stopped `VACUUM` via `scripts\Invoke-KoLiteVacuum.ps1`. See [operations runbook](operations-runbook.md#database-growth-and-retention).

The bounded `performance_attempts` read model references the job rather than the mutable current
slice/attempt rows. Whole-slice reruns therefore retain earlier attempts in performance statistics;
confirmed job hard deletion removes them. Performance facts use the protected chart-retention
window, never shorter than 30 days. Resource availability and collector checkpoints are separate
from authoritative execution state.

The Performance warning is a scoped read-model policy, not the collector's last-error state.
The same aggregate snapshot counts successful, non-suppressed attempts in the selected period,
excluding its newest five minutes, and counts each attempt missing any valid resource family once.
A warning requires both at least 20% missing and five missing attempts across visible job totals.
Name filtering recomputes those totals locally. Global errors remain in Collection details; neither
warning visibility nor filtering changes collection, retries, storage, or the table's statistics.

The app uses local queue leases sized from the larger of worker visibility timeout and job `queryTimeout`, plus a fixed buffer. This prevents reclaiming work while a valid Kusto request is still running, but crash recovery can take longer for jobs with long query timeouts.

Each execution attempt has a client-side deadline (job `queryTimeout` plus a small buffer) kept below the lease duration, so a hung call is cancelled and released rather than holding the lease open. An attempt that faults or times out abandons and retries its queue item immediately, and expired (orphaned) leases are reclaimed on the next dispatch cycle once past a short grace margin — not only when the worker pool is idle. Re-execution is idempotent (`ingest-by`), so recovery never duplicates output. Orphaned leases on paused or soft-deleted jobs are not auto-recovered (consistent with pause semantics); they surface as **Stalled** in the window history and can be recovered from the slice detail page once the job is enabled. See [operations runbook](operations-runbook.md#orphaned-leases-and-recovery).

## Safety boundaries

- User-facing fake/offline execution is not registered in the local app.
- Live Kusto execution uses the configured `target`, `functionName`, and `outputTable`.
- Kusto append commands use idempotency tags so duplicate slice execution can be suppressed by Kusto.
- Unchunked ingest-by identities are unchanged. A chunk's identity includes parent slice, chunk id, and total chunks and remains stable across retry, repair, recovery, and restart.
- A per-attempt client request ID is diagnostic correlation, not ingestion identity. Changing it between attempts must never change ingest-by tags or the append command's idempotency.
- Performance collection is automatic with no independent off switch, but `KoLite:Scheduler:Enabled=false` suppresses both collection and backfill before any remote reader is resolved. Paused jobs or idle workers in a normal instance do not suppress historical collection. Collector failures are logged and retried without changing worker state.
- Chunk failure evidence is retained per execution in child state/events, attempts, logs, queue rows, and repair history. Repair excludes chunks with automatic retry work, records each repaired chunk/queue mapping atomically, and updates logical repair summaries from child outcomes.
- Pausing a job prevents new scheduling and queued retry claims; already-running slices are allowed to finish.
- Rerun planning suggests Kusto cleanup commands but leaves execution of cleanup to the operator.
- Single and bulk hard delete remain browser-only operations behind explicit typed confirmation. Dashboard multi-select never offers hard delete; bulk selection exists only on the dedicated Manage soft-deleted jobs page. The server accepts soft-deleted jobs only, revalidates the entire selection under one write transaction, and rolls back without deleting anything if any job is no longer eligible.
- The local agent API is loopback-only and uses one injectable local-request policy for agent, OpenAPI, detailed status, UI-support, and control routes. It exposes first-class pause/resume and failed-work repair, but no hard delete, whole-slice rerun, cleanup execution, or arbitrary Kusto write.

## Auth modes

`AzureCli` is the default and uses the signed-in Azure CLI user. `ManagedIdentity` supports system-assigned identity or user-assigned identity through `KoLite:Kusto:ManagedIdentityClientId`.
