<#
.SYNOPSIS
Opts the current Windows user into KO Lite startup at sign-in.

.DESCRIPTION
Registers a limited-privilege, passwordless interactive task for a published app. It does not
start or stop KO Lite. Console mode is the first-registration default; Background hides the
window. Updates preserve unspecified arguments and window mode. Settings are stored as data
in the task, not credentials; never pass secrets through AppArguments.

.PARAMETER AppDirectory
Published folder. Defaults to an adjacent app, the existing registration, or
%LOCALAPPDATA%\KoLite\run-app, in that order.

.PARAMETER AppArguments
Application configuration overrides. Omit to preserve existing overrides; pass @() to clear.

.PARAMETER WindowMode
Console shows live output; Background hides the window. Changes apply on the next start.

.PARAMETER DryRun
Validate and preview without registering a task, writing files, or launching the app.

.EXAMPLE
.\Register-KoLiteStartup.ps1

.EXAMPLE
.\Register-KoLiteStartup.ps1 -WindowMode Background -DryRun
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$AppDirectory,
    [AllowEmptyCollection()][string[]]$AppArguments = @(),
    [ValidateSet('Console', 'Background')][string]$WindowMode = 'Console',
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KoLite.Startup.psm1') -Force -ErrorAction Stop

$context = Get-KoLiteStartupContext
$existing = Get-KoLiteStartupTask $context
$previous = if ($null -ne $existing) { Get-KoLiteStartupTaskConfiguration $existing $context } else { $null }
if (-not $PSBoundParameters.ContainsKey('AppDirectory')) {
    if ((Test-Path -LiteralPath (Join-Path $PSScriptRoot 'KoLite.LocalApp.exe')) -or
        (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'KoLite.LocalApp.dll'))) {
        $AppDirectory = $PSScriptRoot
    } elseif ($null -ne $previous) {
        $AppDirectory = $previous.appDirectory
    } else {
        $AppDirectory = Join-Path $env:LOCALAPPDATA 'KoLite\run-app'
    }
}
if ($null -ne $previous) {
    if (-not $PSBoundParameters.ContainsKey('AppArguments')) { $AppArguments = @($previous.appArguments) }
    if (-not $PSBoundParameters.ContainsKey('WindowMode')) { $WindowMode = $previous.windowMode }
}
$configuration = New-KoLiteStartupConfiguration -AppDirectory $AppDirectory -AppArguments $AppArguments `
    -WindowMode $WindowMode -Context $context
$entrypoint = Get-KoLiteStartupEntrypoint $configuration.appDirectory
$launcher = Join-Path $configuration.appDirectory 'Start-KoLiteApp.ps1'
$module = Join-Path $configuration.appDirectory 'KoLite.Startup.psm1'
if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) -or -not (Test-Path -LiteralPath $module -PathType Leaf)) {
    throw 'The published folder is missing startup helpers. Deploy the matching scripts before registering startup.'
}
$tokens = $null
$parseErrors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0 -or $null -eq $syntax.ParamBlock -or
    @($syntax.ParamBlock.Parameters.Name.VariablePath.UserPath) -notcontains 'StartupConfiguration') {
    throw 'The published start script does not support startup configuration. Deploy the matching scripts first.'
}
$definition = New-KoLiteStartupTask $configuration $context

Write-Host "Task         : $($context.TaskName)"
Write-Host "AppDirectory : $($configuration.appDirectory)"
Write-Host "Entrypoint   : $($entrypoint.FileName)"
Write-Host "WindowMode   : $($configuration.windowMode)"
Write-Host "AppArguments : $($configuration.appArguments -join ' ')"
Write-Host "Startup logs : $($context.LogDirectory)"
Write-Host 'At the next sign-in, existing enabled jobs will resume according to these settings.'
if ($DryRun) {
    Write-Host 'DryRun: no task is registered, no files are written, and the app is not started.'
    return
}
if (-not $PSCmdlet.ShouldProcess($context.TaskName, 'Register KO Lite startup at sign-in')) { return }

$current = Get-KoLiteStartupTask $context
if (($null -eq $existing) -ne ($null -eq $current) -or
    ($null -ne $current -and $current.Actions[0].Arguments -cne $existing.Actions[0].Arguments)) {
    throw 'The startup registration changed during preparation. Inspect it and retry.'
}
$parameters = @{
    TaskName = $context.TaskName
    TaskPath = $context.TaskPath
    InputObject = $definition
    ErrorAction = 'Stop'
}
if ($null -ne $current) { $parameters.Force = $true }
Register-ScheduledTask @parameters | Out-Null
Write-Host 'Startup registered. The running app was not changed; this takes effect at the next sign-in.'
