# KO Lite development

This guide covers repository layout and local validation commands for KO Lite contributors.

## Prerequisites

- .NET SDK `10.0.300` or a later feature band compatible with `global.json`.
- Node.js and npm for restoring the bundled browser assets (Chart.js, Cytoscape.js, and marked).
- Azure CLI sign-in for default Kusto auth, or managed identity configuration for service-style runs.
- Kusto permissions to execute the configured function and append to the configured output table when live scheduling is enabled.

## Project layout

| Path | Purpose |
| --- | --- |
| `KoLite.Local.sln` | Standalone local-first solution. |
| `src\KoLite.Local.Core` | Schedule parsing, scheduling models, mutation policy, dependency readiness, rerun/repair contracts. |
| `src\KoLite.Local.Sqlite` | Local SQLite persistence, schema, queue, catalog, state, observability, repair, and rerun services. |
| `src\KoLite.Local.Kusto` | Live Kusto request building, authentication, execution, and error classification. |
| `src\KoLite.LocalApp` | Razor Pages dashboard, local hosted scheduler/worker services, health/shutdown endpoints, and static assets. |
| `tests\KoLite.Local.*` | Unit, integration, web, and local end-to-end tests for the active solution. |
| `docs` | Architecture, schedule contract, operations, development, and release-readiness notes. |

## Restore, build, and test

From the repository root:

```powershell
npm ci
dotnet restore .\KoLite.Local.sln
dotnet format .\KoLite.Local.sln --verify-no-changes --no-restore --verbosity minimal
dotnet build .\KoLite.Local.sln --no-restore --nologo
dotnet test .\KoLite.Local.sln --no-build --nologo
dotnet list .\KoLite.Local.sln package --vulnerable
npm audit --omit=dev --audit-level=moderate
```

`npm ci` restores Chart.js, Cytoscape.js (plus cytoscape-dagre), and marked, and runs the `postinstall` copy step for `src\KoLite.LocalApp\wwwroot\lib\chartjs`, `src\KoLite.LocalApp\wwwroot\lib\cytoscape`, and `src\KoLite.LocalApp\wwwroot\lib\marked`. The generated static assets are intentionally tracked so the .NET app can build and test without npm during normal development, but `npm ci` remains the source-of-truth refresh path.

## Local development run

For UI review, run with scheduler dispatch disabled and an explicit local SQLite path:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-dev.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Use the [operations runbook](operations-runbook.md) for live scheduling, configuration, diagnostics, and rerun guidance.

The example above uses a `ko-lite-dev.db` sandbox; `ko-lite-review.db` is the runbook's review sandbox. These distinct names are intentional — `ko-lite.db` is only the default path used when no connection string is supplied. To find which database an instance is actually using, run `.\scripts\Get-KoLiteDatabase.ps1` (or read `databasePath` from `/status/health`).

### View the live database while the app is running

To browse the **live** default database (`%LOCALAPPDATA%\KoLite\ko-lite.db`) while your published app keeps running, start a second UI-only instance on a different port:

```powershell
.\scripts\Start-KoLiteUi.ps1            # live DB on port 5099, scheduler + retention disabled
```

A second instance on the same database is normally refused by the single-instance guard. `Start-KoLiteUi.ps1` bypasses it with `--KoLite:AllowMultipleInstances=true` while keeping the scheduler, worker, and retention disabled, so the viewer performs no background writes. The manual equivalent is:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Retention:Enabled=false --KoLite:AllowMultipleInstances=true --KoLite:Kusto:AuthMode=AzureCli --KoLite:Urls=http://127.0.0.1:5099
```

Startup still applies the current schema to the live database, so when your branch changes the schema use `-UseCopy` (or a sandbox `-DatabasePath`) instead. Mutating UI actions also write to the live database.

## Run from a deployed copy (avoid the build file lock)

`dotnet run` from the repository locks `src\KoLite.LocalApp\bin\...\KoLite.LocalApp.dll`, so a running app makes `dotnet build` / `dotnet test` fail with MSB3026/MSB3027 file-in-use errors. To iterate on changes while an app keeps running, deploy the build to an isolated folder and run it from there:

```powershell
.\scripts\Publish-KoLiteApp.ps1            # dotnet publish (Release) to %LOCALAPPDATA%\KoLite\run-app
.\scripts\Start-KoLiteApp.ps1              # run that deployed copy in the foreground (Ctrl+C to stop)
```

`Start-KoLiteApp.ps1` can be invoked from the repository as shown above or from inside the deployed folder after `Publish-KoLiteApp.ps1` copies it there. In both cases it starts the process with the deployed folder as the working directory so published static assets resolve correctly. Pass `-StopRunning` to gracefully drain an instance already running from the target folder before re-publishing. See the [operations runbook](operations-runbook.md#published-output) for all options.

## Create a GitHub Release

Run the **KO Lite Release** workflow from GitHub Actions with an unused
`vMAJOR.MINOR.PATCH` version. The hosted workflow runs the quality gates,
publishes and smoke-tests both Windows x64 packages, generates checksums and
complete release notes, and creates a draft.

After the draft exists, generate optional local highlights:

```powershell
pwsh -File .\scripts\New-KoLiteReleaseHighlights.ps1 -Version v1.1.0
```

The script reads the draft and the commits since the previous published release,
runs local Copilot with no tools, and writes its non-empty response to a
Markdown file under `%TEMP%`.
Review and paste it above `## Complete generated notes`, then review the draft
and publish manually. The script never edits GitHub. See the
[release guide](docs/releasing.md).
