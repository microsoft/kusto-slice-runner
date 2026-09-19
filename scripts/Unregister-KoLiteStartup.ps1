<#
.SYNOPSIS
Removes only the current user's owned KO Lite startup task and its stored settings.

.DESCRIPTION
Does not stop KO Lite or delete application files, credentials, logs, or SQLite state.
Use Stop-KoLiteApp.ps1 separately when a graceful shutdown is wanted.

.PARAMETER DryRun
Preview removal without changing Task Scheduler.
#>
[CmdletBinding(SupportsShouldProcess)]
param([switch]$DryRun)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'KoLite.Startup.psm1') -Force -ErrorAction Stop
$context = Get-KoLiteStartupContext
$task = Get-KoLiteStartupTask $context
if ($null -eq $task) {
    Write-Host 'KO Lite startup is not registered; nothing to remove.'
    return
}
if ($DryRun) {
    Write-Host "DryRun: would remove '$($context.TaskName)'. The running app, database, and logs would be left alone."
    return
}
if ($PSCmdlet.ShouldProcess($context.TaskName, 'Remove KO Lite startup registration')) {
    [void](Get-KoLiteStartupTask $context)
    Unregister-ScheduledTask -TaskName $context.TaskName -TaskPath $context.TaskPath -Confirm:$false -ErrorAction Stop
    Write-Host 'Startup removed. The running app, database, and logs were not changed.'
}
