# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Runs a published Kusto Slice Runner local app from a deployed folder.

.DESCRIPTION
Starts the published Kusto Slice Runner app in the foreground (Ctrl+C to stop) from an isolated deployed
copy instead of the repository build output. A self-contained KoLite.LocalApp.exe is preferred
when present; otherwise the script runs KoLite.LocalApp.dll through dotnet. Running from a
deployed folder keeps the repository bin/obj output free, so 'dotnet build' and 'dotnet test'
are not blocked by the running app holding KoLite.LocalApp.dll/.exe.

The app is always started with AppDirectory as its working directory so ASP.NET Core resolves
the published wwwroot files correctly, even when this script is invoked from the repository or
another directory.

The app directory is resolved automatically:

1. If KoLite.LocalApp.exe or KoLite.LocalApp.dll exists next to this script (the script was
   copied into the deployed folder by Publish-KoLiteApp.ps1), that folder is used.
2. Otherwise the default deploy directory %LOCALAPPDATA%\KoLite\run-app is used.

By default no extra configuration flags are passed, so the deployed run behaves exactly like
starting the app with no parameters today: the background scheduler is enabled (live), Kusto
auth is AzureCli, and the default SQLite database %LOCALAPPDATA%\KoLite\ko-lite.db is used.
Pass overrides through -AppArguments, for example a disposable database with the scheduler
disabled:

  -AppArguments '--ConnectionStrings:KoLiteSqlite=...','--KoLite:Scheduler:Enabled=false'

.PARAMETER AppDirectory
Folder that contains the published KoLite.LocalApp.exe or KoLite.LocalApp.dll. Defaults to the
script folder when either entry point is present there, otherwise
%LOCALAPPDATA%\KoLite\run-app.

.PARAMETER AppArguments
Additional arguments passed through to the app (configuration overrides). Defaults to none,
which matches running the app with no parameters.

.PARAMETER DryRun
Print the resolved entrypoint and the exact 'dotnet' command without starting the app.

.PARAMETER StartupConfiguration
Versioned configuration data supplied by Register-KoLiteStartup.ps1. Scheduled startup uses
the selected console/background mode and bounded logs in %LOCALAPPDATA%\KoLite\startup.
Cannot be combined with AppDirectory or AppArguments.

.EXAMPLE
.\Start-KoLiteApp.ps1

.EXAMPLE
.\Start-KoLiteApp.ps1 -AppArguments '--KoLite:Scheduler:Enabled=false'
#>
param(
    [string]$AppDirectory,
    [string[]]$AppArguments = @(),
    [string]$StartupConfiguration,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSBoundParameters.ContainsKey('StartupConfiguration')) {
    if ($PSBoundParameters.ContainsKey('AppDirectory') -or $PSBoundParameters.ContainsKey('AppArguments')) {
        throw 'StartupConfiguration cannot be combined with AppDirectory or AppArguments.'
    }
    Import-Module (Join-Path $PSScriptRoot 'KoLite.Startup.psm1') -Force -ErrorAction Stop
    $startupExitCode = Invoke-KoLiteStartup -Encoded $StartupConfiguration -DryRun:$DryRun
    exit $startupExitCode
}

$exeName = 'KoLite.LocalApp.exe'
$dllName = 'KoLite.LocalApp.dll'
$defaultDeployDirectory = Join-Path (Join-Path $env:LOCALAPPDATA 'KoLite') 'run-app'

if ([string]::IsNullOrWhiteSpace($AppDirectory)) {
    if ((Test-Path -LiteralPath (Join-Path $PSScriptRoot $exeName)) -or
        (Test-Path -LiteralPath (Join-Path $PSScriptRoot $dllName))) {
        $AppDirectory = $PSScriptRoot
    } else {
        $AppDirectory = $defaultDeployDirectory
    }
}

$exePath = Join-Path $AppDirectory $exeName
$dllPath = Join-Path $AppDirectory $dllName
$useExecutable = Test-Path -LiteralPath $exePath

if ($useExecutable) {
    $entrypoint = $exePath
    $commandPreview = "`"$exePath`""
} else {
    $entrypoint = $dllPath
    $commandPreview = "dotnet `"$dllPath`""
}

if (@($AppArguments).Count -gt 0) {
    $commandPreview += ' ' + ($AppArguments -join ' ')
}

Write-Host 'Kusto Slice Runner start (deployed copy)'
Write-Host "AppDirectory: $AppDirectory"
Write-Host "Entrypoint  : $entrypoint"
Write-Host "WorkingDir  : $AppDirectory"
Write-Host "Command     : $commandPreview"

if ($DryRun) {
    Write-Host 'DryRun: the app is not started.'
    return
}

if (-not $useExecutable -and -not (Test-Path -LiteralPath $dllPath)) {
    throw "Could not find $exeName or $dllName in '$AppDirectory'. Publish first with scripts\Publish-KoLiteApp.ps1, or pass -AppDirectory."
}

Write-Host 'Starting the deployed app (press Ctrl+C to stop)...'
$appExitCode = 0
Push-Location -LiteralPath $AppDirectory
try {
    if ($useExecutable) {
        & $exePath @AppArguments
    } else {
        & dotnet $dllPath @AppArguments
    }
    $appExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $appExitCode
