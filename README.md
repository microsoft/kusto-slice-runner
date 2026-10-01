# Kusto Slice Runner (KSR)

**Desktop scheduling and backfills for Kusto.**

This repository is being prepared for its first public release. Public release
approval is pending; no public KSR binary release is available yet.

Kusto Slice Runner is a local, developer-desktop system for scheduled Kusto set-or-append jobs. It is designed for an authenticated user or service identity that already has permission to execute the configured Kusto functions and append to the configured output tables.

The job catalog, queue, slice history, operational logs, rerun reports, repair state, and performance observations are stored in SQLite. Normal execution-enabled instances also collect Kusto command statistics automatically in the background. Query output stays in Kusto; operational metadata is stored locally. UI-only instances do not execute jobs or collect statistics, and Performance page requests read SQLite only.

KSR is intended for non-production scenarios, such as personal dashboards,
prototyping, and historical backfills into sample datasets. It is not a hosted
service or a production orchestration platform. See [SUPPORT.md](SUPPORT.md)
for questions and feedback.

## Execution and recovery

- Scheduling and operational state run locally against SQLite; query execution and output stay in Kusto.
- You can rerun slices! Click on any slice in the colorful window history view and then click "Rerun this slice" to get into that experience. This will properly handle dependent jobs too, but you'll need to make sure the Kusto tables are ready to accept the new data. Kusto Slice Runner only reruns the jobs, it doesn't delete old data.
- You can both soft delete a job (keep the history to be resurrected in the future) or hard delete a job (permanently remove it and its history). Hard-delete avoids any problems around re-creating a job with the same id as a previous one.
- Pausing a job immediately blocks any future scheduling from happening. This includes retry loops! So when you pause a job, it will continue any in-flight set-or-append command but if that fails, it won't retry. After you unpause, it will pick up where it left off in the retry logic.
- Optional chunking splits each logical time window into 1-32 independently retried Kusto executions. Each chunk consumes one `maxParallelism` slot, downstream jobs wait for every upstream chunk, and each chunk has its own stable ingest-by identity. Per-job parallelism and the default all-up worker pool have no upper cap; operators may configure a finite global cap.
- A failed slice's **Repair or rerun** page defaults to repairing only terminal failed chunks (successful siblings and automatic retries are untouched, with no Kusto cleanup). Whole-slice rerun remains available when output must be recomputed after manual cleanup.
- Inside any job, open the Operations tab and click "Analyze failures" for a Copilot-written analysis of recent issues. It invokes GitHub Copilot CLI in non-interactive, no-tools mode, so no separate model endpoint or resource deployment is needed.
- You can visualize job dependencies and then also add Kusto functions and table schema depdendencies to the map.
- You can now add tags to your jobs and then filter them in the UI. This helps you handle multiple workstreams in a single instance.
- A combination of skills and an API make it easy to use GHCP to manage and maintain your jobs.

## What it does

- Executes Kusto functions on a schedule and writes results to Kusto tables.
- Schedules due time slices from enabled jobs into a local SQLite queue.
- Executes live Kusto `.set-or-append` commands for each claimed slice.
- Tracks queue state, slice history, attempts, logs, failures, and success-rate charts.
- Imports and exports strict schedule JSON for Kusto output jobs, including optional Markdown descriptions and job organization tags.
- Shows active, completed, and soft-deleted jobs in a local dashboard.
- Summarizes each job with a compact **color-only status pill** (hover to learn more): a left half for **recent health** — green (healthy), amber (warning), red (attention) — that answers "is it working now?", plus, for strict jobs, a right half that flags **historical completeness** (red when unaddressed dead-lettered gaps exist, green when whole). The per-job `healthPolicy` (`complete` default, or `recent`) chooses whether old gaps are surfaced; `recent` jobs show a single solid capsule. See [docs/operations-runbook.md](docs/operations-runbook.md#dashboard-status-model).
- Visualizes job dependencies as a graph (colored by current job status) from a job's details page or by multi-selecting jobs on the dashboard and choosing "Dependencies". On demand, the graph can also resolve each job's Kusto lineage — the downstream functions/materialized views that consume its output, the upstream tables/functions it reads (including cross-cluster sources), and **implicit** (undeclared) dependencies where a job reads another KSR job's output without declaring it.
- Shows an **Activity** page (`/activity`) with separate logical-slice and execution-unit counts. Chunked windows stay one table row while showing completed/total progress and every running chunk/worker; ETAs use recent whole-window durations, and processed totals/charts count each chunk or unchunked slice as one execution.
- Adds an **Activity -> Performance** comparison of job and chunk CPU, server command duration, and peak-memory P50/P90/P95, plus completed-attempt counts, success rates, and metric coverage. Job rows pool successful attempts and expand into raw chunk IDs; the default period is seven days. Statistics are collected automatically into SQLite, including a best-effort recent-history backfill.
- Bounds local database growth: a retention service prunes old operational telemetry (logs, terminal queue rows, old attempts) on a schedule while preserving the full slice window-history, so reruns and scheduling stay intact.
- Plans historical reruns and local state repair while leaving destructive Kusto cleanup to the operator.
- Exposes a versioned localhost-only JSON API under `/api/v1` with generated OpenAPI, first-class job create/update/pause/resume, ETag concurrency, safe soft-delete/restore and failed-work repair, plus cursor-paged operational diagnostics. Hard delete, whole-slice rerun, and Kusto cleanup remain browser/operator-only.
- Ships **Copilot skills** in `.github/skills`: `ksr-job-manager` drives that API (import/upsert, pause/resume, soft-delete/restore, diagnostics), [`ksr-gap-repair`](.github/skills/ksr-gap-repair/SKILL.md) fills terminal gaps after recent job health recovers and follows downstream completion (report-only for `healthPolicy: "recent"` unless explicitly overridden), `ksr-schedule-json` authors and validates schedule JSON locally, and `ksr-release-highlights` writes AI highlights for an existing release draft to a local Markdown file.
- Periodically checks GitHub (via the `gh` CLI) for a newer published Kusto Slice Runner release and shows an update badge in the top bar.

## Quick start from a release

After a public release is approved and published, open
[GitHub Releases](https://github.com/microsoft/kusto-slice-runner/releases)
and download one of these Windows x64 packages:

- `kusto-slice-runner-<version>-win-x64-self-contained.zip` includes the .NET runtime and is the easiest option.
- `kusto-slice-runner-<version>-win-x64-framework-dependent.zip` is smaller but requires the .NET 10 runtime.

Extract the ZIP to a stable folder. Sign in with Azure CLI for Kusto access, then make the first start with scheduling disabled:

```powershell
az login
.\Start-KsrApp.ps1 -AppArguments '--Ksr:Scheduler:Enabled=false'
```

Open `http://127.0.0.1:5057` and check `http://127.0.0.1:5057/healthz`. Detailed local status is at `http://127.0.0.1:5057/api/v1/system/status`. Review the configured jobs and Kusto targets before restarting without the scheduler override. The local catalog and execution history remain in `%LOCALAPPDATA%\Ksr\ksr.db`, outside the extracted application folder, so replacing the application folder does not replace your runtime state.

Before upgrading an existing database, review the [throttling-advisor retirement precautions](docs/operations-runbook.md#upgrading-after-throttling-advisor-retirement): startup removes obsolete observation storage while preserving ordinary execution history.

GitHub CLI is optional for basic execution but is required for the update badge; run `gh auth login` once to enable update checks. Copilot-powered failure analysis separately requires [GitHub Copilot CLI](https://docs.github.com/copilot/how-tos/use-copilot-agents/use-copilot-cli); install the `copilot` command and run `copilot login` once.

General Kusto CLI administration is intentionally not bundled as a Kusto Slice Runner
skill. Developers can separately install or register a `kusto-cli` skill in
their Copilot environment; Kusto Slice Runner repository instructions require explicit
cluster and database arguments when it is used.

### Optional startup at Windows sign-in

After reviewing your jobs, opt in from the published folder to resume Kusto Slice Runner
automatically when you sign in after a reboot:

```powershell
.\Register-KsrStartup.ps1                         # visible PowerShell console
.\Register-KsrStartup.ps1 -WindowMode Background  # alternatively, no visible window
.\Get-KsrStartup.ps1                              # inspect settings and last result
.\Unregister-KsrStartup.ps1                       # disable startup; leave the app running
```

Registration does not start or stop the app now. It reuses your Windows user,
Azure CLI sign-in, and application settings; pass any custom launch overrides
through `-AppArguments`. The first registration defaults to a visible console;
later updates preserve omitted settings. Minimize the console to leave Kusto Slice Runner
running; closing it can terminate active work. Azure CLI may still require
`az login` when credentials expire or MFA is needed.

Both package types include these helpers. Use `-DryRun` to preview, and see
[automatic startup](docs/operations-runbook.md#automatic-startup-at-windows-sign-in)
for logging, upgrades, and limitations.

## Quick start from source

From the repository root:

```powershell
npm ci

dotnet run --project .\src\Ksr.LocalApp\Ksr.LocalApp.csproj -- --Ksr:Scheduler:Enabled=false
```

Open `http://127.0.0.1:5057` and check `http://127.0.0.1:5057/healthz`.

Review every job's cluster, database, function, output table, and permissions
before restarting with execution enabled.

You should be able to kill it at any point and it will restart without duplicating data (thanks to ingest-by tags) but to avoid any chance of issues, execute `scripts\Stop-KsrApp.ps1`. It will wait for the workers to drain and then shut down gracefully.

## Screenshots

These screenshots use fictional retail jobs and generated local history. The resolved
lineage and failure analysis are illustrative fixture responses, not live Kusto or
Copilot results. See [Recreating the screenshots](docs/screenshots.md) for the isolated
capture workflow.

Job overview:

![Kusto Slice Runner job overview dashboard](docs/images/job-overview.png)

Job detail:

![Kusto Slice Runner job detail page](docs/images/job-detail.png)

Job dependencies with resolved Kusto lineage:

![Kusto Slice Runner dependency graph with Kusto lineage](docs/images/dependency-graph-lineage.png)

Activity:

![Kusto Slice Runner activity page](docs/images/activity.png)

Analyze failures with Copilot:

![Kusto Slice Runner Copilot failure analysis](docs/images/copilot-failure-analysis.png)

## Documentation

| Topic | Link |
| --- | --- |
| Architecture and component responsibilities | [Local-first architecture](docs/local-first-architecture.md) |
| Schedule JSON contract and import/export behavior | [Schedule JSON](docs/schedule-json.md) |
| Localhost API for agent-driven job management | [Local management API](docs/local-api.md) |
| Safe local runs, configuration, diagnostics, and reruns | [Operations runbook](docs/operations-runbook.md) |
| Repository layout, restore, build, and test commands | [Development guide](DEVELOPMENT.md) |
| Creating and reviewing GitHub Releases | [Release guide](docs/releasing.md) |
| Standalone repo validation checklist | [Release readiness checklist](docs/release-readiness-checklist.md) |

## Support and security

See [SUPPORT.md](SUPPORT.md), [SECURITY.md](SECURITY.md), [CONTRIBUTING.md](CONTRIBUTING.md), and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This project has adopted the
[Microsoft Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/).

**Do not report security vulnerabilities through public GitHub issues.** Report
them to the [Microsoft Security Response Center](https://msrc.microsoft.com/create-report);
see [SECURITY.md](SECURITY.md) for the maintained reporting policy.

## Development history

The development history was imported with original dates, public contributor
identities, and sensitive historical assets removed. Some internal-only commits
were omitted. Commit hashes differ from the original history.

## Data and external services

The catalog, logs, and execution history are stored locally in SQLite. Execution
contacts the configured Kusto targets and collects command statistics into the
local database. Disabling `Ksr:Scheduler:Enabled` disables execution and its
background statistics collection; explicitly requested lineage resolution is a
separate Kusto read.

Optional update checks contact GitHub through `gh`; disable them with
`Ksr:UpdateCheck:Enabled=false`. User-triggered failure analysis sends job
identifiers, target/function/table names, timestamps, and secret-sanitized error
evidence to GitHub Copilot CLI. Secret redaction is not anonymization. Review your
organization's data-sharing policy before using it, or disable the feature with
`Ksr:CopilotAnalysis:Enabled=false`.

## License and third-party code

KSR's first-party source is licensed under [MIT](LICENSE.TXT). The repository
includes third-party Chart.js, Cytoscape.js, cytoscape-dagre, and marked browser
assets, including bundled transitive components. See [NOTICE](NOTICE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for attribution and dependencies.
Third-party components retain their own licenses.

## Trademarks

This project may contain trademarks or logos for projects, products, or services.
Authorized use of Microsoft trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/en-us/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must
not cause confusion or imply Microsoft sponsorship. Any use of third-party
trademarks or logos are subject to those third-party's policies.
