param(
    [string]$BaseUrl = 'http://127.0.0.1:5057',
    [string]$Reason = 'operator-request',
    [TimeSpan]$Timeout = ([TimeSpan]::FromMinutes(30)),
    [int]$PollIntervalSeconds = 2,
    [switch]$NoWait,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PollIntervalSeconds -le 0) {
    throw 'PollIntervalSeconds must be greater than zero.'
}

if ($Timeout -le [TimeSpan]::Zero) {
    throw 'Timeout must be greater than zero.'
}

$root = $BaseUrl.TrimEnd('/')
$encodedReason = [System.Uri]::EscapeDataString($Reason)
$drainUrl = "$root/status/shutdown/drain?reason=$encodedReason"
$statusUrl = "$root/status/shutdown"

function Get-ShutdownSnapshot {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSec = 5
    )

    try {
        return Invoke-RestMethod -Method Get -Uri $Url -TimeoutSec $TimeoutSec
    } catch {
        return $null
    }
}

function Confirm-AppStopped {
    param(
        [Parameter(Mandatory)][string]$Url
    )

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        if ($null -ne (Get-ShutdownSnapshot -Url $Url -TimeoutSec 3)) {
            return $false
        }

        Start-Sleep -Milliseconds 300
    }

    return $true
}

Write-Host 'KO Lite graceful drain stop'
Write-Host "DrainUrl: $drainUrl"
Write-Host "StatusUrl: $statusUrl"
Write-Host "Timeout: $Timeout"

if ($DryRun) {
    Write-Host 'DryRun: no HTTP request is sent.'
    return
}

try {
    $response = Invoke-RestMethod -Method Post -Uri $drainUrl -TimeoutSec 10
    Write-Host "Drain requested: mode=$($response.mode), activeWorkerCount=$($response.activeWorkerCount)"
} catch {
    # When the app is already drained it can stop immediately after accepting the request,
    # closing the connection before the HTTP response is fully delivered (for example
    # "The response ended prematurely."). Confirm the real state via the status endpoint
    # instead of treating that as a failure.
    $drainError = $_.Exception.Message
    Write-Host "Drain POST did not return a complete response: $drainError"
    Write-Host 'Confirming KO Lite app state via the status endpoint...'

    if (Confirm-AppStopped -Url $statusUrl) {
        Write-Host 'KO Lite app is no longer responding; graceful drain stop completed.'
        return
    }

    $snapshot = Get-ShutdownSnapshot -Url $statusUrl
    if ($null -ne $snapshot -and $snapshot.mode -ne 'Running') {
        Write-Host "Drain already in progress: mode=$($snapshot.mode), activeWorkerCount=$($snapshot.activeWorkerCount)"
    } else {
        throw "Failed to request graceful drain: $drainError"
    }
}

if ($NoWait) {
    Write-Host 'NoWait: drain request submitted; not waiting for app exit.'
    return
}

$deadline = [DateTimeOffset]::UtcNow.Add($Timeout)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds $PollIntervalSeconds
    $status = Get-ShutdownSnapshot -Url $statusUrl -TimeoutSec 5
    if ($null -eq $status) {
        if (Confirm-AppStopped -Url $statusUrl) {
            Write-Host 'KO Lite app is no longer responding; graceful drain stop completed.'
            return
        }

        continue
    }

    Write-Host "Still running: mode=$($status.mode), activeWorkerCount=$($status.activeWorkerCount)"
}

throw "Timed out waiting for KO Lite app to stop after $Timeout. Active work may still be draining in the app."
