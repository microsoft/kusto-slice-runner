# KO Lite operations runbook

This runbook covers local and service-style operation for the internal standalone KO Lite repo.

## Safe local review

Run with scheduler dispatch disabled when inspecting the UI or reviewing imported jobs:

```powershell
$db = "$env:LOCALAPPDATA\KoLite\ko-lite-review.db"
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=false --KoLite:Kusto:AuthMode=AzureCli
```

Open `http://127.0.0.1:5057/status/health` and confirm the database path, scheduler settings, Kusto auth mode, shutdown state, and worker-pool snapshot.

## Live local execution

Before enabling scheduler dispatch:

1. Confirm the Azure CLI user or managed identity has the intended Kusto permissions.
2. Confirm every enabled job points to the intended cluster, database, function, and output table.
3. Confirm `queryWindowSize`, `delayFromUtcNow`, `startFrom`, and `maxParallelism` are safe for the target workload.
4. Keep unreviewed jobs paused.

Then run with scheduler dispatch enabled:

```powershell
dotnet run --project .\src\KoLite.LocalApp\KoLite.LocalApp.csproj -- --ConnectionStrings:KoLiteSqlite="$db" --KoLite:Scheduler:Enabled=true --KoLite:Kusto:AuthMode=AzureCli
```

## Published app workflow

Publish to an isolated local folder:

```powershell
.\scripts\Publish-KoLiteLocalApp.ps1 -Configuration Release
```

Run UI-only:

```powershell
.\scripts\Start-KoLitePublishedUi.ps1
```

Run with scheduler setting chosen explicitly:

```powershell
.\scripts\Start-KoLitePublishedApp.ps1 -SchedulerEnabled $false
.\scripts\Start-KoLitePublishedApp.ps1 -SchedulerEnabled $true
```

Stop with graceful drain:

```powershell
.\scripts\Stop-KoLitePublishedApp.ps1
```

## Service metadata

The service install script is dry-run by default and does not start or stop services:

```powershell
.\scripts\Install-KoLiteLocalService.ps1 -DryRun
```

The default `AppDllPath` is `%LOCALAPPDATA%\KoLite\run-app\KoLite.LocalApp.dll`, matching `Publish-KoLiteLocalApp.ps1`. Run publish first, or pass `-AppDllPath` explicitly if the service should use a different installation folder.

Use `-Apply` only after reviewing the printed `sc.exe` commands.

## Diagnostics

Use:

```powershell
.\scripts\Test-KoLiteLocalDiagnostics.ps1 -DryRun
```

Enable per-pass scheduler/worker diagnostics temporarily with:

```powershell
--KoLite:Scheduler:LogEveryPass=true
```

Inspect recent scheduler pass gaps with:

```sql
WITH scheduler_passes AS (
    SELECT
        recorded_at_utc,
        properties_json,
        LAG(recorded_at_utc) OVER (ORDER BY recorded_at_utc) AS previous_recorded_at_utc
    FROM operational_logs
    WHERE category = 'scheduler-pass'
)
SELECT
    recorded_at_utc,
    ROUND((julianday(recorded_at_utc) - julianday(previous_recorded_at_utc)) * 86400.0, 3) AS seconds_since_previous,
    json_extract(properties_json, '$.configuredTickInterval') AS configured_tick_interval,
    json_extract(properties_json, '$.durationMs') AS duration_ms,
    json_extract(properties_json, '$.enqueued') AS enqueued,
    json_extract(properties_json, '$.dependencyBlocked') AS dependency_blocked,
    json_extract(properties_json, '$.skippedMaxParallelism') AS skipped_max_parallelism
FROM scheduler_passes
ORDER BY recorded_at_utc DESC
LIMIT 30;
```

## Rerun and cleanup

Rerun is blocked while affected slices are queued, leased, or running. The rerun planner suggests `.delete table ... records <|` commands, but the operator must review and execute Kusto cleanup manually before acknowledging the local rerun batch.

Back up the SQLite database before service upgrades, hard deletes, repair experiments, or large reruns.

## Crash recovery

Use `scripts\Inspect-KoLiteCrashRecovery.ps1` to inspect local crash-recovery state. Long `queryTimeout` values also lengthen queue lease windows, so recovery after a hard crash can take longer for long-running jobs.
