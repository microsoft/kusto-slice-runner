# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Recreates documentation screenshots with an isolated, local-only synthetic fixture.

.DESCRIPTION
Publishes the developer-only screenshot host under this checkout's artifacts directory.
Each run owns a new SQLite database and a loopback listener. Scheduling, retention,
updates, Kusto access, and real Copilot invocation are disabled. The live app is not used.
The owned process is stopped in finally; no service or startup task is registered.

.PARAMETER Port
Unused loopback port for the fixture. The normal live port 5057 is refused.

.PARAMETER UpdateImages
Replace the five docs\images PNGs after a successful capture and fixture shutdown.
Without this switch images remain in the printed staging directory for review.

.PARAMETER DryRun
Print the exact isolation paths and intended actions without writing or starting anything.
#>
param(
    [ValidateRange(1024, 65535)][int]$Port = 5107,
    [switch]$UpdateImages,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Port -eq 5057) { throw 'Port 5057 belongs to the normal live app. Choose a separate port.' }
$workspace = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $workspace 'artifacts\documentation-screenshots'
$publishDirectory = Join-Path $artifactRoot 'app'
$runId = [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path $artifactRoot "runs\$runId"
$manifestPath = Join-Path $runDirectory 'manifest.json'
$baseUrl = "http://127.0.0.1:$Port"
$project = Join-Path $workspace 'tests\KoLite.LocalApp.ScreenshotHost\KoLite.LocalApp.ScreenshotHost.csproj'
$imageNames = @('job-overview.png', 'job-detail.png', 'dependency-graph-lineage.png', 'activity.png', 'copilot-failure-analysis.png')

Write-Host "Screenshot deployment: $publishDirectory"
Write-Host "Fresh fixture database: $(Join-Path $runDirectory 'screenshots.db')"
Write-Host "Fixture URL: $baseUrl"
Write-Host "Staged images: $(Join-Path $runDirectory 'images')"
if ($DryRun) {
    Write-Host 'DryRun: no files, processes, HTTP requests, or external service calls.'
    Write-Host 'Would publish the test-only host, seed fictional jobs, capture five views, then stop only the owned process.'
    if ($UpdateImages) { Write-Host 'Would replace the five docs\images PNGs after successful capture.' }
    return
}

if (-not (Test-Path -LiteralPath (Join-Path $workspace 'node_modules\playwright\package.json'))) {
    throw 'Playwright is missing. Run npm ci in this checkout, then follow docs\screenshots.md to install its browser.'
}
for ($directory = [System.IO.DirectoryInfo]::new($publishDirectory); $null -ne $directory; $directory = $directory.Parent) {
    if ($directory.Exists -and ($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint)) {
        throw "Screenshot deployment paths must not traverse links: $($directory.FullName)"
    }
}
$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
if (@($listeners | Where-Object Port -EQ $Port).Count -gt 0) {
    throw "Port $Port is already in use. Nothing was stopped; choose another screenshot port."
}

& dotnet publish $project --configuration Release --output $publishDirectory --nologo --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Screenshot host publish failed ($LASTEXITCODE). No existing app was stopped." }

$oldBrowserPath = $env:PLAYWRIGHT_BROWSERS_PATH
$env:PLAYWRIGHT_BROWSERS_PATH = Join-Path $artifactRoot 'browsers'
$process = [System.Diagnostics.Process]::new()
$process.StartInfo = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$process.StartInfo.WorkingDirectory = $publishDirectory
$process.StartInfo.UseShellExecute = $false
$process.StartInfo.RedirectStandardOutput = $true
$process.StartInfo.RedirectStandardError = $true
$process.StartInfo.ArgumentList.Add((Join-Path $publishDirectory 'KoLite.LocalApp.ScreenshotHost.dll'))
$process.StartInfo.ArgumentList.Add($workspace)
$process.StartInfo.ArgumentList.Add($runId)
$process.StartInfo.ArgumentList.Add([string]$Port)
$started = $false
$verified = $false
$captured = $false
$stdout = $null
$stderr = $null
try {
    $started = $process.Start()
    if (-not $started) { throw 'Could not start the screenshot host.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
    while (-not (Test-Path -LiteralPath $manifestPath)) {
        if ($process.HasExited) { throw "Screenshot host exited before readiness ($($process.ExitCode)). See the run logs." }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Screenshot host readiness timed out.' }
        Start-Sleep -Milliseconds 250
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.runId -ne $runId -or $manifest.baseUrl -ne $baseUrl -or $manifest.processId -ne $process.Id) {
        throw 'The fixture manifest does not identify the process started by this capture.'
    }
    $response = Invoke-WebRequest -Uri "$baseUrl/api/v1/system/status" -TimeoutSec 10
    $status = $response.Content | ConvertFrom-Json
    if ($response.Headers['X-KoLite-Screenshot-Fixture'] -ne $runId -or
        $status.database.path -ne (Join-Path $runDirectory 'screenshots.db') -or
        $status.database.jobCount -ne 10 -or
        $status.scheduler.enabled -or $status.workerPool.enabled -or
        $status.retention.enabled -or $status.update.enabled) {
        throw 'The responding app is not the expected isolated, execution-disabled fixture.'
    }
    $verified = $true

    & node (Join-Path $PSScriptRoot 'capture-documentation-screenshots.mjs') $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "Browser capture failed ($LASTEXITCODE). Tracked images were not changed." }
    $captured = $true
} finally {
    if ($started) {
        if (-not $process.HasExited -and $verified) {
            try {
                $identity = Invoke-WebRequest -Uri "$baseUrl/healthz" -TimeoutSec 5
                if ($identity.Headers['X-KoLite-Screenshot-Fixture'] -ne $runId) {
                    throw 'Endpoint ownership changed; refusing to send an HTTP shutdown request.'
                }
                & (Join-Path $PSScriptRoot 'Stop-KoLiteApp.ps1') -BaseUrl $baseUrl `
                    -Reason 'documentation-screenshot-capture' -Timeout ([TimeSpan]::FromSeconds(20)) -PollIntervalSeconds 1
            } catch {
                Write-Warning "Owned fixture drain failed: $($_.Exception.Message)"
            }
        }
        if (-not $process.WaitForExit(5000)) {
            Write-Warning "Stopping only the screenshot child process $($process.Id)."
            Stop-Process -Id $process.Id -ErrorAction Stop
            $process.WaitForExit()
        }
        $logDirectory = Join-Path $artifactRoot 'logs'
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $logDirectory "$runId.stdout.log") -Encoding utf8
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $logDirectory "$runId.stderr.log") -Encoding utf8
        $hostExitCode = $process.ExitCode
    }
    $process.Dispose()
    $env:PLAYWRIGHT_BROWSERS_PATH = $oldBrowserPath
}
if (-not $captured -or $hostExitCode -ne 0) {
    throw "Screenshot host did not complete cleanly ($hostExitCode). Tracked images were not changed."
}

$imagesDirectory = Join-Path $runDirectory 'images'
foreach ($name in $imageNames) {
    if (-not (Test-Path -LiteralPath (Join-Path $imagesDirectory $name) -PathType Leaf)) {
        throw "Expected screenshot is missing: $name"
    }
}
if ($UpdateImages) {
    foreach ($name in $imageNames) {
        Copy-Item -LiteralPath (Join-Path $imagesDirectory $name) -Destination (Join-Path $workspace "docs\images\$name") -Force
    }
    Write-Host 'Updated all five documentation screenshots.'
}
Write-Host "Capture complete. Review images at: $imagesDirectory"
Write-Host "Capture evidence: $(Join-Path $runDirectory 'capture.json')"
