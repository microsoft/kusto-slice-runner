# KO Lite development

This guide covers repository layout and local validation commands for KO Lite contributors.

## Prerequisites

- .NET SDK `10.0.300` or a later feature band compatible with `global.json`.
- Node.js and npm for restoring Chart.js assets.
- Azure CLI sign-in for default Kusto auth, or managed identity configuration for service-style runs.
- Kusto permissions to execute the configured function and append to the configured output table when live scheduling is enabled.

## Project layout

| Path | Purpose |
| --- | --- |
| `KoLite.Local.sln` | Standalone local-first solution. |
| `src\KoLite.Local.Core` | Schedule parsing, scheduling models, mutation policy, dependency readiness, rerun/repair contracts. |
| `src\KoLite.Local.Sqlite` | Local SQLite persistence, migrations, queue, catalog, state, observability, repair, and rerun services. |
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

`npm ci` restores Chart.js and runs the `postinstall` copy step for `src\KoLite.LocalApp\wwwroot\lib\chartjs`. The generated static assets are intentionally tracked so the .NET app can build and test without npm during normal development, but `npm ci` remains the source-of-truth refresh path.

## Local development run

For UI review, run with scheduler dispatch disabled and an explicit local SQLite path:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-dev.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Use the [operations runbook](operations-runbook.md) for live scheduling, configuration, diagnostics, and rerun guidance.

The example above uses a `ko-lite-dev.db` sandbox; `ko-lite-review.db` is the runbook's review sandbox. These distinct names are intentional — `ko-lite.db` is only the default path used when no connection string is supplied. To find which database an instance is actually using, run `.\scripts\Get-KoLiteDatabase.ps1` (or read `databasePath` from `/status/health`).

## Run from a deployed copy (avoid the build file lock)

`dotnet run` from the repository locks `src\KoLite.LocalApp\bin\...\KoLite.LocalApp.dll`, so a running app makes `dotnet build` / `dotnet test` fail with MSB3026/MSB3027 file-in-use errors. To iterate on changes while an app keeps running, deploy the build to an isolated folder and run it from there:

```powershell
.\scripts\Publish-KoLiteApp.ps1            # dotnet publish (Release) to %LOCALAPPDATA%\KoLite\run-app
cd "$env:LOCALAPPDATA\KoLite\run-app"
.\Start-KoLiteApp.ps1                      # run the deployed copy in the foreground (Ctrl+C to stop)
```

`Publish-KoLiteApp.ps1` copies `Start-KoLiteApp.ps1` and `Stop-KoLiteApp.ps1` into the deployed folder and prints the full deployed path. Pass `-StopRunning` to gracefully drain an instance already running from the target folder before re-publishing. See the [operations runbook](operations-runbook.md#published-output) for all options.
