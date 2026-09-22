# Copyright (c) Microsoft Corporation.
# Licensed under the MIT License.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-KoLiteStartupContext {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'Kusto Slice Runner automatic startup requires Windows.'
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $sid = $identity.User.Value
    } finally {
        $identity.Dispose()
    }

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw 'LOCALAPPDATA is required to locate Kusto Slice Runner startup logs.'
    }

    return [pscustomobject]@{
        OwnerSid = $sid
        TaskName = "Kusto Slice Runner Startup - $sid"
        TaskPath = '\'
        Description = "Kusto Slice Runner automatic startup v1; owner=$sid"
        PowerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        LogDirectory = Join-Path $env:LOCALAPPDATA 'KoLite\startup'
    }
}

function ConvertTo-KoLiteNativeArgument {
    param([AllowEmptyString()][Parameter(Mandatory)][string]$Value)

    # Windows argv quoting: double backslashes before quotes and before the closing quote.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Assert-KoLiteStartupConfiguration {
    param(
        [Parameter(Mandatory)][object]$Configuration,
        [Parameter(Mandatory)][object]$Context
    )

    $fields = @('schemaVersion', 'ownerSid', 'appDirectory', 'appArguments', 'windowMode')
    $actual = @($Configuration.PSObject.Properties.Name)
    if (@(Compare-Object $fields $actual).Count -ne 0) {
        throw 'Invalid startup configuration fields. Re-register startup with the matching Kusto Slice Runner scripts.'
    }
    if (($Configuration.schemaVersion -isnot [int] -and $Configuration.schemaVersion -isnot [long]) -or
        $Configuration.schemaVersion -ne 1) {
        throw 'Unsupported startup configuration version.'
    }
    if ($Configuration.ownerSid -cne $Context.OwnerSid) {
        throw 'Startup configuration belongs to a different Windows user.'
    }
    if ($Configuration.windowMode -cnotin @('Console', 'Background')) {
        throw 'Startup windowMode must be Console or Background.'
    }
    if ($Configuration.appDirectory -isnot [string] -or
        [string]::IsNullOrWhiteSpace($Configuration.appDirectory) -or
        -not [IO.Path]::IsPathRooted($Configuration.appDirectory) -or
        $Configuration.appDirectory.IndexOfAny([char[]]"`0`r`n`"") -ge 0) {
        throw 'Startup appDirectory must be an absolute filesystem path.'
    }
    if ($Configuration.appArguments -isnot [array]) {
        throw 'Startup appArguments must be an array of strings.'
    }
    foreach ($argument in $Configuration.appArguments) {
        if ($argument -isnot [string] -or $argument.Contains([string][char]0)) {
            throw 'Startup appArguments must contain only strings without null characters.'
        }
        if ($argument -match '(?i)^--?[^=]*(password|secret|token|credential|api[-_]?key)(=|$)') {
            throw 'Do not persist credentials in startup arguments. Use your existing user authentication instead.'
        }
    }
}

function New-KoLiteStartupConfiguration {
    param(
        [Parameter(Mandatory)][string]$AppDirectory,
        [AllowEmptyCollection()][string[]]$AppArguments = @(),
        [ValidateSet('Console', 'Background')][string]$WindowMode = 'Console',
        [Parameter(Mandatory)][object]$Context
    )

    $directory = Get-Item -LiteralPath $AppDirectory -ErrorAction Stop
    if ($directory -isnot [IO.DirectoryInfo]) {
        throw "AppDirectory is not a filesystem directory: $AppDirectory"
    }
    $configuration = [pscustomobject][ordered]@{
        schemaVersion = 1
        ownerSid = $Context.OwnerSid
        appDirectory = $directory.FullName
        appArguments = @($AppArguments)
        windowMode = if ($WindowMode -ieq 'Console') { 'Console' } else { 'Background' }
    }
    Assert-KoLiteStartupConfiguration $configuration $Context
    return $configuration
}

function ConvertTo-KoLiteStartupConfiguration {
    param([Parameter(Mandatory)][object]$Configuration)

    $json = ConvertTo-Json -InputObject $Configuration -Depth 4 -Compress
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    if ($encoded.Length -gt 16000) {
        throw 'Startup configuration is too large. Put non-secret application settings in appsettings.json instead.'
    }
    return $encoded
}

function ConvertFrom-KoLiteStartupConfiguration {
    param(
        [Parameter(Mandatory)][string]$Encoded,
        [Parameter(Mandatory)][object]$Context
    )

    if ($Encoded.Length -gt 16000) {
        throw 'Startup configuration is too large.'
    }
    try {
        $json = [Text.UTF8Encoding]::new($false, $true).GetString([Convert]::FromBase64String($Encoded))
    } catch [FormatException], [Text.DecoderFallbackException] {
        throw 'Invalid encoded startup configuration.'
    }
    $configuration = ConvertFrom-Json -InputObject $json -ErrorAction Stop
    if ($null -eq $configuration -or $configuration -is [array]) {
        throw 'Startup configuration must be an object.'
    }
    Assert-KoLiteStartupConfiguration $configuration $Context
    return $configuration
}

function Get-KoLiteStartupActionArguments {
    param(
        [Parameter(Mandatory)][object]$Configuration,
        [string]$EncodedConfiguration
    )

    $style = if ($Configuration.windowMode -eq 'Console') { 'Normal' } else { 'Hidden' }
    $launcher = ConvertTo-KoLiteNativeArgument (Join-Path $Configuration.appDirectory 'Start-KoLiteApp.ps1')
    $encoded = if ($PSBoundParameters.ContainsKey('EncodedConfiguration')) {
        $EncodedConfiguration
    } else {
        ConvertTo-KoLiteStartupConfiguration $Configuration
    }
    $arguments = "-NoLogo -NoProfile -NonInteractive -WindowStyle $style -File $launcher -StartupConfiguration $encoded"
    if ($arguments.Length -gt 30000) {
        throw 'The startup command exceeds the supported command-line length.'
    }
    return $arguments
}

function Get-KoLiteStartupTask {
    param([Parameter(Mandatory)][object]$Context)

    $tasks = @(Get-ScheduledTask -ErrorAction Stop | Where-Object {
        $_.TaskName -eq $Context.TaskName -and $_.TaskPath -eq $Context.TaskPath
    })
    if ($tasks.Count -eq 0) { return $null }
    if ($tasks.Count -ne 1) { throw 'Multiple tasks matched the Kusto Slice Runner startup identity.' }
    $task = $tasks[0]
    if ($task.Description -cne $Context.Description) {
        throw "Refusing to modify an unrecognized task named '$($Context.TaskName)'."
    }

    $owner = [string]$task.Principal.UserId
    if ($owner -ne $Context.OwnerSid) {
        $owner = [Security.Principal.NTAccount]::new($owner).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($owner -ne $Context.OwnerSid) {
        throw "Task '$($Context.TaskName)' belongs to a different Windows user."
    }
    return $task
}

function Get-KoLiteStartupTaskConfiguration {
    param(
        [Parameter(Mandatory)][object]$Task,
        [Parameter(Mandatory)][object]$Context
    )

    $actions = @($Task.Actions)
    if ($actions.Count -ne 1 -or
        $actions[0].Execute -ine $Context.PowerShellPath -or
        $actions[0].Arguments -cnotmatch ' -StartupConfiguration ([A-Za-z0-9+/]+={0,2})$') {
        throw 'The registered startup action is not recognized. Inspect it before replacing the registration.'
    }
    # JSON escaping differs between Windows PowerShell and PowerShell 7; retain the original data encoding.
    $encoded = $Matches[1]
    $configuration = ConvertFrom-KoLiteStartupConfiguration -Encoded $encoded -Context $Context
    $expectedArguments = Get-KoLiteStartupActionArguments -Configuration $configuration -EncodedConfiguration $encoded
    if ($actions[0].Arguments -cne $expectedArguments -or
        $actions[0].WorkingDirectory -ine $configuration.appDirectory) {
        throw 'The registered startup action does not match its configuration.'
    }
    return $configuration
}

function Get-KoLiteStartupEntrypoint {
    param([Parameter(Mandatory)][string]$AppDirectory)

    $exe = Join-Path $AppDirectory 'KoLite.LocalApp.exe'
    if (Test-Path -LiteralPath $exe -PathType Leaf) {
        return [pscustomobject]@{ FileName = $exe; Arguments = @() }
    }
    $dll = Join-Path $AppDirectory 'KoLite.LocalApp.dll'
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "No published Kusto Slice Runner executable or DLL exists in '$AppDirectory'."
    }
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction Stop
    return [pscustomobject]@{ FileName = $dotnet.Source; Arguments = @($dll) }
}

function New-KoLiteStartupTask {
    param(
        [Parameter(Mandatory)][object]$Configuration,
        [Parameter(Mandatory)][object]$Context
    )

    Assert-KoLiteStartupConfiguration $Configuration $Context
    $action = New-ScheduledTaskAction -Execute $Context.PowerShellPath `
        -Argument (Get-KoLiteStartupActionArguments $Configuration) -WorkingDirectory $Configuration.appDirectory
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Context.OwnerSid
    $principal = New-ScheduledTaskPrincipal -UserId $Context.OwnerSid -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    return New-ScheduledTask -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description $Context.Description
}

function New-KoLiteStartupLog {
    param([Parameter(Mandatory)][object]$Context)

    [void][IO.Directory]::CreateDirectory($Context.LogDirectory)
    return [pscustomobject]@{
        Path = Join-Path $Context.LogDirectory 'startup.log'
        MaximumBytes = 1MB
        RetainedFiles = 3
        Encoding = [Text.UTF8Encoding]::new($false)
    }
}

function Write-KoLiteStartupLog {
    param(
        [Parameter(Mandatory)][object]$Log,
        [AllowEmptyString()][Parameter(Mandatory)][string]$Message
    )

    if ($Message.Length -gt 4096) { $Message = $Message.Substring(0, 4096) + ' [truncated]' }
    $line = '{0} {1}{2}' -f [DateTimeOffset]::UtcNow.ToString('o'), $Message, [Environment]::NewLine
    $size = if ([IO.File]::Exists($Log.Path)) { [IO.FileInfo]::new($Log.Path).Length } else { 0 }
    if ($size + $Log.Encoding.GetByteCount($line) -gt $Log.MaximumBytes) {
        $oldest = "$($Log.Path).$($Log.RetainedFiles)"
        if ([IO.File]::Exists($oldest)) { [IO.File]::Delete($oldest) }
        for ($index = $Log.RetainedFiles - 1; $index -ge 1; $index--) {
            $source = "$($Log.Path).$index"
            if ([IO.File]::Exists($source)) { [IO.File]::Move($source, "$($Log.Path).$($index + 1)") }
        }
        if ([IO.File]::Exists($Log.Path)) { [IO.File]::Move($Log.Path, "$($Log.Path).1") }
    }
    [IO.File]::AppendAllText($Log.Path, $line, $Log.Encoding)
}

function Invoke-KoLiteStartup {
    param(
        [Parameter(Mandatory)][string]$Encoded,
        [switch]$DryRun
    )

    $context = Get-KoLiteStartupContext
    if ($DryRun) {
        $configuration = ConvertFrom-KoLiteStartupConfiguration $Encoded $context
        $entrypoint = Get-KoLiteStartupEntrypoint $configuration.appDirectory
        Write-Host "DryRun: would start $($entrypoint.FileName) in $($configuration.windowMode) mode."
        Write-Host "WorkingDir: $($configuration.appDirectory)"
        Write-Host "LogDirectory: $($context.LogDirectory)"
        return 0
    }

    $mutex = [Threading.Mutex]::new($false, "KoLite-startup-$($context.OwnerSid)")
    $acquired = $false
    $log = $null
    $process = $null
    try {
        try { $acquired = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $acquired = $true }
        if (-not $acquired) { throw 'A Kusto Slice Runner startup launcher is already running in this Windows session.' }
        $log = New-KoLiteStartupLog $context
        Write-KoLiteStartupLog $log 'Starting Kusto Slice Runner from Windows sign-in.'
        $configuration = ConvertFrom-KoLiteStartupConfiguration $Encoded $context
        $entrypoint = Get-KoLiteStartupEntrypoint $configuration.appDirectory
        Write-KoLiteStartupLog $log "AppDirectory=$($configuration.appDirectory); WindowMode=$($configuration.windowMode)."
        $console = $configuration.windowMode -eq 'Console'
        if ($console) {
            Write-Host "Kusto Slice Runner - $($configuration.appDirectory)"
            Write-Host "Startup logs: $($log.Path)"
            Write-Host 'Minimize this window to leave Kusto Slice Runner running. Use Stop-KoLiteApp.ps1 for a graceful stop.'
        }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo.FileName = $entrypoint.FileName
        $arguments = @($entrypoint.Arguments) + @($configuration.appArguments)
        $process.StartInfo.Arguments = (@($arguments | ForEach-Object { ConvertTo-KoLiteNativeArgument $_ }) -join ' ')
        $process.StartInfo.WorkingDirectory = $configuration.appDirectory
        $process.StartInfo.UseShellExecute = $false
        $process.StartInfo.RedirectStandardOutput = $true
        $process.StartInfo.RedirectStandardError = $true
        [void]$process.Start()

        $outputRead = $process.StandardOutput.ReadLineAsync()
        $errorRead = $process.StandardError.ReadLineAsync()
        $loggingError = $null
        while ($null -ne $outputRead -or $null -ne $errorRead) {
            $pending = @()
            if ($null -ne $outputRead) { $pending += $outputRead }
            if ($null -ne $errorRead) { $pending += $errorRead }
            [void][Threading.Tasks.Task]::WaitAny([Threading.Tasks.Task[]]$pending)
            foreach ($channel in @('output', 'error')) {
                $read = if ($channel -eq 'output') { $outputRead } else { $errorRead }
                if ($null -eq $read -or -not $read.IsCompleted) { continue }
                $line = $read.GetAwaiter().GetResult()
                if ($null -ne $line) {
                    if ($console) { Write-Host $line }
                    if ($null -eq $loggingError) {
                        try {
                            Write-KoLiteStartupLog $log "[$channel] $line"
                        } catch [IO.IOException], [UnauthorizedAccessException] {
                            $loggingError = $_.Exception.Message
                            Write-Warning "Startup logging failed: $loggingError. Kusto Slice Runner will keep running; inspect its dashboard."
                        }
                    }
                    $read = if ($channel -eq 'output') { $process.StandardOutput.ReadLineAsync() } else { $process.StandardError.ReadLineAsync() }
                } else {
                    $read = $null
                }
                if ($channel -eq 'output') { $outputRead = $read } else { $errorRead = $read }
            }
        }
        $process.WaitForExit()
        if ($null -ne $loggingError) { throw "Kusto Slice Runner exited, but startup logging failed: $loggingError" }
        Write-KoLiteStartupLog $log "Kusto Slice Runner exited with code $($process.ExitCode). No automatic restart is configured."
        return $process.ExitCode
    } catch [Management.Automation.RuntimeException], [IO.IOException], [UnauthorizedAccessException], [ComponentModel.Win32Exception], [ArgumentException] {
        if ($null -ne $log) {
            try { Write-KoLiteStartupLog $log "Startup error: $($_.Exception.Message)" }
            catch [IO.IOException], [UnauthorizedAccessException] { Write-Warning "Could not write the startup error log: $($_.Exception.Message)" }
        }
        throw
    } finally {
        if ($null -ne $process) { $process.Dispose() }
        if ($acquired) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

Export-ModuleMember -Function Get-KoLiteStartupContext, New-KoLiteStartupConfiguration, `
    ConvertTo-KoLiteStartupConfiguration, ConvertFrom-KoLiteStartupConfiguration, Get-KoLiteStartupTask, `
    Get-KoLiteStartupTaskConfiguration, Get-KoLiteStartupEntrypoint, New-KoLiteStartupTask, Invoke-KoLiteStartup
