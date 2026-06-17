# KO Lite local-first architecture

KO Lite runs as a local ASP.NET Core Razor Pages app with hosted background services. Local SQLite is the durable source of truth for catalog, queue, slice state, logs, repair, and rerun history. Kusto is only contacted when a worker executes a schedule slice.

## Components

| Component | Project | Responsibility |
| --- | --- | --- |
| Web dashboard | `src\KoLite.LocalApp` | Razor Pages UI for catalog management, dashboard views, slice history, rerun planning, repair, health, and shutdown. |
| Scheduler service | `src\KoLite.LocalApp` + `src\KoLite.Local.Core` | Enumerates due slices for enabled jobs and enqueues local work bounded by per-job and worker-pool limits. |
| Worker pool | `src\KoLite.LocalApp` + `src\KoLite.Local.Core` | Claims queued work, extends leases, executes output writes, records progress, retries, and terminal state. |
| SQLite persistence | `src\KoLite.Local.Sqlite` | Owns migrations, catalog, queue, state, operational read models, rerun snapshots, failure summaries, and repair services. |
| Kusto execution | `src\KoLite.Local.Kusto` | Builds `.set-or-append` commands, configures auth, executes live Kusto writes, and classifies Kusto errors. |
| Update-check service | `src\KoLite.LocalApp` | Periodically compares the built git commit against the remote branch HEAD via the GitHub CLI and surfaces a top-bar badge (up to date, update available, ahead of published, diverged, or unavailable) plus `/status/health` fields; read-only and failure-tolerant. |
| Local management API | `src\KoLite.LocalApp` | Loopback-only JSON API (`/api/jobs*`) that lets a same-machine agent read jobs and create/update schedules through the validated catalog import path. Exposes no enable/disable, delete, Kusto, rerun, or repair surface. |
| Operational scripts | `scripts` | Publish, run, UI-only run, drain shutdown, service metadata, diagnostics, and crash-recovery inspection. |

## Data flow

1. A user creates or imports schedule JSON through the dashboard.
2. The catalog stores canonical schedule JSON and lifecycle metadata in SQLite.
3. The scheduler enumerates due slices from enabled jobs and inserts idempotent queue rows.
4. The worker pool claims claimable queue rows and evaluates dependencies/max-parallelism bounds.
5. The Kusto executor runs the configured function for the slice window and appends results to the schedule output table.
6. Slice state, queue state, attempts, events, and operational logs are updated in SQLite.
7. Dashboard read models query SQLite to show job status, history, failures, and worker/scheduler health.

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

The app uses local queue leases sized from the larger of worker visibility timeout and job `queryTimeout`, plus a fixed buffer. This prevents reclaiming work while a valid Kusto request is still running, but crash recovery can take longer for jobs with long query timeouts.

## Safety boundaries

- User-facing fake/offline execution is not registered in the local app.
- Live Kusto execution uses the configured `target`, `functionName`, and `outputTable`.
- Kusto append commands use idempotency tags so duplicate slice execution can be suppressed by Kusto.
- Pausing a job prevents new scheduling and queued retry claims; already-running slices are allowed to finish.
- Rerun planning suggests Kusto cleanup commands but leaves execution of cleanup to the operator.
- The local management API is loopback-only and limited to reads and the validated, additive/update-only schedule import path; it cannot enable/disable, delete, run Kusto, rerun, or repair.

## Auth modes

`AzureCli` is the default and uses the signed-in Azure CLI user. `ManagedIdentity` supports system-assigned identity or user-assigned identity through `KoLite:Kusto:ManagedIdentityClientId`.
