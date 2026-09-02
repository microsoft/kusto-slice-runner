<#
.SYNOPSIS
Publishes the KO Lite local app to an isolated folder so it can run outside the repository.

.DESCRIPTION
Runs 'dotnet publish' for src\KoLite.LocalApp\KoLite.LocalApp.csproj into an isolated output
directory, then copies the Stop-KoLiteApp.ps1 and Start-KoLiteApp.ps1 helper scripts next to
the published app so the deployed folder is self-sufficient. When it finishes it prints the
full deployed path.

Running the deployed copy (with Start-KoLiteApp.ps1) keeps the repository build output free,
so 'dotnet build' and 'dotnet test' are not blocked by a running app holding
KoLite.LocalApp.dll/.exe.

A published DLL cannot be overwritten while an app instance is running from the same output
directory. Use -StopRunning to gracefully drain and stop a running instance (via
Stop-KoLiteApp.ps1) before re-publishing, or stop it yourself first.

.PARAMETER OutputDirectory
Destination folder for the published app. Default: %LOCALAPPDATA%\KoLite\run-app

.PARAMETER Configuration
Build configuration passed to 'dotnet publish'. Default: Release

.PARAMETER BaseUrl
Loopback base URL used to detect or stop a running instance. Default: http://127.0.0.1:5057

.PARAMETER Clean
Remove the contents of OutputDirectory before publishing.

.PARAMETER StopRunning
Gracefully drain and stop a running instance (via Stop-KoLiteApp.ps1 against -BaseUrl) before
publishing. Without this switch the script only warns when an instance appears to be running.

.PARAMETER DryRun
Print the publish and copy steps without running 'dotnet publish' or copying files.

.EXAMPLE
.\scripts\Publish-KoLiteApp.ps1

.EXAMPLE
.\scripts\Publish-KoLiteApp.ps1 -OutputDirectory 'D:\ko-lite-run' -StopRunning -Clean
#>
param(
    [string]$OutputDirectory = (Join-Path (Join-Path $env:LOCALAPPDATA 'KoLite') 'run-app'),
    [string]$Configuration = 'Release',
    [string]$BaseUrl = 'http://127.0.0.1:5057',
    [switch]$Clean,
    [switch]$StopRunning,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsDirectory = $PSScriptRoot
$repositoryRoot = Split-Path -Parent $scriptsDirectory
$projectPath = Join-Path $repositoryRoot 'src\KoLite.LocalApp\KoLite.LocalApp.csproj'
$stopScript = Join-Path $scriptsDirectory 'Stop-KoLiteApp.ps1'
$startScript = Join-Path $scriptsDirectory 'Start-KoLiteApp.ps1'
$helperScripts = @($stopScript, $startScript)

Write-Host 'KO Lite publish (deploy to isolated folder)'
Write-Host "Project        : $projectPath"
Write-Host "OutputDirectory: $OutputDirectory"
Write-Host "Configuration  : $Configuration"

if ($DryRun) {
    Write-Host 'DryRun: no publish, drain, or copy is performed.'
    if ($Clean) { Write-Host "Would remove existing contents of $OutputDirectory." }
    if ($StopRunning) { Write-Host "Would gracefully drain a running instance at $BaseUrl via Stop-KoLiteApp.ps1." }
    Write-Host "Would run: dotnet publish `"$projectPath`" --configuration $Configuration --output `"$OutputDirectory`" --nologo"
    Write-Host "Would copy these helper scripts into ${OutputDirectory}:"
    foreach ($helper in $helperScripts) { Write-Host "  $(Split-Path -Leaf $helper)" }
    Write-Host "Would print the full deployed path: $OutputDirectory"
    return
}

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Could not find the KO Lite app project at '$projectPath'."
}

if ($StopRunning) {
    Write-Host "Draining any running instance at $BaseUrl before publishing..."
    & $stopScript -BaseUrl $BaseUrl
} else {
    $health = $null
    try {
        $statusUrl = "$($BaseUrl.TrimEnd('/'))/api/v1/system/status"
        $response = Invoke-WebRequest -Method Get -Uri $statusUrl -TimeoutSec 3 -SkipHttpErrorCheck
        if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) {
            throw "A service is responding at $statusUrl but returned HTTP $([int]$response.StatusCode). Use the Publish-KoLiteApp.ps1 version shipped with that app."
        }
        $health = $response.Content | ConvertFrom-Json -Depth 20
        $properties = @($health.PSObject.Properties | ForEach-Object Name)
        if ($properties -notcontains 'supportedApiVersions') {
            throw "A service is responding at $statusUrl but did not advertise KO Lite agent API v1. Use the Publish-KoLiteApp.ps1 version shipped with that app."
        }
        if (@($health.supportedApiVersions) -notcontains 'v1') {
            throw "A service is responding at $statusUrl but did not advertise KO Lite agent API v1. Use the Publish-KoLiteApp.ps1 version shipped with that app."
        }
    } catch [System.Net.Http.HttpRequestException] {
        $health = $null
    }

    if ($null -ne $health) {
        Write-Host "Warning: a KO Lite instance is responding at $BaseUrl." -ForegroundColor Yellow
        Write-Host "If it is running from $OutputDirectory the publish will fail on a locked DLL." -ForegroundColor Yellow
        Write-Host 'Re-run with -StopRunning, or stop it first with scripts\Stop-KoLiteApp.ps1.' -ForegroundColor Yellow
    }
}

if ($Clean -and (Test-Path -LiteralPath $OutputDirectory)) {
    Write-Host "Cleaning $OutputDirectory..."
    Get-ChildItem -LiteralPath $OutputDirectory -Force | Remove-Item -Recurse -Force
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Write-Host 'Publishing...'
& dotnet publish $projectPath --configuration $Configuration --output $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

foreach ($helper in $helperScripts) {
    if (Test-Path -LiteralPath $helper) {
        Copy-Item -LiteralPath $helper -Destination $OutputDirectory -Force
        Write-Host "Copied $(Split-Path -Leaf $helper) into the deployed folder."
    } else {
        Write-Host "Warning: helper script not found, skipped: $helper" -ForegroundColor Yellow
    }
}

$resolvedOutput = (Resolve-Path -LiteralPath $OutputDirectory).Path
$deployedExecutable = Join-Path $resolvedOutput 'KoLite.LocalApp.exe'
$deployedDll = Join-Path $resolvedOutput 'KoLite.LocalApp.dll'
$deployedEntrypoint = if (Test-Path -LiteralPath $deployedExecutable) {
    $deployedExecutable
} else {
    $deployedDll
}

Write-Host ''
Write-Host 'KO Lite deployed successfully.'
Write-Host "Deployed path: $resolvedOutput"
Write-Host "Entrypoint   : $deployedEntrypoint"
Write-Host 'Run it from the deployed folder with:'
Write-Host "  cd `"$resolvedOutput`""
Write-Host '  .\Start-KoLiteApp.ps1'

return [pscustomobject]@{
    DeployedPath  = $resolvedOutput
    Entrypoint    = $deployedEntrypoint
    Configuration = $Configuration
    CopiedScripts = @($helperScripts | ForEach-Object { Split-Path -Leaf $_ })
}
