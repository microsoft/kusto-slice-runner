# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Runs the Kusto Slice Runner UI from repository source against an existing SQLite database with the
background scheduler and worker disabled, for validating code/UI changes before deploying.

.DESCRIPTION
Starts 'dotnet run' on src\KoLite.LocalApp\KoLite.LocalApp.csproj in the foreground (Ctrl+C to
stop) so you can browse the current source build of the dashboard against real data, without
running any jobs.

It always passes these fixed overrides (a later value in -AppArguments still wins):

1. --ConnectionStrings:KoLiteSqlite=<database>  points the instance at the chosen database. By
   default this is the live default database %LOCALAPPDATA%\KoLite\ko-lite.db (resolved the same
   way the app resolves it when no connection string is configured).
2. --KoLite:Scheduler:Enabled=false  disables BOTH the scheduler enqueue loop and the worker
   dispatcher, plus performance collection and backfill, so no slices are claimed and no automatic
   Kusto access / execution writes happen. The instance is effectively UI-only.
3. --KoLite:Retention:Enabled=false  disables the retention pruner so this secondary instance
   never prunes the database the live app already maintains.
4. --KoLite:AllowMultipleInstances=true  bypasses the single-instance guard so this UI-only
   instance can run alongside your live app against the SAME database. The guard normally refuses a
   second instance on one database; with the scheduler, worker, and retention all disabled here the
   startup schema apply still writes and may retire obsolete tables. Use -UseCopy when this build's
   schema differs from the live app. The secondary instance logs a "guard is disabled; use a distinct
   database" warning; only a same-schema viewer is an intentional shared-database case.
5. --KoLite:Urls=http://127.0.0.1:<Port>  binds a non-default loopback port so this instance can
   coexist with your live app (usually on http://127.0.0.1:5057). The app reads KoLite:Urls in
   UseUrls(...), so --urls / ASPNETCORE_URLS are ignored; this is the supported way to move it.

Once the app responds, the dashboard URL is opened in your default browser; pass -NoBrowser to skip
that and open it yourself.

Safety notes when running against the live database (the default):

- Startup applies the current schema to whatever database it opens. Pure UI / read-model / Razor
  changes are a no-op, but if your branch changes the schema it WILL be applied to that database.
  Use -UseCopy (or point -DatabasePath at a throwaway file) when your branch changes the schema.
  In particular, upgrading after throttling-advisor retirement drops the obsolete observation
  table even in UI-only mode. Never validate that upgrade against the older app's live database.
- The scheduler being disabled stops automated execution, but the UI still exposes mutating
  operator actions (enable/disable, soft-delete, pause/resume, edit schedule, rerun ack, repair),
  and they write to whatever database is configured. Navigating/inspecting is safe; clicking those
  is not, when running against the live database.
- This instance coexists with a running live app on the same database (the single-instance guard is
  bypassed). Reads and the disabled background services are safe under WAL; the residual risk is the
  startup schema apply above, so still prefer -UseCopy when your branch changes the schema.

-UseCopy snapshots the chosen database (plus its -wal / -shm sidecars when present) to a throwaway
file and runs against that copy, leaving the live database untouched. The copy is best-effort while
the live app is writing; for a perfectly faithful snapshot, copy while the app is idle.

.PARAMETER DatabasePath
Database the UI instance reads. Default: %LOCALAPPDATA%\KoLite\ko-lite.db (the live default db).

.PARAMETER Port
Loopback port for this instance. Default: 5099. Choose one that is free and not your live app's port.

.PARAMETER UseCopy
Run against a throwaway copy of the database instead of the database itself. Leaves the source
database untouched and removes the schema-apply / accidental-mutation risk.

.PARAMETER CopyPath
Destination for -UseCopy. Default: '<source-name>-validate.db' next to the source database.

.PARAMETER AppArguments
Extra arguments passed through to the app after the fixed overrides (for example
'--KoLite:Kusto:AuthMode=AzureCli'). Later values win over the fixed overrides.

.PARAMETER NoBrowser
Do not open a browser. By default the dashboard URL is opened in your default browser once the app responds.

.PARAMETER DryRun
Print the resolved database, port, and exact 'dotnet' command without copying anything or starting
the app.

.EXAMPLE
.\scripts\Start-KoLiteUi.ps1
Views the live default database on port 5099 while your live app keeps running on 5057.

.EXAMPLE
.\scripts\Start-KoLiteUi.ps1 -UseCopy -Port 5099

.EXAMPLE
.\scripts\Start-KoLiteUi.ps1 -DatabasePath 'C:\data\ko-lite.db' -DryRun
#>
param(
    [string]$DatabasePath,
    [ValidateRange(1024, 65535)]
    [int]$Port = 5099,
    [switch]$UseCopy,
    [string]$CopyPath,
    [string[]]$AppArguments = @(),
    [switch]$NoBrowser,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\KoLite.LocalApp\KoLite.LocalApp.csproj'
$defaultDatabasePath = Join-Path (Join-Path $env:LOCALAPPDATA 'KoLite') 'ko-lite.db'

if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    $DatabasePath = $defaultDatabasePath
}

function Copy-DatabaseSnapshot {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        throw "Source database '$Source' does not exist; nothing to copy."
    }

    $destinationDirectory = Split-Path -Parent $Destination
    if (-not [string]::IsNullOrEmpty($destinationDirectory)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }

    Copy-Item -LiteralPath $Source -Destination $Destination -Force

    # Bring the WAL/SHM sidecars along so the copy includes not-yet-checkpointed writes; clear any
    # stale sidecar from a previous copy so the destination .db is never paired with a mismatched WAL.
    foreach ($suffix in '-wal', '-shm') {
        $sourceSidecar = "$Source$suffix"
        $destinationSidecar = "$Destination$suffix"
        if (Test-Path -LiteralPath $sourceSidecar) {
            Copy-Item -LiteralPath $sourceSidecar -Destination $destinationSidecar -Force
        } elseif (Test-Path -LiteralPath $destinationSidecar) {
            Remove-Item -LiteralPath $destinationSidecar -Force
        }
    }
}

function Test-PortInUse {
    param([Parameter(Mandatory)][int]$Port)

    try {
        $listeners = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
        return [bool]$listeners
    } catch {
        # Get-NetTCPConnection is unavailable or failed; skip the pre-flight check rather than block.
        return $false
    }
}

# Resolve the effective database the instance will open.
if ($UseCopy) {
    if ([string]::IsNullOrWhiteSpace($CopyPath)) {
        $sourceDirectory = Split-Path -Parent $DatabasePath
        $sourceBaseName = [System.IO.Path]::GetFileNameWithoutExtension($DatabasePath)
        $CopyPath = Join-Path $sourceDirectory "$sourceBaseName-validate.db"
    }
    $effectiveDatabasePath = $CopyPath
} else {
    $effectiveDatabasePath = $DatabasePath
}

$effectiveUrl = "http://127.0.0.1:$Port"

$dotnetArgs = @(
    'run',
    '--project', $project,
    '--',
    "--ConnectionStrings:KoLiteSqlite=$effectiveDatabasePath",
    '--KoLite:Scheduler:Enabled=false',
    '--KoLite:Retention:Enabled=false',
    '--KoLite:AllowMultipleInstances=true',
    "--KoLite:Urls=$effectiveUrl"
)
if (@($AppArguments).Count -gt 0) {
    $dotnetArgs += $AppArguments
}

$commandPreview = 'dotnet ' + ($dotnetArgs -join ' ')

Write-Host 'Kusto Slice Runner UI (source build, scheduler/worker disabled)'
Write-Host "Project       : $project"
if ($UseCopy) {
    Write-Host "Database       : $effectiveDatabasePath (copy of $DatabasePath)"
} else {
    Write-Host "Database       : $effectiveDatabasePath (live)"
}
Write-Host "Url            : $effectiveUrl"
Write-Host 'Scheduler      : disabled (no slice claims, no Kusto execution)'
Write-Host 'Performance    : stored observations only (collection and backfill disabled with execution)'
Write-Host 'Retention      : disabled (does not prune the shared database)'
Write-Host 'Coexistence    : shared-database guard bypassed (--KoLite:AllowMultipleInstances=true)'
if ($NoBrowser) {
    Write-Host 'Browser        : not opened (-NoBrowser)'
} else {
    Write-Host 'Browser        : default browser (opens when ready)'
}
Write-Host "Command        : $commandPreview"

if (-not $UseCopy) {
    Write-Host ''
    Write-Host 'WARNING: this instance opens the live database read/write.' -ForegroundColor Yellow
    Write-Host '  - Startup applies any schema change your branch adds to this database.' -ForegroundColor Yellow
    Write-Host '  - UI actions (disable/delete/edit/rerun/repair) write to this database.' -ForegroundColor Yellow
    Write-Host '  - Re-run with -UseCopy to validate against a throwaway copy instead.' -ForegroundColor Yellow
}

if ($DryRun) {
    Write-Host ''
    if ($UseCopy) {
        Write-Host "DryRun: would copy $DatabasePath (and -wal/-shm sidecars) to $effectiveDatabasePath."
    }
    if (-not $NoBrowser) {
        Write-Host "DryRun: would open $effectiveUrl in your default browser once the app responds."
    }
    Write-Host "DryRun: would verify port $Port is free, then start the app. Nothing was copied or started."
    return
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "Could not find the LocalApp project at '$project'. Run this script from the ko-lite repository."
}

if (Test-PortInUse -Port $Port) {
    throw "Port $Port is already in use. Pass -Port with a free port (your live app is usually on 5057)."
}

if ($UseCopy) {
    Write-Host ''
    Write-Host "Copying database to $effectiveDatabasePath ..."
    Copy-DatabaseSnapshot -Source $DatabasePath -Destination $effectiveDatabasePath
}

$browserJob = $null
if (-not $NoBrowser) {
    # The foreground 'dotnet run' below blocks until Ctrl+C, so poll for readiness in a background
    # job and open the default browser once the app answers, then run the app in the foreground.
    $browserJob = Start-Job -Name 'KoLiteUiBrowser' -ArgumentList $effectiveUrl -ScriptBlock {
        param([string]$Url)
        $ProgressPreference = 'SilentlyContinue'
        $healthUrl = "$Url/healthz"
        $deadline = (Get-Date).AddSeconds(90)
        $ready = $false
        while ((Get-Date) -lt $deadline) {
            try {
                Invoke-WebRequest -Uri $healthUrl -TimeoutSec 2 -UseBasicParsing | Out-Null
                $ready = $true
                break
            } catch {
                # Any HTTP response (even an error status) means the server is up; only a
                # connection failure (no Response) means keep waiting.
                if ($null -ne $_.Exception.Response) { $ready = $true; break }
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $ready) { return }

        # ShellExecute opens the URL in the machine's default browser.
        Start-Process -FilePath $Url
    }
}

Write-Host ''
Write-Host "Starting the UI at $effectiveUrl (press Ctrl+C to stop)..."
$exitCode = 0
try {
    & dotnet @dotnetArgs
    $exitCode = $LASTEXITCODE
} finally {
    if ($null -ne $browserJob) {
        Remove-Job -Job $browserJob -Force -ErrorAction SilentlyContinue
    }
}
exit $exitCode
