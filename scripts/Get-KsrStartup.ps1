# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

<#
.SYNOPSIS
Reports the current user's Kusto Slice Runner startup registration and last task result.

.DESCRIPTION
Reads Task Scheduler only. A running task does not prove Kusto authentication is valid.
No app process, credentials, or SQLite state is changed.

.PARAMETER DryRun
Print the read-only inspection target without querying Task Scheduler.
#>
param([switch]$DryRun)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Ksr.Startup.psm1') -Force -ErrorAction Stop
$context = Get-KsrStartupContext
if ($DryRun) {
    Write-Host "DryRun: would inspect '$($context.TaskName)' and report logs at '$($context.LogDirectory)'."
    return
}

$task = Get-KsrStartupTask $context
if ($null -eq $task) {
    return [pscustomobject]@{
        Registered = $false
        TaskName = $context.TaskName
        LogDirectory = $context.LogDirectory
    }
}
$configuration = Get-KsrStartupTaskConfiguration $task $context
$info = Get-ScheduledTaskInfo -TaskName $context.TaskName -TaskPath $context.TaskPath -ErrorAction Stop
return [pscustomobject]@{
    Registered = $true
    TaskName = $context.TaskName
    Enabled = $task.Settings.Enabled
    State = $task.State
    AppDirectory = $configuration.appDirectory
    AppArguments = @($configuration.appArguments)
    WindowMode = $configuration.windowMode
    LastRunTime = $info.LastRunTime
    LastTaskResult = $info.LastTaskResult
    LastTaskResultHex = '0x{0:X8}' -f [long]$info.LastTaskResult
    LogDirectory = $context.LogDirectory
}
