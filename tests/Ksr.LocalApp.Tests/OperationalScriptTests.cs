// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using Ksr.Local.Sqlite.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ksr.LocalApp.Tests
{
    public sealed class OperationalScriptTests
    {
        [Theory]
        [InlineData(null, null)]
        [InlineData(null, "configured.db")]
        [InlineData("preferred.db", "configured.db")]
        public void Database_options_use_ksr_defaults_and_configuration_precedence(string? connectionString, string? databasePath)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:KsrSqlite"] = connectionString,
                    ["Ksr:DatabasePath"] = databasePath
                })
                .Build();
            using var services = new ServiceCollection().AddKsrServices(configuration).BuildServiceProvider();

            var options = services.GetRequiredService<KsrSqliteConnectionOptions>();

            Assert.Equal(
                Path.GetFullPath(connectionString ?? databasePath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ksr", "ksr.db")),
                options.DatabasePath);
        }

        [Fact]
        public void Release_names_and_skill_entrypoints_use_the_public_product_identity()
        {
            var root = FindRepositoryRoot();
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
            foreach (var kind in new[] { "self-contained", "framework-dependent" })
            {
                Assert.Contains($"kusto-slice-runner-$env:RELEASE_VERSION-win-x64-{kind}", workflow, StringComparison.Ordinal);
            }

            Assert.Contains("ksr-release-workflow:$version@$env:GITHUB_SHA", workflow, StringComparison.Ordinal);
            Assert.Contains("KsrGitCommitSha", workflow, StringComparison.Ordinal);
            foreach (var name in new[] { "job-manager", "gap-repair", "schedule-json", "release-highlights" })
            {
                var skill = File.ReadAllText(Path.Combine(root, ".github", "skills", $"ksr-{name}", "SKILL.md"));
                Assert.Contains($"name: ksr-{name}", skill, StringComparison.Ordinal);
            }
            foreach (var script in new[] { "Start-KsrApp.ps1", "Stop-KsrApp.ps1", "Ksr.Startup.psm1", "Register-KsrStartup.ps1", "Get-KsrStartup.ps1", "Unregister-KsrStartup.ps1" })
            {
                Assert.True(File.Exists(Path.Combine(root, "scripts", script)));
                Assert.Contains(script, workflow, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Public_repository_documents_and_license_metadata_are_consistent()
        {
            var root = FindRepositoryRoot();
            foreach (var name in new[] { "LICENSE.txt", "NOTICE", "SECURITY.md", "CODE_OF_CONDUCT.md", "CONTRIBUTING.md", "SUPPORT.md" })
            {
                Assert.True(File.Exists(Path.Combine(root, name)), $"Missing {name}");
            }

            Assert.Contains("MIT License", File.ReadAllText(Path.Combine(root, "LICENSE.txt")), StringComparison.Ordinal);
            Assert.Contains("\"license\": \"MIT\"", File.ReadAllText(Path.Combine(root, "package.json")), StringComparison.Ordinal);
            Assert.Contains("Microsoft's Trademark & Brand Guidelines", File.ReadAllText(Path.Combine(root, "README.md")), StringComparison.Ordinal);
            Assert.Contains("cla.opensource.microsoft.com", File.ReadAllText(Path.Combine(root, "CONTRIBUTING.md")), StringComparison.Ordinal);
            Assert.Contains("https://aka.ms/SECURITY.md", File.ReadAllText(Path.Combine(root, "SECURITY.md")), StringComparison.Ordinal);
        }

        [Fact]
        public void Release_packaging_includes_license_and_notices()
        {
            var root = FindRepositoryRoot();
            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
            var project = File.ReadAllText(Path.Combine(root, "src", "Ksr.LocalApp", "Ksr.LocalApp.csproj"));
            foreach (var name in new[] { "LICENSE.txt", "NOTICE", "THIRD-PARTY-NOTICES.md" })
            {
                Assert.Contains(name, workflow, StringComparison.Ordinal);
                Assert.Contains($"Link=\"{name}\" CopyToPublishDirectory=\"PreserveNewest\"", project, StringComparison.Ordinal);
            }
        }

        [Theory]
        [MemberData(nameof(OperationalScripts))]
        public void Operational_scripts_default_to_dry_run_style_and_safe_local_flags(string path)
        {
            var script = File.ReadAllText(path);

            Assert.Contains("Set-StrictMode -Version Latest", script, StringComparison.Ordinal);
            Assert.Contains("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Start-Service", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stop-Service", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("New-Service", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Invoke-Az", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Screenshot_capture_dry_run_does_not_create_its_database_or_start_an_app()
        {
            var output = RunPowerShell(
                Path.Combine(FindRepositoryRoot(), "scripts", "Capture-DocumentationScreenshots.ps1"),
                "-DryRun");

            Assert.Contains("DryRun: no files, processes, HTTP requests, or external service calls.", output, StringComparison.Ordinal);
            Assert.Contains("http://127.0.0.1:5107", output, StringComparison.Ordinal);
            var databaseLine = output.Split('\n').Single(line => line.StartsWith("Fresh fixture database:", StringComparison.Ordinal));
            var databasePath = databaseLine["Fresh fixture database:".Length..].Trim();
            Assert.False(File.Exists(databasePath));
            Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
        }

        [Fact]
        public void Stop_script_defaults_to_dry_run_and_uses_graceful_drain_endpoint()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Stop-KsrApp.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("if ($DryRun)", script, StringComparison.Ordinal);
            Assert.Contains("DryRun: no HTTP request is sent.", script, StringComparison.Ordinal);
            Assert.Contains("/control/v1/shutdown/drain", script, StringComparison.Ordinal);
            Assert.Contains("ContentType 'application/json'", script, StringComparison.Ordinal);
            Assert.Contains("Invoke-RestMethod -Method Post", script, StringComparison.Ordinal);
            Assert.Contains("Invoke-WebRequest -Method Get", script, StringComparison.Ordinal);
            Assert.Contains("-SkipHttpErrorCheck", script, StringComparison.Ordinal);
        }

        [Fact]
        public void Job_manager_helper_uses_v1_etags_problem_details_and_cursor_continuation()
        {
            var script = File.ReadAllText(Path.Combine(
                FindRepositoryRoot(),
                ".github",
                "skills",
                "ksr-job-manager",
                "scripts",
                "Invoke-KsrJobApi.ps1"));

            Assert.Contains("$apiRoot = '/api/v1'", script, StringComparison.Ordinal);
            Assert.Contains("'If-Match' = $current.ETag", script, StringComparison.Ordinal);
            Assert.Contains("supportedApiVersions", script, StringComparison.Ordinal);
            Assert.Contains("($code)", script, StringComparison.Ordinal);
            Assert.Contains("nextCursor", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$AllPages", script, StringComparison.Ordinal);
            Assert.Contains("@{ schedules = $schedules }", script, StringComparison.Ordinal);
            Assert.Contains("repair-previews", script, StringComparison.Ordinal);
            Assert.Contains("Repair accepted at $($result.Location)", script, StringComparison.Ordinal);
            Assert.DoesNotContain("/api/jobs", script, StringComparison.Ordinal);
            Assert.DoesNotContain("/status/health", script, StringComparison.Ordinal);
        }

        [Fact]
        public void Gap_repair_skill_has_frontmatter_and_existing_workflow_references()
        {
            var root = FindRepositoryRoot();
            var skill = File.ReadAllText(Path.Combine(root, ".github", "skills", "ksr-gap-repair", "SKILL.md"));

            Assert.Contains("name: ksr-gap-repair", skill, StringComparison.Ordinal);
            Assert.Contains("description:", skill, StringComparison.Ordinal);
            Assert.Contains("../ksr-job-manager/SKILL.md", skill, StringComparison.Ordinal);
            Assert.Contains(@"scripts\Invoke-KsrJobApi.ps1", skill, StringComparison.Ordinal);
            Assert.Contains("../../../docs/local-api.md", skill, StringComparison.Ordinal);
            Assert.Contains("../../../docs/schedule-json.md", skill, StringComparison.Ordinal);
            Assert.Contains("../../../docs/operations-runbook.md", skill, StringComparison.Ordinal);

            foreach (var path in new[]
            {
                Path.Combine(root, "README.md"),
                Path.Combine(root, "docs", "operations-runbook.md"),
                Path.Combine(root, ".github", "skills", "ksr-job-manager", "SKILL.md")
            })
            {
                Assert.Contains("ksr-gap-repair", File.ReadAllText(path), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Shipped_agent_tools_and_instructions_do_not_call_legacy_routes()
        {
            var root = FindRepositoryRoot();
            var paths = new[]
            {
                Path.Combine(root, ".github", "copilot-instructions.md"),
                Path.Combine(root, ".github", "agents", "ksr-maintainer.agent.md"),
                Path.Combine(root, ".github", "skills", "ksr-gap-repair", "SKILL.md"),
                Path.Combine(root, ".github", "skills", "ksr-job-manager", "SKILL.md"),
                Path.Combine(root, ".github", "skills", "ksr-job-manager", "scripts", "Invoke-KsrJobApi.ps1"),
                Path.Combine(root, ".github", "skills", "ksr-schedule-json", "SKILL.md"),
                Path.Combine(root, ".github", "workflows", "release.yml"),
                Path.Combine(root, "scripts", "Get-KsrDatabase.ps1"),
                Path.Combine(root, "scripts", "Invoke-KsrVacuum.ps1"),
                Path.Combine(root, "scripts", "Publish-KsrApp.ps1"),
                Path.Combine(root, "scripts", "Start-KsrUi.ps1"),
                Path.Combine(root, "scripts", "Stop-KsrApp.ps1")
            };

            foreach (var path in paths)
            {
                var content = File.ReadAllText(path);
                Assert.DoesNotContain("/api/jobs", content, StringComparison.Ordinal);
                Assert.DoesNotContain("/api/diagnostics", content, StringComparison.Ordinal);
                Assert.DoesNotContain("/status/health", content, StringComparison.Ordinal);
                Assert.DoesNotContain("/status/shutdown", content, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Publish_script_publishes_copies_helper_scripts_and_prints_deployed_path()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Publish-KsrApp.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("if ($DryRun)", script, StringComparison.Ordinal);
            Assert.Contains("dotnet publish", script, StringComparison.Ordinal);
            Assert.Contains("Ksr.LocalApp.csproj", script, StringComparison.Ordinal);
            Assert.Contains("Copy-Item", script, StringComparison.Ordinal);
            Assert.Contains("Stop-KsrApp.ps1", script, StringComparison.Ordinal);
            Assert.Contains("Start-KsrApp.ps1", script, StringComparison.Ordinal);
            Assert.Contains("Deployed path:", script, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(404, """{"code":"not-found","detail":"The v1 API is unavailable."}""")]
        [InlineData(200, "{}")]
        public async Task Operational_scripts_abort_when_an_incompatible_service_responds(int statusCode, string body)
        {
            await using var server = new IncompatibleHttpServer(statusCode, body);
            var root = FindRepositoryRoot();
            var temporaryOutput = Path.Combine(Path.GetTempPath(), "ksr-publish-tests", Guid.NewGuid().ToString("N"));
            try
            {
                var stop = await RunPowerShellAsync(
                    Path.Combine(root, "scripts", "Stop-KsrApp.ps1"),
                    "-BaseUrl", server.BaseUrl,
                    "-Confirm:$false");
                Assert.NotEqual(0, stop.ExitCode);
                Assert.Contains("version shipped with that app", stop.Error, StringComparison.Ordinal);
                Assert.Equal(0, server.PostCount);

                var vacuum = await RunPowerShellAsync(
                    Path.Combine(root, "scripts", "Invoke-KsrVacuum.ps1"),
                    "-BaseUrl", server.BaseUrl,
                    "-DatabasePath", Path.Combine(temporaryOutput, "missing.db"),
                    "-DryRun");
                Assert.NotEqual(0, vacuum.ExitCode);
                Assert.Contains("version shipped with that app", vacuum.Error, StringComparison.Ordinal);

                var publish = await RunPowerShellAsync(
                    Path.Combine(root, "scripts", "Publish-KsrApp.ps1"),
                    "-BaseUrl", server.BaseUrl,
                    "-OutputDirectory", temporaryOutput);
                Assert.NotEqual(0, publish.ExitCode);
                Assert.Contains("version shipped with that app", publish.Error, StringComparison.Ordinal);
                Assert.False(Directory.Exists(temporaryOutput));
            }
            finally
            {
                if (Directory.Exists(temporaryOutput))
                {
                    Directory.Delete(temporaryOutput, recursive: true);
                }
            }
        }

        [Fact]
        public void Start_script_prefers_published_executable_and_falls_back_to_dotnet()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-KsrApp.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("if ($DryRun)", script, StringComparison.Ordinal);
            Assert.Contains("Ksr.LocalApp.exe", script, StringComparison.Ordinal);
            Assert.Contains("Ksr.LocalApp.dll", script, StringComparison.Ordinal);
            Assert.Contains("$PSScriptRoot", script, StringComparison.Ordinal);
            Assert.Contains("run-app", script, StringComparison.Ordinal);
            Assert.Contains("Push-Location -LiteralPath $AppDirectory", script, StringComparison.Ordinal);
            Assert.Contains("Pop-Location", script, StringComparison.Ordinal);
            Assert.Contains("& $exePath", script, StringComparison.Ordinal);
            Assert.Contains("& dotnet", script, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Database_discovery_accepts_status_before_and_after_counter_retirement(bool includeRetiredCounter)
        {
            var packageDirectory = CreatePackageDirectory();
            try
            {
                var databasePath = Path.Combine(packageDirectory, "compat.db");
                File.WriteAllText(databasePath, string.Empty);
                var retention = new Dictionary<string, int> { ["queueRowsDeleted"] = 1 };
                if (includeRetiredCounter)
                {
                    retention["ingestionThrottlesDeleted"] = 2;
                }
                var body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    supportedApiVersions = new[] { "v1" },
                    database = new { path = databasePath },
                    retention
                });
                await using var server = new IncompatibleHttpServer(200, body);

                var result = await RunPowerShellAsync(
                    Path.Combine(FindRepositoryRoot(), "scripts", "Get-KsrDatabase.ps1"),
                    "-BaseUrl", server.BaseUrl,
                    "-DatabaseRoot", packageDirectory);

                Assert.True(result.ExitCode == 0, result.Error);
                Assert.Contains("compat.db", result.Output, StringComparison.Ordinal);
                Assert.Equal(0, server.PostCount);
                Assert.Equal(string.Empty, File.ReadAllText(databasePath));
            }
            finally
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }

        [Fact]
        public void Start_script_dry_run_prefers_self_contained_executable()
        {
            var packageDirectory = CreatePackageDirectory();
            try
            {
                File.WriteAllText(Path.Combine(packageDirectory, "Ksr.LocalApp.exe"), string.Empty);
                File.WriteAllText(Path.Combine(packageDirectory, "Ksr.LocalApp.dll"), string.Empty);

                var output = RunStartScriptDryRun(packageDirectory);

                Assert.Contains(Path.Combine(packageDirectory, "Ksr.LocalApp.exe"), output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain($"dotnet \"{Path.Combine(packageDirectory, "Ksr.LocalApp.dll")}\"", output, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }

        [Fact]
        public void Start_script_dry_run_uses_dotnet_for_dll_only_package()
        {
            var packageDirectory = CreatePackageDirectory();
            try
            {
                File.WriteAllText(Path.Combine(packageDirectory, "Ksr.LocalApp.dll"), string.Empty);

                var output = RunStartScriptDryRun(packageDirectory);

                Assert.Contains($"dotnet \"{Path.Combine(packageDirectory, "Ksr.LocalApp.dll")}\"", output, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }

        [Fact]
        public void Start_script_runs_entrypoint_from_app_directory()
        {
            var packageDirectory = CreatePackageDirectory();
            try
            {
                File.WriteAllText(Path.Combine(packageDirectory, "Ksr.LocalApp.dll"), string.Empty);
                File.WriteAllText(
                    Path.Combine(packageDirectory, "dotnet.cmd"),
                    "@echo off\r\necho %CD%\r\n");

                var output = RunStartScript(packageDirectory, dryRun: false, pathPrefix: packageDirectory);

                Assert.Contains(packageDirectory, output, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(packageDirectory, recursive: true);
            }
        }

        [Fact]
        public void Release_highlights_script_limits_github_writes_to_approved_draft_notes()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "New-KsrReleaseHighlights.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$SelfTest", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$Force", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$PrepareUpdate", script, StringComparison.Ordinal);
            Assert.Contains("[string]$ApplyUpdatePlan", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$ConfirmDraftEdit", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$AllowOverwriteChanges", script, StringComparison.Ordinal);
            Assert.Contains("--no-custom-instructions", script, StringComparison.Ordinal);
            Assert.Contains("--disable-builtin-mcps", script, StringComparison.Ordinal);
            Assert.Contains("--available-tools=", script, StringComparison.Ordinal);
            Assert.Contains("--no-remote", script, StringComparison.Ordinal);
            Assert.Contains("Assert-Highlights", script, StringComparison.Ordinal);
            Assert.Contains("Get-TextSha256", script, StringComparison.Ordinal);
            Assert.Contains("Get-ReleaseTagSha", script, StringComparison.Ordinal);
            Assert.Contains("$targetCommitish = $release.MarkerSha", script, StringComparison.Ordinal);
            Assert.Contains("changed immediately before editing", script, StringComparison.Ordinal);
            Assert.Contains("ksr-release-workflow:", script, StringComparison.Ordinal);
            Assert.Contains("'release', 'edit'", script, StringComparison.Ordinal);
            Assert.Contains("--notes-file", script, StringComparison.Ordinal);
            Assert.DoesNotContain("gh auth token", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workflow run", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("release delete", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("release publish", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("'release', 'create'", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("'release', 'upload'", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("git tag", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--allow-all", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--allow-tool", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secrets.", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Release_highlights_script_self_tests_pass()
        {
            var result = RunPowerShell(
                Path.Combine(FindRepositoryRoot(), "scripts", "New-KsrReleaseHighlights.ps1"),
                "-SelfTest");

            Assert.Contains("self-tests passed", result, StringComparison.OrdinalIgnoreCase);
        }

        public static IEnumerable<object[]> OperationalScripts()
        {
            var scriptsDirectory = Path.Combine(FindRepositoryRoot(), "scripts");
            return Directory.EnumerateFiles(scriptsDirectory, "*.ps1")
                .Order(StringComparer.Ordinal)
                .Select(path => new object[] { path });
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Ksr.Local.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not find ksr repository root.");
        }

        private static string CreatePackageDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "ksr-start-script-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string RunStartScriptDryRun(string packageDirectory)
        {
            return RunStartScript(packageDirectory, dryRun: true);
        }

        private static string RunStartScript(string packageDirectory, bool dryRun, string? pathPrefix = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "scripts", "Start-KsrApp.ps1"));
            startInfo.ArgumentList.Add("-AppDirectory");
            startInfo.ArgumentList.Add(packageDirectory);
            if (dryRun)
            {
                startInfo.ArgumentList.Add("-DryRun");
            }
            if (pathPrefix is not null)
            {
                startInfo.Environment["PATH"] = $"{pathPrefix};{startInfo.Environment["PATH"]}";
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start powershell.exe.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"Start-KsrApp.ps1 failed with exit code {process.ExitCode}: {standardError}");
            return standardOutput;
        }

        private static string RunPowerShell(string scriptPath, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start pwsh.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"{Path.GetFileName(scriptPath)} failed with exit code {process.ExitCode}: {standardError}");
            return standardOutput;
        }

        private static async Task<ProcessResult> RunPowerShellAsync(string scriptPath, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start pwsh.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }

        private sealed record ProcessResult(int ExitCode, string Output, string Error);

        private sealed class IncompatibleHttpServer : IAsyncDisposable
        {
            private readonly System.Net.Sockets.TcpListener listener =
                new(System.Net.IPAddress.Loopback, 0);
            private readonly CancellationTokenSource cancellation = new();
            private readonly Task loop;
            private readonly int statusCode;
            private readonly string body;
            private int postCount;

            public IncompatibleHttpServer(int statusCode, string body)
            {
                this.statusCode = statusCode;
                this.body = body;
                listener.Start();
                var endpoint = (System.Net.IPEndPoint)listener.LocalEndpoint;
                BaseUrl = $"http://127.0.0.1:{endpoint.Port}";
                loop = AcceptLoop();
            }

            public string BaseUrl { get; }
            public int PostCount => Volatile.Read(ref postCount);

            public async ValueTask DisposeAsync()
            {
                cancellation.Cancel();
                listener.Stop();
                try
                {
                    await loop;
                }
                catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException)
                {
                }

                cancellation.Dispose();
            }

            private async Task AcceptLoop()
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    _ = Task.Run(() => Respond(client), cancellation.Token);
                }
            }

            private async Task Respond(System.Net.Sockets.TcpClient client)
            {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, leaveOpen: true))
                {
                    var requestLine = await reader.ReadLineAsync();
                    if (requestLine?.StartsWith("POST ", StringComparison.Ordinal) == true)
                    {
                        Interlocked.Increment(ref postCount);
                    }

                    string? line;
                    do
                    {
                        line = await reader.ReadLineAsync();
                    }
                    while (!string.IsNullOrEmpty(line));

                    var bodyBytes = System.Text.Encoding.UTF8.GetBytes(body);
                    var reason = statusCode == 200 ? "OK" : "Not Found";
                    var headers = System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers);
                    await stream.WriteAsync(bodyBytes);
                }
            }
        }
    }
}
