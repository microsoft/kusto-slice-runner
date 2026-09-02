<#
.SYNOPSIS
Reclaims free space in the KO Lite local SQLite database by running VACUUM.

.DESCRIPTION
KO Lite's retention background service prunes old operational telemetry so the database stops
growing without bound, but SQLite does not return freed pages to the operating system on its own:
the file size plateaus and freed pages are reused for future growth. Run this script to physically
shrink the file with a one-off VACUUM after retention has removed a large backlog.

VACUUM rewrites the whole database and needs exclusive access plus enough free disk for a temporary
copy, so the KO Lite app must be stopped first. The script:

1. Resolves the in-use database path (from the running app's /api/v1/system/status, an explicit
   -DatabasePath, or the default %LOCALAPPDATA%\KoLite\ko-lite.db).
2. Refuses to run while the app is responding, unless -Force is supplied (not recommended:
   VACUUM will usually fail with "database is locked" while the app holds a connection).
3. Reports the database size before and after, using the same Microsoft.Data.Sqlite engine the
   app itself uses (loaded from the deployed app folder or the repository build output).

The script never deletes the database and never creates a new one: if the resolved file does not
exist it stops without changes.

.PARAMETER BaseUrl
Loopback base URL of the local app, queried to resolve the in-use database path and to detect
whether the app is running. Default: http://127.0.0.1:5057

.PARAMETER DatabasePath
Explicit path to the database file to vacuum. Overrides health/default resolution.

.PARAMETER AppDirectory
Folder containing Microsoft.Data.Sqlite.dll (the deployed app folder). Used to load the SQLite
engine. Default: %LOCALAPPDATA%\KoLite\run-app

.PARAMETER Force
Attempt the VACUUM even if the app appears to be running. Not recommended.

.PARAMETER DryRun
Resolve the database path and report its current size without modifying anything.

.EXAMPLE
.\scripts\Invoke-KoLiteVacuum.ps1 -DryRun

.EXAMPLE
.\scripts\Stop-KoLiteApp.ps1
.\scripts\Invoke-KoLiteVacuum.ps1
#>
param(
    [string]$BaseUrl = 'http://127.0.0.1:5057',
    [string]$DatabasePath,
    [string]$AppDirectory = (Join-Path $env:LOCALAPPDATA 'KoLite\run-app'),
    [switch]$Force,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-DatabaseSizeBytes {
    param([Parameter(Mandatory)][string]$Path)
    $total = 0L
    foreach ($suffix in @('', '-wal', '-shm')) {
        $item = Get-Item -LiteralPath "$Path$suffix" -ErrorAction SilentlyContinue
        if ($null -ne $item) { $total += $item.Length }
    }
    return $total
}

function Resolve-SqliteAssembly {
    param([string]$AppDirectory)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($AppDirectory)) {
        $candidates += (Join-Path $AppDirectory 'Microsoft.Data.Sqlite.dll')
    }

    # Repository build output fallback so the script also works from a dev checkout.
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $buildRoot = Join-Path $repoRoot 'src\KoLite.LocalApp\bin'
    if (Test-Path -LiteralPath $buildRoot) {
        $candidates += Get-ChildItem -LiteralPath $buildRoot -Recurse -Filter 'Microsoft.Data.Sqlite.dll' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -ExpandProperty FullName
    }

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return $candidate
        }
    }

    throw "Could not find Microsoft.Data.Sqlite.dll. Pass -AppDirectory pointing at the deployed app folder (it contains Microsoft.Data.Sqlite.dll), or build the solution first."
}

# 1. Resolve the database path and whether the app is running.
$isRunning = $false
$health = $null
try {
    $statusUrl = "$($BaseUrl.TrimEnd('/'))/api/v1/system/status"
    $response = Invoke-WebRequest -Method Get -Uri $statusUrl -TimeoutSec 5 -SkipHttpErrorCheck
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
        throw "A service is responding at $statusUrl but returned HTTP $([int]$response.StatusCode). Use the Invoke-KoLiteVacuum.ps1 version shipped with that app."
    }
    $health = $response.Content | ConvertFrom-Json -Depth 20
    $properties = @($health.PSObject.Properties | ForEach-Object Name)
    if ($properties -notcontains 'supportedApiVersions') {
        throw "A service is responding at $statusUrl but did not advertise KO Lite agent API v1. Use the Invoke-KoLiteVacuum.ps1 version shipped with that app."
    }
    if (@($health.supportedApiVersions) -notcontains 'v1') {
        throw "A service is responding at $statusUrl but did not advertise KO Lite agent API v1. Use the Invoke-KoLiteVacuum.ps1 version shipped with that app."
    }
    $isRunning = $true
} catch [System.Net.Http.HttpRequestException] {
    $isRunning = $false
}

if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
    if ($isRunning) {
        $properties = @($health.PSObject.Properties | ForEach-Object Name)
        if ($properties -notcontains 'database' -or $null -eq $health.database) {
            throw "A service is responding at $statusUrl but did not return the expected KO Lite database path. Use the Invoke-KoLiteVacuum.ps1 version shipped with that app."
        }
        if ((@($health.database.PSObject.Properties | ForEach-Object Name) -notcontains 'path') -or ([string]::IsNullOrWhiteSpace($health.database.path))) {
            throw "A service is responding at $statusUrl but did not return the expected KO Lite database path. Use the Invoke-KoLiteVacuum.ps1 version shipped with that app."
        }
        $DatabasePath = [string]$health.database.path
    } else {
        $DatabasePath = Join-Path (Join-Path $env:LOCALAPPDATA 'KoLite') 'ko-lite.db'
    }
}

Write-Host "KO Lite database : $DatabasePath"
Write-Host "App responding   : $isRunning ($BaseUrl)"

if (-not (Test-Path -LiteralPath $DatabasePath)) {
    Write-Host "Database file does not exist; nothing to vacuum." -ForegroundColor Yellow
    return
}

$beforeBytes = Get-DatabaseSizeBytes -Path $DatabasePath
Write-Host ("Current size     : {0:N1} MB ({1:N0} bytes, incl. -wal/-shm)" -f ($beforeBytes / 1MB), $beforeBytes)

if ($DryRun) {
    Write-Host 'DryRun: no changes made. Stop the app, then re-run without -DryRun to VACUUM.'
    return
}

if ($isRunning -and -not $Force) {
    Write-Host ''
    Write-Host 'The KO Lite app appears to be running. VACUUM needs exclusive access and will fail while the app holds a connection.' -ForegroundColor Yellow
    Write-Host 'Stop it first (graceful drain) and re-run:' -ForegroundColor Yellow
    Write-Host '  .\scripts\Stop-KoLiteApp.ps1'
    Write-Host '  .\scripts\Invoke-KoLiteVacuum.ps1'
    Write-Host 'Or pass -Force to attempt anyway (not recommended).'
    throw 'Refusing to VACUUM while the app is running. Stop the app or pass -Force.'
}

# 2. Load the SQLite engine the app uses and run VACUUM.
$assemblyPath = Resolve-SqliteAssembly -AppDirectory $AppDirectory
Write-Host "SQLite engine    : $assemblyPath"
Add-Type -Path $assemblyPath

$connectionString = "Data Source=$DatabasePath;Mode=ReadWrite"
$connection = [Microsoft.Data.Sqlite.SqliteConnection]::new($connectionString)
try {
    $connection.Open()
    $busy = $connection.CreateCommand()
    $busy.CommandText = 'PRAGMA busy_timeout=10000;'
    [void]$busy.ExecuteNonQuery()

    Write-Host 'Running VACUUM...'
    $vacuum = $connection.CreateCommand()
    $vacuum.CommandText = 'VACUUM;'
    [void]$vacuum.ExecuteNonQuery()

    $checkpoint = $connection.CreateCommand()
    $checkpoint.CommandText = 'PRAGMA wal_checkpoint(TRUNCATE);'
    [void]$checkpoint.ExecuteNonQuery()
} finally {
    $connection.Close()
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    $connection.Dispose()
}

$afterBytes = Get-DatabaseSizeBytes -Path $DatabasePath
$reclaimed = $beforeBytes - $afterBytes
Write-Host ("New size         : {0:N1} MB ({1:N0} bytes)" -f ($afterBytes / 1MB), $afterBytes)
Write-Host ("Reclaimed        : {0:N1} MB ({1:N0} bytes)" -f ($reclaimed / 1MB), $reclaimed) -ForegroundColor Green

return [pscustomobject]@{
    DatabasePath   = $DatabasePath
    BeforeBytes    = $beforeBytes
    AfterBytes     = $afterBytes
    ReclaimedBytes = $reclaimed
}
