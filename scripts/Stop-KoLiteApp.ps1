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
$drainUrl = "$root/control/v1/shutdown/drain"
$statusUrl = "$root/control/v1/shutdown"

function Assert-ShutdownSnapshot {
    param(
        [Parameter(Mandatory)][object]$Snapshot,
        [Parameter(Mandatory)][string]$Url
    )

    $properties = @($Snapshot.PSObject.Properties | ForEach-Object Name)
    $allowedModes = @('Running', 'DrainRequested', 'Drained', 'Stopping')
    $activeWorkerCount = 0
    if (($properties -notcontains 'mode') -or
        ($allowedModes -notcontains ([string]$Snapshot.mode)) -or
        ($properties -notcontains 'activeWorkerCount') -or
        (-not [int]::TryParse([string]$Snapshot.activeWorkerCount, [ref]$activeWorkerCount)) -or
        ($activeWorkerCount -lt 0)) {
        throw "A service is responding at $Url but did not return the expected KO Lite shutdown contract. Use the Stop-KoLiteApp.ps1 version shipped with that app."
    }

    return $Snapshot
}

function Get-ShutdownSnapshot {
    param(
        [Parameter(Mandatory)][string]$Url,
        [int]$TimeoutSec = 5
    )

    try {
        $response = Invoke-WebRequest -Method Get -Uri $Url -TimeoutSec $TimeoutSec -SkipHttpErrorCheck
    } catch [System.Net.Http.HttpRequestException] {
        return $null
    }

    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
        throw "A service is responding at $Url but returned HTTP $([int]$response.StatusCode). Use the Stop-KoLiteApp.ps1 version shipped with that app."
    }

    try {
        $snapshot = $response.Content | ConvertFrom-Json -Depth 20
        return Assert-ShutdownSnapshot -Snapshot $snapshot -Url $Url
    } catch {
        throw "A service is responding at $Url but did not return the expected KO Lite shutdown contract. Use the Stop-KoLiteApp.ps1 version shipped with that app."
    }
}

function Test-TcpEndpoint {
    param(
        [Parameter(Mandatory)][string]$Url
    )

    $uri = [uri]$Url
    $port = if ($uri.IsDefaultPort) {
        if ($uri.Scheme -eq 'https') { 443 } else { 80 }
    } else {
        $uri.Port
    }
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.ConnectAsync($uri.Host, $port)
        return $connect.Wait(1000) -and $client.Connected
    } catch {
        return $false
    } finally {
        $client.Dispose()
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

    return -not (Test-TcpEndpoint -Url $Url)
}

Write-Host 'KO Lite graceful drain stop'
Write-Host "DrainUrl: $drainUrl"
Write-Host "StatusUrl: $statusUrl"
Write-Host "Timeout: $Timeout"

if ($DryRun) {
    Write-Host 'DryRun: no HTTP request is sent.'
    return
}

$initial = Get-ShutdownSnapshot -Url $statusUrl
if ($null -eq $initial) {
    if (Confirm-AppStopped -Url $statusUrl) {
        Write-Host 'KO Lite app is not responding; nothing to stop.'
        return
    }

    throw "A service is listening at $BaseUrl, but KO Lite shutdown status could not be verified. No drain request was sent."
}

if ($initial.mode -eq 'Running') {
    try {
        $body = @{ reason = $Reason } | ConvertTo-Json -Compress
        $response = Invoke-RestMethod -Method Post -Uri $drainUrl -Body $body -ContentType 'application/json' -TimeoutSec 10
        $response = Assert-ShutdownSnapshot -Snapshot $response -Url $drainUrl
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
} else {
    Write-Host "Drain already in progress: mode=$($initial.mode), activeWorkerCount=$($initial.activeWorkerCount)"
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
