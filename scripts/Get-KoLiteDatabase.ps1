# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Reports which local SQLite database the Kusto Slice Runner app is using.

.DESCRIPTION
The in-use database path is resolved at runtime from configuration precedence
(ConnectionStrings:KoLiteSqlite -> KoLite:DatabasePath -> %LOCALAPPDATA%\KoLite\ko-lite.db),
so it cannot be read reliably from appsettings.json alone.

This script reports the path that is actually in use:

1. If the local app is running, it queries GET <BaseUrl>/api/v1/system/status and returns the
   authoritative 'database.path' value the app resolved at startup.
2. If the app is not running, it reports the default path and makes a best-effort guess at
   the most recently used database file in DatabaseRoot, flagging the likely live file by
   WAL/SHM sidecar presence and most-recent write time while ignoring backup/copy files and
   the *.db-wal / *.db-shm sidecars themselves.

The script is read-only and never modifies, deletes, or moves any database file.
Only database.path is needed from system status; optional or retired telemetry
fields do not affect database discovery.

.PARAMETER BaseUrl
Loopback base URL of the running local app. Default: http://127.0.0.1:5057

.PARAMETER DatabaseRoot
Folder scanned for database files when the app is not running.
Default: %LOCALAPPDATA%\KoLite

.PARAMETER DryRun
Describe what the script would inspect (health endpoint and fallback scan) without making
the HTTP request or scanning the filesystem.

.EXAMPLE
.\scripts\Get-KoLiteDatabase.ps1

.EXAMPLE
.\scripts\Get-KoLiteDatabase.ps1 -BaseUrl http://127.0.0.1:5099
#>
param(
    [string]$BaseUrl = 'http://127.0.0.1:5057',
    [string]$DatabaseRoot = (Join-Path $env:LOCALAPPDATA 'KoLite'),
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$defaultPath = Join-Path (Join-Path $env:LOCALAPPDATA 'KoLite') 'ko-lite.db'

if ($DryRun) {
    Write-Host 'DryRun: no request or filesystem scan is performed.'
    Write-Host "Would query $($BaseUrl.TrimEnd('/'))/api/v1/system/status for the authoritative database.path."
    Write-Host "If unreachable, would report the default path and scan $DatabaseRoot for the most likely live *.db file."
    Write-Host "Default database path: $defaultPath"
    return
}

function Get-DatabaseFileInfo {
    param(
        [Parameter(Mandatory)][string]$Path
    )

    $item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue
    $walPath = "$Path-wal"
    $shmPath = "$Path-shm"

    return [pscustomobject]@{
        Path             = $Path
        Exists           = $null -ne $item
        SizeBytes        = if ($null -ne $item) { $item.Length } else { $null }
        LastWriteTimeUtc = if ($null -ne $item) { $item.LastWriteTimeUtc } else { $null }
        HasWal           = Test-Path -LiteralPath $walPath
        HasShm           = Test-Path -LiteralPath $shmPath
        Likely           = $false
    }
}

# 1. Authoritative path from the running app.
$health = $null
try {
    $health = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/system/status" -TimeoutSec 5
} catch {
    $health = $null
}

if ($null -ne $health -and $null -ne $health.database -and -not [string]::IsNullOrWhiteSpace($health.database.path)) {
    $databasePath = [string]$health.database.path
    $info = Get-DatabaseFileInfo -Path $databasePath
    $info.Likely = $true

    Write-Host 'Kusto Slice Runner in-use database (authoritative, from running app):'
    Write-Host "  $databasePath"
    Write-Host "  source     : running app /api/v1/system/status ($BaseUrl)"
    if ($info.Exists) {
        Write-Host "  sizeBytes  : $($info.SizeBytes)"
        Write-Host "  lastWrite  : $($info.LastWriteTimeUtc.ToString('o')) (UTC)"
        Write-Host "  sidecars   : wal=$($info.HasWal) shm=$($info.HasShm)"
    } else {
        Write-Host '  note       : the app reported a path that does not exist on disk yet.'
    }

    return [pscustomobject]@{
        DatabasePath = $databasePath
        Source       = 'RunningApp'
        IsRunning    = $true
        DatabaseRoot = $DatabaseRoot
        Candidates   = @($info)
    }
}

# 2. App not running: report the default and make a best-effort guess.
Write-Host "Kusto Slice Runner app is not responding at $BaseUrl; reporting a best-effort guess." -ForegroundColor Yellow
Write-Host "Default database path (used when no connection string is configured):"
Write-Host "  $defaultPath"

$candidates = @()
if (Test-Path -LiteralPath $DatabaseRoot) {
    $candidates = Get-ChildItem -LiteralPath $DatabaseRoot -Filter '*.db' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.BaseName -notmatch '(?i)(copy|backup|\bbak\b)' } |
        ForEach-Object { Get-DatabaseFileInfo -Path $_.FullName }
}

if (@($candidates).Count -gt 0) {
    # Rank: live WAL/SHM sidecars first, then most recent write time. The top candidate is the
    # likely in-use database.
    $ranked = @($candidates) | Sort-Object -Property @{ Expression = { $_.HasWal -or $_.HasShm }; Descending = $true }, @{ Expression = { $_.LastWriteTimeUtc }; Descending = $true }
    $ranked[0].Likely = $true

    Write-Host ''
    Write-Host "Likely in-use database (best-effort; app is stopped):"
    Write-Host "  $($ranked[0].Path)"
    Write-Host "  sizeBytes  : $($ranked[0].SizeBytes)"
    Write-Host "  lastWrite  : $($ranked[0].LastWriteTimeUtc.ToString('o')) (UTC)"
    Write-Host "  sidecars   : wal=$($ranked[0].HasWal) shm=$($ranked[0].HasShm)"

    $others = @($ranked | Select-Object -Skip 1)
    if ($others.Count -gt 0) {
        Write-Host ''
        Write-Host "Other database files in $DatabaseRoot (ignored backups/copies excluded):"
        foreach ($other in $others) {
            Write-Host "  $($other.Path)  (lastWrite $($other.LastWriteTimeUtc.ToString('o')), wal=$($other.HasWal) shm=$($other.HasShm))"
        }
    }

    Write-Host ''
    Write-Host 'Start the app and re-run this script for the authoritative path.'

    return [pscustomobject]@{
        DatabasePath = $ranked[0].Path
        Source       = 'StoppedAppGuess'
        IsRunning    = $false
        DatabaseRoot = $DatabaseRoot
        Candidates   = $ranked
    }
}

Write-Host ''
Write-Host "No database files found under $DatabaseRoot. The default path will be created on first run:"
Write-Host "  $defaultPath"

return [pscustomobject]@{
    DatabasePath = $defaultPath
    Source       = 'DefaultPath'
    IsRunning    = $false
    DatabaseRoot = $DatabaseRoot
    Candidates   = @()
}
