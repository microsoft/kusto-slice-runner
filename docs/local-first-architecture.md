# KO Lite local-first architecture

KO Lite runs as a local ASP.NET Core Razor Pages app with hosted background services. Local SQLite is the durable source of truth for catalog, queue, slice state, logs, repair, and rerun history. Kusto is only contacted when a worker executes a schedule slice.

## Components

| Component | Project | Responsibility |
| --- | --- | --- |
| Web dashboard | `src\KoLite.LocalApp` | Razor Pages UI for catalog management, dashboard views, slice history, the job dependency graph, rerun planning, repair, health, and shutdown. |
| Scheduler service | `src\KoLite.LocalApp` + `src\KoLite.Local.Core` | Each pass tops up every enabled job's queue to its `maxParallelism` (dependency-ready slices only). There is no global per-tick enqueue throttle; a pass is naturally bounded by the sum of per-job `maxParallelism`. |
| Worker pool | `src\KoLite.LocalApp` + `src\KoLite.Local.Core` | Claims queued work, extends leases, executes output writes, records progress, retries, and terminal state. |
| SQLite persistence | `src\KoLite.Local.Sqlite` | Owns the SQLite schema, catalog, queue, state, operational read models, rerun snapshots, failure summaries, and repair services. |
| Kusto execution | `src\KoLite.Local.Kusto` | Builds `.set-or-append` commands, configures auth, executes live Kusto writes, and classifies Kusto errors. |
| Ingestion throttling advisor | `src\KoLite.LocalApp` + `src\KoLite.Local.Sqlite` + `src\KoLite.Local.Core` | Records Kusto ingestion-capacity throttles (`ingestion_throttle_observations`, with a `terminal` dead-letter flag) and surfaces a severity view (throttled-attempt rate + chart) plus read-only `maxParallelism` reduction recommendations. A cluster shows when the throttled-attempt rate crosses a threshold with enough volume (and clears after a clean period), or whenever a slice has recently dead-lettered on throttling. Recommendations are guarded by a per-job keep-up floor, and a backfilling job is only trimmed to the catch-up floor that still clears its backlog within the target. Operators apply reductions explicitly. |
| Update-check service | `src\KoLite.LocalApp` | Periodically compares the built git commit against the remote branch HEAD via the GitHub CLI and surfaces a top-bar badge (up to date, update available, ahead of published, diverged, or unavailable) plus `/status/health` fields; read-only and failure-tolerant. |
| Failure analysis | `src\KoLite.LocalApp` | On-demand "Analyze failures with Copilot": sends secret-sanitized failure evidence to an OpenAI-compatible endpoint (GitHub Models by default) through `Microsoft.Extensions.AI`, authenticating with the GitHub CLI sign-in, and renders the returned Markdown. Read-only, gated by `KoLite:CopilotAnalysis:Enabled`, and contacts no Kusto. |
| Retention service | `src\KoLite.LocalApp` + `src\KoLite.Local.Sqlite` | Periodic background service that prunes non-authoritative operational telemetry (`operational_logs`, terminal `work_queue` rows, old `slice_attempts`, `scheduled_slices`, `ingestion_throttle_observations`) older than a configurable window (default 30 days, enabled by default) so the database stops growing without bound. Preserves the authoritative window-history (`current_slice_state`, `slice_state_events`) and all catalog/lifecycle/audit/rerun/repair rows; surfaces its last outcome under `retention` in `/status/health`. |
| Local management API | `src\KoLite.LocalApp` | Loopback-only JSON API (`/api/jobs*`) that lets a same-machine agent read jobs, create/update schedules through the validated catalog import path, and soft-delete/restore a job (reversible; each requires the job's current catalogVersion). Exposes no hard-delete, generic enable/disable, rerun, or repair surface; the only Kusto contact is the on-demand, read-only dependency-graph consumer endpoint (`POST /api/dependency-graph/kusto-consumers`). |
| Operational scripts | `scripts` | Publish, run, UI-only run, drain shutdown, in-use database reporting, manual database VACUUM (`Invoke-KoLiteVacuum.ps1`), service metadata, diagnostics, and crash-recovery inspection. |

## Data flow

1. A user creates or imports schedule JSON through the dashboard.
2. The catalog stores canonical schedule JSON and lifecycle metadata in SQLite.
3. The scheduler enumerates due slices from enabled jobs and inserts idempotent queue rows.
4. The worker pool claims claimable queue rows, enforcing each job's `maxParallelism` at claim time. Global worker concurrency is unbounded by default, so total in-flight work equals the sum of each job's `maxParallelism`.
5. The Kusto executor runs the configured function for the slice window and appends results to the schedule output table.
6. Slice state, queue state, attempts, events, and operational logs are updated in SQLite.
7. Dashboard read models query SQLite to show job status, history, failures, and worker/scheduler health.
8. The job dependency graph is a read-only UI projection (`DependencyGraphQuery` in `src\KoLite.LocalApp`): it reuses the dashboard's per-job status projection and the stored `dependsOn` GUID edges, and the pure connected-component computation lives in `src\KoLite.Local.Core` (`Graph\DependencyGraphLayout.cs`). The browser renders the nodes/edges with the bundled Cytoscape.js (dagre layout) for pan/zoom/fit; the server emits no pixel geometry. It adds no persistence.
9. On demand only (the graph's "Resolve Kusto lineage" button), the graph can be enriched with both directions of each job's data lineage: the downstream Kusto functions/materialized views that consume its output table, the upstream tables/functions its function reads (including cross-cluster sources, named directly in the dependencies), and **implicit** job dependencies (a function reads another KO job's output that is not in its `dependsOn`, drawn distinctly). This runs one read-only `.show databases entities` per cluster (`IKustoEntityDependencyReader` in `src\KoLite.Local.Kusto`; pure lineage in `src\KoLite.Local.Core` `Graph\KustoLineage.cs`), is the only graph path that contacts Kusto, performs no writes, and persists nothing. Implicit-dependency flagging is informational and does not change scheduler readiness.

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

The database is bounded over time by the retention service, which prunes only non-authoritative operational telemetry (logs, terminal queue rows, old attempts, scheduled-slice and throttle records) older than a configurable window while preserving the authoritative window-history (`current_slice_state`, `slice_state_events`) and all catalog/lifecycle/audit/rerun/repair rows. SQLite reuses freed pages rather than returning them to the OS, so the file size plateaus instead of shrinking; reclaiming disk space is an explicit, app-stopped `VACUUM` via `scripts\Invoke-KoLiteVacuum.ps1`. See [operations runbook](operations-runbook.md#database-growth-and-retention).

The app uses local queue leases sized from the larger of worker visibility timeout and job `queryTimeout`, plus a fixed buffer. This prevents reclaiming work while a valid Kusto request is still running, but crash recovery can take longer for jobs with long query timeouts.

Each execution attempt has a client-side deadline (job `queryTimeout` plus a small buffer) kept below the lease duration, so a hung call is cancelled and released rather than holding the lease open. An attempt that faults or times out abandons and retries its queue item immediately, and expired (orphaned) leases are reclaimed on the next dispatch cycle once past a short grace margin — not only when the worker pool is idle. Re-execution is idempotent (`ingest-by`), so recovery never duplicates output. Orphaned leases on paused or soft-deleted jobs are not auto-recovered (consistent with pause semantics); they surface as **Stalled** in the window history and can be recovered from the slice detail page once the job is enabled. See [operations runbook](operations-runbook.md#orphaned-leases-and-recovery).

## Safety boundaries

- User-facing fake/offline execution is not registered in the local app.
- Live Kusto execution uses the configured `target`, `functionName`, and `outputTable`.
- Kusto append commands use idempotency tags so duplicate slice execution can be suppressed by Kusto.
- Pausing a job prevents new scheduling and queued retry claims; already-running slices are allowed to finish.
- Rerun planning suggests Kusto cleanup commands but leaves execution of cleanup to the operator.
- The ingestion throttling advisor only recommends `maxParallelism` reductions; applying one is an explicit, audited operator action scoped to the throttled cluster, and a server-side keep-up floor prevents reducing a job below the parallelism it needs to keep up with real time. A job that is behind real time (a real backlog) is treated as a backfill and only trimmed to the catch-up floor that still clears its backlog within the configured target, with the catch-up ETA trade-off shown.
- The local management API is loopback-only and limited to reads, the validated additive/update-only schedule import path, and reversible soft-delete/restore; it exposes no hard-delete, generic enable/disable, Kusto execution, rerun, or repair.

## Auth modes

`AzureCli` is the default and uses the signed-in Azure CLI user. `ManagedIdentity` supports system-assigned identity or user-assigned identity through `KoLite:Kusto:ManagedIdentityClientId`.
