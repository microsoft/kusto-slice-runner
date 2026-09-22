// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace KoLite.LocalApp.Tests
{
    public sealed class StartupScriptTests
    {
        [Theory]
        [InlineData("Console", "Normal")]
        [InlineData("Background", "Hidden")]
        public async Task Task_definition_uses_passwordless_logon_and_preserves_lifetime(string mode, string style)
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                $context = Get-KoLiteStartupContext
                $configuration = New-KoLiteStartupConfiguration -AppDirectory {{Literal(sandbox.AppDirectory)}} -WindowMode {{Literal(mode)}} -Context $context
                $task = New-KoLiteStartupTask $configuration $context
                function Resolve-OwnerSid($value) {
                    if ($value -match '^S-1-') { return $value }
                    return [Security.Principal.NTAccount]::new($value).Translate([Security.Principal.SecurityIdentifier]).Value
                }
                Write-Result @{
                    logonType = [int]$task.Principal.LogonType
                    runLevel = [int]$task.Principal.RunLevel
                    ownerMatches = (Resolve-OwnerSid $task.Principal.UserId) -eq $context.OwnerSid
                    triggerOwnerMatches = (Resolve-OwnerSid $task.Triggers[0].UserId) -eq $context.OwnerSid
                    triggerType = $task.Triggers[0].CimClass.CimClassName
                    limit = $task.Settings.ExecutionTimeLimit
                    multipleInstances = [int]$task.Settings.MultipleInstances
                    noBatteryStart = $task.Settings.DisallowStartIfOnBatteries
                    stopOnBattery = $task.Settings.StopIfGoingOnBatteries
                    restartCount = $task.Settings.RestartCount
                    wake = $task.Settings.WakeToRun
                    idleOnly = $task.Settings.RunOnlyIfIdle
                    workingDirectory = $task.Actions[0].WorkingDirectory
                    arguments = $task.Actions[0].Arguments
                }
                """);

            using var data = ResultJson(result);
            var root = data.RootElement;
            Assert.Equal(3, root.GetProperty("logonType").GetInt32());
            Assert.Equal(0, root.GetProperty("runLevel").GetInt32());
            Assert.True(root.GetProperty("ownerMatches").GetBoolean());
            Assert.True(root.GetProperty("triggerOwnerMatches").GetBoolean());
            Assert.Equal("MSFT_TaskLogonTrigger", root.GetProperty("triggerType").GetString());
            Assert.Equal("PT0S", root.GetProperty("limit").GetString());
            Assert.Equal(2, root.GetProperty("multipleInstances").GetInt32());
            Assert.False(root.GetProperty("noBatteryStart").GetBoolean());
            Assert.False(root.GetProperty("stopOnBattery").GetBoolean());
            Assert.Equal(0, root.GetProperty("restartCount").GetInt32());
            Assert.False(root.GetProperty("wake").GetBoolean());
            Assert.False(root.GetProperty("idleOnly").GetBoolean());
            Assert.Equal(sandbox.AppDirectory, root.GetProperty("workingDirectory").GetString());
            var arguments = root.GetProperty("arguments").GetString()!;
            Assert.Contains($"-WindowStyle {style}", arguments, StringComparison.Ordinal);
            Assert.Contains("-NoProfile -NonInteractive", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("-NoExit", arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-Command ", arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-ExecutionPolicy", arguments, StringComparison.OrdinalIgnoreCase);
            Assert.False(Directory.Exists(sandbox.LogDirectory));
        }

        [Theory]
        [InlineData("-DryRun")]
        [InlineData("-WhatIf")]
        public async Task Registration_previews_have_no_side_effects(string option)
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                {{TaskMocks}}
                & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -AppDirectory {{Literal(sandbox.AppDirectory)}} {{option}}
                Write-Result @{ writes = $global:TaskWrites; taskExists = $null -ne $global:StartupTask }
                """);

            using var data = ResultJson(result);
            Assert.Equal(0, data.RootElement.GetProperty("writes").GetInt32());
            Assert.False(data.RootElement.GetProperty("taskExists").GetBoolean());
            Assert.False(Directory.Exists(sandbox.LogDirectory));
        }

        [Fact]
        public async Task Updating_window_mode_preserves_arguments_and_updates_one_task()
        {
            using var sandbox = new StartupSandbox();
            var arguments = new[] { "--KoLite:Scheduler:Enabled=false", "--KoLite:Urls=http://127.0.0.1:5199" };
            var result = await sandbox.RunAsync($$"""
                {{TaskMocks}}
                $arguments = @({{Data(arguments)}})
                & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -AppDirectory {{Literal(sandbox.AppDirectory)}} -AppArguments $arguments
                $firstName = $global:StartupTask.TaskName
                $context = Get-KoLiteStartupContext
                $first = Get-KoLiteStartupTaskConfiguration $global:StartupTask $context
                & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -WindowMode Background
                & {{sandbox.Script("Register-KoLiteStartup.ps1")}}
                $current = Get-KoLiteStartupTaskConfiguration $global:StartupTask $context
                $status = & {{sandbox.Script("Get-KoLiteStartup.ps1")}}
                Write-Result @{
                    firstMode = $first.windowMode
                    currentMode = $current.windowMode
                    sameTask = $firstName -eq $global:StartupTask.TaskName
                    arguments = @($current.appArguments)
                    registered = $status.Registered
                    statusMode = $status.WindowMode
                    result = $status.LastTaskResultHex
                    writes = $global:TaskWrites
                }
                """);

            using var data = ResultJson(result);
            var root = data.RootElement;
            Assert.Equal("Console", root.GetProperty("firstMode").GetString());
            Assert.Equal("Background", root.GetProperty("currentMode").GetString());
            Assert.True(root.GetProperty("sameTask").GetBoolean());
            Assert.Equal(arguments, root.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
            Assert.True(root.GetProperty("registered").GetBoolean());
            Assert.Equal("Background", root.GetProperty("statusMode").GetString());
            Assert.Equal("0x00000017", root.GetProperty("result").GetString());
            Assert.Equal(3, root.GetProperty("writes").GetInt32());
            Assert.False(Directory.Exists(sandbox.LogDirectory));
        }

        [Fact]
        public async Task Failed_update_keeps_previous_task_and_settings()
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                {{TaskMocks}}
                & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -AppDirectory {{Literal(sandbox.AppDirectory)}}
                $original = $global:StartupTask.Actions[0].Arguments
                $global:FailRegistration = $true
                $message = ''
                try { & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -WindowMode Background }
                catch { $message = $_.Exception.Message }
                Write-Result @{
                    message = $message
                    unchanged = $original -ceq $global:StartupTask.Actions[0].Arguments
                    writes = $global:TaskWrites
                }
                """);

            using var data = ResultJson(result);
            Assert.Contains("simulated registration failure", data.RootElement.GetProperty("message").GetString());
            Assert.True(data.RootElement.GetProperty("unchanged").GetBoolean());
            Assert.Equal(1, data.RootElement.GetProperty("writes").GetInt32());
        }

        [Theory]
        [InlineData("Register-KoLiteStartup.ps1")]
        [InlineData("Unregister-KoLiteStartup.ps1")]
        public async Task Unrecognized_tasks_are_never_modified(string script)
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                {{TaskMocks}}
                $context = Get-KoLiteStartupContext
                $global:StartupTask = [pscustomobject]@{
                    TaskName = $context.TaskName
                    TaskPath = $context.TaskPath
                    Description = 'unrelated task'
                }
                $message = ''
                try { & {{sandbox.Script(script)}} }
                catch { $message = $_.Exception.Message }
                Write-Result @{ message = $message; writes = $global:TaskWrites }
                """);

            using var data = ResultJson(result);
            Assert.Contains("unrecognized task", data.RootElement.GetProperty("message").GetString());
            Assert.Equal(0, data.RootElement.GetProperty("writes").GetInt32());
        }

        [Fact]
        public async Task Scheduler_access_errors_are_not_reported_as_missing_registration()
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                function global:Get-ScheduledTask { [CmdletBinding()]param() throw 'Access denied by test policy' }
                & {{sandbox.Script("Get-KoLiteStartup.ps1")}}
                """);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Access denied by test policy", result.Error, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Unregister_removes_only_the_task_and_is_repeatable_even_if_app_folder_is_gone()
        {
            using var sandbox = new StartupSandbox();
            Directory.CreateDirectory(sandbox.LogDirectory);
            var log = Path.Combine(sandbox.LogDirectory, "startup.log");
            var database = Path.Combine(sandbox.LocalAppData, "KoLite", "ko-lite.db");
            File.WriteAllText(log, "retained log");
            File.WriteAllText(database, "retained database");
            var result = await sandbox.RunAsync($$"""
                {{TaskMocks}}
                & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -AppDirectory {{Literal(sandbox.AppDirectory)}}
                & {{sandbox.Script("Unregister-KoLiteStartup.ps1")}} -DryRun
                $afterPreview = $null -ne $global:StartupTask
                [IO.Directory]::Move({{Literal(sandbox.AppDirectory)}}, {{Literal(sandbox.AppDirectory + "-moved")}})
                & {{sandbox.Script("Unregister-KoLiteStartup.ps1")}}
                & {{sandbox.Script("Unregister-KoLiteStartup.ps1")}}
                $status = & {{sandbox.Script("Get-KoLiteStartup.ps1")}}
                Write-Result @{ afterPreview = $afterPreview; registered = $status.Registered; writes = $global:TaskWrites }
                """);

            using var data = ResultJson(result);
            Assert.True(data.RootElement.GetProperty("afterPreview").GetBoolean());
            Assert.False(data.RootElement.GetProperty("registered").GetBoolean());
            Assert.Equal(2, data.RootElement.GetProperty("writes").GetInt32());
            Assert.Equal("retained log", File.ReadAllText(log));
            Assert.Equal("retained database", File.ReadAllText(database));
        }

        [Theory]
        [InlineData("$configuration.schemaVersion = 2", "version")]
        [InlineData("$configuration.ownerSid = 'S-1-5-18'", "different Windows user")]
        [InlineData("$configuration.appDirectory = 'relative'", "absolute filesystem path")]
        [InlineData("$configuration.windowMode = 'Other'", "windowMode")]
        [InlineData("$configuration.appArguments = 'not an array'", "array of strings")]
        [InlineData("$configuration.appArguments = @('--client-secret=do-not-save')", "credentials")]
        public async Task Invalid_startup_data_cannot_launch_the_app(string mutation, string expected)
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                $context = Get-KoLiteStartupContext
                $configuration = New-KoLiteStartupConfiguration -AppDirectory {{Literal(sandbox.AppDirectory)}} -Context $context
                {{mutation}}
                $encoded = ConvertTo-KoLiteStartupConfiguration $configuration
                & {{sandbox.Script("Start-KoLiteApp.ps1")}} -StartupConfiguration $encoded -DryRun
                exit $LASTEXITCODE
                """);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(expected, result.Error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(sandbox.LogDirectory));
        }

        [Fact]
        public async Task Registration_rejects_an_old_launcher_before_writing()
        {
            using var sandbox = new StartupSandbox();
            File.WriteAllText(Path.Combine(sandbox.AppDirectory, "Start-KoLiteApp.ps1"), "param([switch]$DryRun)");
            var result = await sandbox.RunAsync($$"""
                {{TaskMocks}}
                $message = ''
                try { & {{sandbox.Script("Register-KoLiteStartup.ps1")}} -AppDirectory {{Literal(sandbox.AppDirectory)}} }
                catch { $message = $_.Exception.Message }
                Write-Result @{ message = $message; writes = $global:TaskWrites }
                """);

            using var data = ResultJson(result);
            Assert.Contains("does not support startup configuration", data.RootElement.GetProperty("message").GetString());
            Assert.Equal(0, data.RootElement.GetProperty("writes").GetInt32());
        }

        [Theory]
        [InlineData("Console")]
        [InlineData("Background")]
        public async Task Launcher_preserves_native_arguments_working_directory_and_failure_exit_code(string mode)
        {
            using var sandbox = new StartupSandbox();
            await sandbox.BuildProbeAsync();
            var arguments = new[] { "", "two words", "quote\"inside", @"C:\trailing\", "--label=$(not-code);'text'", "\u03b2", "--fail" };
            var result = await sandbox.RunAsync(sandbox.LaunchScript(arguments, mode));

            Assert.Equal(23, result.ExitCode);
            var log = File.ReadAllText(Path.Combine(sandbox.LogDirectory, "startup.log"));
            Assert.Contains("probe-error", log, StringComparison.Ordinal);
            Assert.Contains("exited with code 23", log, StringComparison.Ordinal);
            Assert.Contains("cwd:" + Encode(sandbox.AppDirectory), log, StringComparison.Ordinal);
            var actualArguments = log.Split('\n')
                .Where(line => line.Contains("[output] arg:", StringComparison.Ordinal))
                .Select(line => line[(line.IndexOf("[output] arg:", StringComparison.Ordinal) + "[output] arg:".Length)..].TrimEnd('\r'))
                .Select(value => Encoding.UTF8.GetString(Convert.FromBase64String(value)));
            Assert.Equal(arguments, actualArguments);
            if (mode == "Console")
            {
                Assert.Contains("probe-ready", result.Output, StringComparison.Ordinal);
                Assert.Contains("probe-error", result.Output, StringComparison.Ordinal);
            }
            else
            {
                Assert.DoesNotContain("probe-ready", result.Output, StringComparison.Ordinal);
                Assert.DoesNotContain("probe-error", result.Output, StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Launcher_streams_output_and_waits_for_the_app_to_exit()
        {
            using var sandbox = new StartupSandbox();
            await sandbox.BuildProbeAsync();
            using var process = sandbox.Start(sandbox.LaunchScript(["--hold"], "Console"));
            var errorRead = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                string? line;
                do
                {
                    line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                }
                while (!line.Contains("probe-ready", StringComparison.Ordinal));

                Assert.False(process.HasExited);
                File.WriteAllText(sandbox.ReleaseFile, "release");
                var outputRead = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                Assert.Contains("probe-finished", await outputRead, StringComparison.Ordinal);
                Assert.Equal(string.Empty, await errorRead);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        [Fact]
        public async Task Launcher_logs_are_bounded_and_keep_the_final_exit_result()
        {
            using var sandbox = new StartupSandbox();
            await sandbox.BuildProbeAsync();
            var result = await sandbox.RunAsync(sandbox.LaunchScript(["--volume"], "Background"));
            Assert.True(result.ExitCode == 0, result.Error);
            var logs = Directory.GetFiles(sandbox.LogDirectory);
            Assert.Equal(4, logs.Length);
            Assert.All(logs, path => Assert.InRange(new FileInfo(path).Length, 1, 1024 * 1024));
            var current = File.ReadAllText(Path.Combine(sandbox.LogDirectory, "startup.log"));
            Assert.Contains("exited with code 0", current, StringComparison.Ordinal);
            Assert.Contains("[truncated]", current, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Startup_dry_run_does_not_launch_or_write_logs()
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync(sandbox.LaunchScript([], "Console", dryRun: true));
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Contains("DryRun: would start", result.Output, StringComparison.Ordinal);
            Assert.False(Directory.Exists(sandbox.LogDirectory));
        }

        [Fact]
        public async Task Invalid_encoded_startup_is_logged_and_fails_without_launching()
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                & {{sandbox.Script("Start-KoLiteApp.ps1")}} -StartupConfiguration 'not-base64'
                exit $LASTEXITCODE
                """);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Invalid encoded startup configuration", result.Error, StringComparison.Ordinal);
            var log = File.ReadAllText(Path.Combine(sandbox.LogDirectory, "startup.log"));
            Assert.Contains("Startup error: Invalid encoded startup configuration", log, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Logging_failure_does_not_interrupt_the_app_and_is_not_reported_as_success()
        {
            using var sandbox = new StartupSandbox();
            await sandbox.BuildProbeAsync();
            using var process = sandbox.Start(sandbox.LaunchScript(["--hold"], "Console"));
            var errorRead = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                string? line;
                do
                {
                    line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                    Assert.NotNull(line);
                }
                while (!line.Contains("probe-ready", StringComparison.Ordinal));

                using var lockedLog = new FileStream(
                    Path.Combine(sandbox.LogDirectory, "startup.log"), FileMode.Open, FileAccess.Read, FileShare.Read);
                File.WriteAllText(sandbox.ReleaseFile, "release");
                var outputRead = process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await outputRead;
                Assert.NotEqual(0, process.ExitCode);
                Assert.Contains("probe-finished", output, StringComparison.Ordinal);
                Assert.Contains("logging failed", output + await errorRead, StringComparison.Ordinal);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        [Fact]
        public async Task Dll_only_startup_passes_the_dll_and_exact_arguments_to_dotnet()
        {
            using var sandbox = new StartupSandbox();
            await sandbox.BuildProbeAsync();
            var dotnetProbe = Path.Combine(sandbox.Root, "dotnet-probe.exe");
            File.Move(Path.Combine(sandbox.AppDirectory, "KoLite.LocalApp.exe"), dotnetProbe);
            var dll = Path.Combine(sandbox.AppDirectory, "KoLite.LocalApp.dll");
            File.WriteAllText(dll, string.Empty);
            var result = await sandbox.RunAsync($$"""
                function global:Get-Command {
                    [CmdletBinding()]param($Name, $CommandType)
                    if ($Name -ne 'dotnet.exe') { throw "Unexpected command lookup: $Name" }
                    return [pscustomobject]@{ Source = {{Literal(dotnetProbe)}} }
                }
                {{sandbox.LaunchScript(["two words"], "Console")}}
                """);

            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Contains("arg:" + Encode(dll), result.Output, StringComparison.Ordinal);
            Assert.Contains("arg:" + Encode("two words"), result.Output, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("powershell.exe")]
        [InlineData("pwsh")]
        public async Task Startup_scripts_parse_and_configuration_round_trips_on_both_hosts(string host)
        {
            using var sandbox = new StartupSandbox();
            var result = await sandbox.RunAsync($$"""
                foreach ($path in Get-ChildItem -LiteralPath {{Literal(sandbox.AppDirectory)}} -File) {
                    if ($path.Extension -in @('.ps1', '.psm1')) {
                        $tokens = $null
                        $errors = $null
                        [void][Management.Automation.Language.Parser]::ParseFile($path.FullName, [ref]$tokens, [ref]$errors)
                        if ($errors.Count -ne 0) { throw ($errors | Out-String) }
                    }
                }
                $context = Get-KoLiteStartupContext
                $configuration = New-KoLiteStartupConfiguration -AppDirectory {{Literal(sandbox.AppDirectory)}} -Context $context
                $decoded = ConvertFrom-KoLiteStartupConfiguration (ConvertTo-KoLiteStartupConfiguration $configuration) $context
                Write-Result @{ directory = $decoded.appDirectory; mode = $decoded.windowMode; count = $decoded.appArguments.Count }
                """, host);

            using var data = ResultJson(result);
            Assert.Equal(sandbox.AppDirectory, data.RootElement.GetProperty("directory").GetString());
            Assert.Equal("Console", data.RootElement.GetProperty("mode").GetString());
            Assert.Equal(0, data.RootElement.GetProperty("count").GetInt32());
        }

        [Theory]
        [InlineData("powershell.exe", "pwsh")]
        [InlineData("pwsh", "powershell.exe")]
        public async Task Existing_tasks_can_be_managed_from_a_different_PowerShell_host(string producer, string consumer)
        {
            using var sandbox = new StartupSandbox();
            var arguments = new[] { "--label=A&B", "--label=<tag>", "--label=O'Reilly" };
            var produced = await sandbox.RunAsync($$"""
                $context = Get-KoLiteStartupContext
                $configuration = New-KoLiteStartupConfiguration -AppDirectory {{Literal(sandbox.AppDirectory)}} -AppArguments @({{Data(arguments)}}) -Context $context
                $task = New-KoLiteStartupTask $configuration $context
                Write-Result @{ actionArguments = $task.Actions[0].Arguments }
                """, producer);
            using var producedData = ResultJson(produced);
            var actionArguments = producedData.RootElement.GetProperty("actionArguments").GetString()!;
            var consumed = await sandbox.RunAsync($$"""
                $context = Get-KoLiteStartupContext
                $configuration = New-KoLiteStartupConfiguration -AppDirectory {{Literal(sandbox.AppDirectory)}} -Context $context
                $task = New-KoLiteStartupTask $configuration $context
                $task.Actions[0].Arguments = {{Literal(actionArguments)}}
                $decoded = Get-KoLiteStartupTaskConfiguration $task $context
                $task.Actions[0].Arguments = $task.Actions[0].Arguments.Replace('-NoProfile', '-NoProfile -NoExit')
                $tamperedRejected = $false
                try { [void](Get-KoLiteStartupTaskConfiguration $task $context) }
                catch { $tamperedRejected = $_.Exception.Message -like '*does not match its configuration*' }
                Write-Result @{
                    directory = $decoded.appDirectory
                    arguments = @($decoded.appArguments)
                    tamperedRejected = $tamperedRejected
                }
                """, consumer);

            using var data = ResultJson(consumed);
            Assert.Equal(sandbox.AppDirectory, data.RootElement.GetProperty("directory").GetString());
            Assert.Equal(arguments, data.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
            Assert.True(data.RootElement.GetProperty("tamperedRejected").GetBoolean());
        }

        [Fact]
        public void Both_distribution_paths_include_every_startup_helper_without_registering()
        {
            var root = FindRepositoryRoot();
            var publish = File.ReadAllText(Path.Combine(root, "scripts", "Publish-KoLiteApp.ps1"));
            var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
            foreach (var file in StartupSandbox.Helpers)
            {
                Assert.Contains(file, publish, StringComparison.Ordinal);
                Assert.Contains(file, release, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("Register-ScheduledTask", publish, StringComparison.Ordinal);
            Assert.DoesNotContain("Register-ScheduledTask", release, StringComparison.Ordinal);
        }

        private static JsonDocument ResultJson(ProcessResult result)
        {
            Assert.True(result.ExitCode == 0, $"PowerShell exited {result.ExitCode}:\n{result.Error}\n{result.Output}");
            var line = result.Output.Split('\n').Last(value => value.StartsWith("RESULT:", StringComparison.Ordinal));
            return JsonDocument.Parse(line["RESULT:".Length..]);
        }

        private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        private static string Data(object value) =>
            $"(ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Encode(JsonSerializer.Serialize(value))}'))))";

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "KoLite.Local.sln")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("Could not find the Kusto Slice Runner repository.");
        }

        private sealed record ProcessResult(int ExitCode, string Output, string Error);

        private sealed class StartupSandbox : IDisposable
        {
            public static readonly string[] Helpers =
            [
                "Start-KoLiteApp.ps1", "KoLite.Startup.psm1", "Register-KoLiteStartup.ps1",
                "Get-KoLiteStartup.ps1", "Unregister-KoLiteStartup.ps1"
            ];

            public StartupSandbox()
            {
                Root = Directory.CreateTempSubdirectory("ko-lite-startup-tests-").FullName;
                AppDirectory = Path.Combine(Root, "app's & published folder");
                LocalAppData = Path.Combine(Root, "user data");
                Directory.CreateDirectory(AppDirectory);
                Directory.CreateDirectory(LocalAppData);
                foreach (var helper in Helpers)
                {
                    File.Copy(Path.Combine(FindRepositoryRoot(), "scripts", helper), Path.Combine(AppDirectory, helper));
                }
                File.WriteAllText(Path.Combine(AppDirectory, "KoLite.LocalApp.exe"), string.Empty);
            }

            public string Root { get; }
            public string AppDirectory { get; }
            public string LocalAppData { get; }
            public string LogDirectory => Path.Combine(LocalAppData, "KoLite", "startup");
            public string ReleaseFile => Path.Combine(Root, "release-probe");
            public string Script(string name) => Literal(Path.Combine(FindRepositoryRoot(), "scripts", name));

            public string LaunchScript(string[] arguments, string mode, bool dryRun = false) => $$"""
                $context = Get-KoLiteStartupContext
                $arguments = @({{Data(arguments)}})
                $configuration = New-KoLiteStartupConfiguration -AppDirectory {{Literal(AppDirectory)}} -AppArguments $arguments -WindowMode {{Literal(mode)}} -Context $context
                $encoded = ConvertTo-KoLiteStartupConfiguration $configuration
                & {{Script("Start-KoLiteApp.ps1")}} -StartupConfiguration $encoded {{(dryRun ? "-DryRun" : "")}}
                exit $LASTEXITCODE
                """;

            public async Task BuildProbeAsync()
            {
                File.Delete(Path.Combine(AppDirectory, "KoLite.LocalApp.exe"));
                var result = await RunAsync($$"""
                    Add-Type -OutputAssembly {{Literal(Path.Combine(AppDirectory, "KoLite.LocalApp.exe"))}} -OutputType ConsoleApplication -TypeDefinition @'
                    using System;
                    using System.IO;
                    using System.Text;
                    using System.Threading;
                    namespace KoLite.StartupProbe
                    {
                        public static class Program
                        {
                            public static int Main(string[] args)
                            {
                                Console.WriteLine("cwd:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(Environment.CurrentDirectory)));
                                foreach (string argument in args)
                                {
                                    Console.WriteLine("arg:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(argument)));
                                }
                                Console.WriteLine("probe-ready");
                                if (Array.IndexOf(args, "--hold") >= 0)
                                {
                                    for (int index = 0; index < 200; index++)
                                    {
                                        if (File.Exists(Environment.GetEnvironmentVariable("KOLITE_TEST_RELEASE"))) break;
                                        Thread.Sleep(50);
                                    }
                                }
                                if (Array.IndexOf(args, "--volume") >= 0)
                                {
                                    for (int index = 0; index < 1300; index++) Console.WriteLine(new string('x', 5000));
                                }
                                if (Array.IndexOf(args, "--fail") >= 0)
                                {
                                    Console.Error.WriteLine("probe-error");
                                    return 23;
                                }
                                Console.WriteLine("probe-finished");
                                return 0;
                            }
                        }
                    }
                    '@
                    """);
                Assert.True(result.ExitCode == 0, result.Error);
            }

            public Process Start(string body, string host = "powershell.exe")
            {
                var scriptPath = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".ps1");
                var script = $$"""
                    Set-StrictMode -Version Latest
                    $ErrorActionPreference = 'Stop'
                    Import-Module {{Literal(Path.Combine(FindRepositoryRoot(), "scripts", "KoLite.Startup.psm1"))}} -Force
                    function Write-Result($value) { Write-Output ('RESULT:' + (ConvertTo-Json -InputObject $value -Depth 8 -Compress)) }
                    {{body}}
                    """;
                File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
                var info = new ProcessStartInfo
                {
                    FileName = host,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Root
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-NonInteractive");
                info.ArgumentList.Add("-File");
                info.ArgumentList.Add(scriptPath);
                info.Environment["LOCALAPPDATA"] = LocalAppData;
                info.Environment["KOLITE_TEST_RELEASE"] = ReleaseFile;
                return Process.Start(info) ?? throw new InvalidOperationException("Could not start the test PowerShell process.");
            }

            public async Task<ProcessResult> RunAsync(string body, string host = "powershell.exe")
            {
                using var process = Start(body, host);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                    return new ProcessResult(process.ExitCode, await output, await error);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
            }

            public void Dispose()
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private const string TaskMocks = """
            Import-Module ScheduledTasks
            $global:StartupTask = $null
            $global:TaskWrites = 0
            $global:FailRegistration = $false
            function global:Get-ScheduledTask {
                [CmdletBinding()]param()
                if ($null -ne $global:StartupTask) { return $global:StartupTask }
            }
            function global:Register-ScheduledTask {
                [CmdletBinding()]param($TaskName, $TaskPath, $InputObject, [switch]$Force)
                if ($global:FailRegistration) { throw 'simulated registration failure' }
                $global:TaskWrites++
                $global:StartupTask = [pscustomobject]@{
                    TaskName = $TaskName
                    TaskPath = $TaskPath
                    Description = $InputObject.Description
                    Actions = @($InputObject.Actions)
                    Principal = $InputObject.Principal
                    Settings = $InputObject.Settings
                    State = 'Ready'
                }
            }
            function global:Unregister-ScheduledTask {
                [CmdletBinding(SupportsShouldProcess)]param($TaskName, $TaskPath)
                if ($TaskName -ne $global:StartupTask.TaskName -or $TaskPath -ne $global:StartupTask.TaskPath) { throw 'Wrong task' }
                $global:TaskWrites++
                $global:StartupTask = $null
            }
            function global:Get-ScheduledTaskInfo {
                [CmdletBinding()]param($TaskName, $TaskPath)
                return [pscustomobject]@{ LastRunTime = [datetime]'2026-01-01'; LastTaskResult = 23 }
            }
            function global:Start-ScheduledTask { throw 'Startup registration must never launch an app.' }
            function global:Stop-ScheduledTask { throw 'Startup management must never stop an app.' }
            """;
    }
}
