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
