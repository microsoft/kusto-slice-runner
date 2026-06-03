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

Write-Host 'KO Lite graceful drain stop'
Write-Host "DrainUrl: $drainUrl"
Write-Host "StatusUrl: $statusUrl"
Write-Host "Timeout: $Timeout"

if ($DryRun) {
    Write-Host 'DryRun: no HTTP request is sent.'
    return
}

$response = Invoke-RestMethod -Method Post -Uri $drainUrl -TimeoutSec 10
Write-Host "Drain requested: mode=$($response.mode), activeWorkerCount=$($response.activeWorkerCount)"

if ($NoWait) {
    Write-Host 'NoWait: drain request submitted; not waiting for app exit.'
    return
}

$deadline = [DateTimeOffset]::UtcNow.Add($Timeout)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    Start-Sleep -Seconds $PollIntervalSeconds
    try {
        $status = Invoke-RestMethod -Method Get -Uri $statusUrl -TimeoutSec 5
        Write-Host "Still running: mode=$($status.mode), activeWorkerCount=$($status.activeWorkerCount)"
    } catch {
        Write-Host 'KO Lite app is no longer responding; graceful drain stop completed.'
        return
    }
}

throw "Timed out waiting for KO Lite app to stop after $Timeout. Active work may still be draining in the app."
