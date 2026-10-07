# Kusto Slice Runner development

This guide covers repository layout and local validation commands for Kusto
Slice Runner contributors.

## Prerequisites

- .NET SDK `10.0.300` or a later feature band compatible with `global.json`.
- Node.js and npm for restoring the bundled browser assets (Chart.js,
  Cytoscape.js, and marked).
- Azure CLI sign-in for default Kusto auth, or managed identity configuration
  for service-style runs.
- Kusto permissions to execute the configured function and append to the
  configured output table when live scheduling is enabled.
- Optional: GitHub Copilot CLI plus `copilot login` for the dashboard's
  on-demand failure analysis.

## Project layout

| Path                   | Purpose                                                                                                                                                 |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Ksr.Local.sln`        | Standalone local-first solution.                                                                                                                        |
| `src\Ksr.Local.Core`   | Schedule parsing, scheduling models, mutation policy, dependency readiness, rerun/repair contracts.                                                     |
| `src\Ksr.Local.Sqlite` | Local SQLite persistence, schema, queue, catalog, state, observability, repair, and rerun services.                                                     |
| `src\Ksr.Local.Kusto`  | Live Kusto request building, authentication, execution, and error classification.                                                                       |
| `src\Ksr.LocalApp`     | Razor Pages dashboard, versioned Minimal API/application handlers, local hosted scheduler/worker services, health/control endpoints, and static assets. |
| `tests\Ksr.Local.*`    | Unit, integration, web, and local end-to-end tests for the active solution.                                                                             |
| `docs`                 | Architecture, schedule contract, operations, development, and release-readiness notes.                                                                  |

## Restore, build, and test

From the repository root:

```powershell
npm ci
npm run format:md:check
npm run test:js
dotnet restore .\Ksr.Local.sln
dotnet format .\Ksr.Local.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build .\Ksr.Local.sln --no-restore --nologo
dotnet test .\Ksr.Local.sln --no-build --nologo
dotnet list .\Ksr.Local.sln package --vulnerable
npm audit --omit=dev --audit-level=moderate
```

`npm ci` restores Chart.js, Cytoscape.js (plus cytoscape-dagre), marked, and the
jsdom test dependency, then runs the `postinstall` asset copy step.
`npm run test:js` verifies that `site.js` consumes Razor-rendered endpoint data
attributes for Kusto lineage and failure analysis.

### Markdown formatting

Run `npm run format:md` to format repository Markdown, including documentation
and `.github` instructions and skills. Prettier wraps prose at 80 columns; long
links, tables, and other Markdown constructs may exceed that width. Fenced code
block contents are left unchanged. Generated output and vendored browser assets
are excluded by `.prettierignore`.

Run `npm run format:md:check` to verify formatting without changing files. CI
runs this check after `npm ci`. Editors with a Prettier integration can use the
repository's `.prettierrc.json` configuration for Markdown formatting.

The `.gitattributes` rule keeps Markdown checkouts on LF line endings, including
on Windows, so Git's `core.autocrlf` setting does not conflict with Prettier.

### Aggregation scale diagnostics

The SQLite repository's 100,000-attempt scale test remains in the normal .NET
suite, with assertions for exact counts, pooled percentiles, chunk results, and
a single connection. Its elapsed aggregation time is informational, not a
pass/fail limit, because local and hosted runner load varies. The reported
duration covers `GetAggregates` only, excluding fixture setup and the rest of
the test process.

CI and release validation display the timing through xUnit live output. After
the build steps above, run the focused test with the same output settings:

```powershell
dotnet test .\tests\Ksr.Local.Sqlite.Tests\Ksr.Local.Sqlite.Tests.csproj --no-build --nologo --filter "FullyQualifiedName~One_hundred_thousand_attempts_have_exact_uncapped_aggregates_in_one_pipeline" --logger "console;verbosity=normal" -- xUnit.ShowLiveOutput=true
```

## Local development run

For UI review, run with scheduler dispatch disabled and an explicit local SQLite
path:

```powershell
$db = "$env:LOCALAPPDATA\Ksr\ksr-dev.db"
dotnet run --project .\src\Ksr.LocalApp\Ksr.LocalApp.csproj -- --ConnectionStrings:KsrSqlite="$db" --Ksr:Scheduler:Enabled=false --Ksr:Kusto:AuthMode=AzureCli
```

Use the [operations runbook](operations-runbook.md) for live scheduling,
configuration, diagnostics, and rerun guidance.

For public-shareable documentation images, use the
[synthetic screenshot workflow](docs/screenshots.md). It runs a developer-only
host with a fresh database and a separate loopback port; it never copies the
live database or contacts Kusto or Copilot.

The example above uses a `ksr-dev.db` sandbox; `ksr-review.db` is the runbook's
review sandbox. These distinct names are intentional — `ksr.db` is only the
default path used when no connection string is supplied. To find which database
an instance is actually using, run `.\scripts\Get-KsrDatabase.ps1` (or read
`database.path` from `/api/v1/system/status`).

### View the live database while the app is running

To browse the **live** default database (`%LOCALAPPDATA%\Ksr\ksr.db`) while your
published app keeps running, start a second UI-only instance on a different
port:

```powershell
.\scripts\Start-KsrUi.ps1            # live DB on port 5099, scheduler + retention disabled
```

A second instance on the same database is normally refused by the
single-instance guard. `Start-KsrUi.ps1` bypasses it with
`--Ksr:AllowMultipleInstances=true` while keeping the scheduler, worker,
performance collection/backfill, and retention disabled, so the viewer performs
no background writes. Performance displays already stored statistics without
contacting Kusto. The manual equivalent is:

```powershell
$db = "$env:LOCALAPPDATA\Ksr\ksr.db"
dotnet run --project .\src\Ksr.LocalApp\Ksr.LocalApp.csproj -- --ConnectionStrings:KsrSqlite="$db" --Ksr:Scheduler:Enabled=false --Ksr:Retention:Enabled=false --Ksr:AllowMultipleInstances=true --Ksr:Kusto:AuthMode=AzureCli --Ksr:Urls=http://127.0.0.1:5099
```

Startup still applies the current schema to the live database, so when your
branch changes the schema use `-UseCopy` (or a sandbox `-DatabasePath`) instead.
Mutating UI actions also write to the live database.

Performance collection is mandatory in a normal execution-enabled host. Tests
that enable the scheduler must replace `IKustoCommandStatisticsReader` (or the
collection-pass seam) as well as any fake output executor; replacing
`ILocalSliceOutputExecutor` alone does not intercept telemetry reads. There is
no separate performance enable/off configuration. UI-only tests inherit the
existing `Ksr:Scheduler:Enabled=false` boundary.

## Run from a deployed copy (avoid the build file lock)

`dotnet run` from the repository locks
`src\Ksr.LocalApp\bin\...\Ksr.LocalApp.dll`, so a running app makes
`dotnet build` / `dotnet test` fail with MSB3026/MSB3027 file-in-use errors. To
iterate on changes while an app keeps running, deploy the build to an isolated
folder and run it from there:

```powershell
.\scripts\Publish-KsrApp.ps1            # dotnet publish (Release) to %LOCALAPPDATA%\Ksr\run-app
.\scripts\Start-KsrApp.ps1              # run that deployed copy in the foreground (Ctrl+C to stop)
```

`Start-KsrApp.ps1` can be invoked from the repository as shown above or from
inside the deployed folder after `Publish-KsrApp.ps1` copies it there. In both
cases it starts the process with the deployed folder as the working directory so
published static assets resolve correctly. Pass `-StopRunning` to gracefully
drain an instance already running from the target folder before re-publishing.
See the [operations runbook](operations-runbook.md#published-output) for all
options.

Publishing also includes the optional Windows sign-in startup helpers and their
shared `Ksr.Startup.psm1` module. Publishing does not register a task. From the
deployed folder, `.\Register-KsrStartup.ps1` opts into a visible console on
sign-in; `-WindowMode Background` selects no visible window.
`Get-KsrStartup.ps1` inspects the registration and `Unregister-KsrStartup.ps1`
removes it without stopping the app. See the
[startup runbook](docs/operations-runbook.md#automatic-startup-at-windows-sign-in).

Startup script tests use isolated application/log directories, fake executable
probes, and mocked task registration; in-memory task-definition checks never
register a real task. Run the focused coverage with:

```powershell
dotnet test .\tests\Ksr.LocalApp.Tests\Ksr.LocalApp.Tests.csproj --no-restore --filter "FullyQualifiedName~StartupScriptTests|FullyQualifiedName~OperationalScriptTests" --nologo
```

Do not test startup by rebooting/signing out, registering against the live app,
or publishing over its files. Any manual startup smoke test must use a sandbox
database, a separate port, and `Ksr:Scheduler:Enabled=false` (which also
disables performance collection/backfill). Actual console visibility is a
Windows visual acceptance check, not just a task-definition assertion.

## Create a GitHub Release

Run the **Kusto Slice Runner Release** workflow from GitHub Actions with an
unused `vMAJOR.MINOR.PATCH` version. The hosted workflow runs the quality gates,
publishes and smoke-tests both Windows x64 packages, generates checksums and
complete release notes, and creates a draft.

After the draft exists, invoke the `ksr-release-highlights` skill. It
auto-selects a single workflow-owned draft, asks when multiple eligible drafts
exist, previews the exact generated bullets, and edits only `## Changes` after
explicit approval. Replacing existing non-placeholder Changes content requires a
separate overwrite confirmation. The transient preview data is removed after the
interaction, and publishing remains manual.

For a read-only script preflight, run:

```powershell
pwsh -File .\scripts\New-KsrReleaseHighlights.ps1 -DryRun
```

See the [release guide](docs/releasing.md).
