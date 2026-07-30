using System.Diagnostics;

namespace KoLite.LocalApp.Tests
{
    public sealed class OperationalScriptTests
    {
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
        public void Stop_script_defaults_to_dry_run_and_uses_graceful_drain_endpoint()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Stop-KoLiteApp.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("if ($DryRun)", script, StringComparison.Ordinal);
            Assert.Contains("DryRun: no HTTP request is sent.", script, StringComparison.Ordinal);
            Assert.Contains("/status/shutdown/drain?reason=", script, StringComparison.Ordinal);
            Assert.Contains("Invoke-RestMethod -Method Post", script, StringComparison.Ordinal);
            Assert.Contains("Invoke-RestMethod -Method Get", script, StringComparison.Ordinal);
        }

        [Fact]
        public void Publish_script_publishes_copies_helper_scripts_and_prints_deployed_path()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Publish-KoLiteApp.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("if ($DryRun)", script, StringComparison.Ordinal);
            Assert.Contains("dotnet publish", script, StringComparison.Ordinal);
            Assert.Contains("KoLite.LocalApp.csproj", script, StringComparison.Ordinal);
            Assert.Contains("Copy-Item", script, StringComparison.Ordinal);
            Assert.Contains("Stop-KoLiteApp.ps1", script, StringComparison.Ordinal);
            Assert.Contains("Start-KoLiteApp.ps1", script, StringComparison.Ordinal);
            Assert.Contains("Deployed path:", script, StringComparison.Ordinal);
        }

        [Fact]
        public void Start_script_prefers_published_executable_and_falls_back_to_dotnet()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Start-KoLiteApp.ps1"));

            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("if ($DryRun)", script, StringComparison.Ordinal);
            Assert.Contains("KoLite.LocalApp.exe", script, StringComparison.Ordinal);
            Assert.Contains("KoLite.LocalApp.dll", script, StringComparison.Ordinal);
            Assert.Contains("$PSScriptRoot", script, StringComparison.Ordinal);
            Assert.Contains("run-app", script, StringComparison.Ordinal);
            Assert.Contains("Push-Location -LiteralPath $AppDirectory", script, StringComparison.Ordinal);
            Assert.Contains("Pop-Location", script, StringComparison.Ordinal);
            Assert.Contains("& $exePath", script, StringComparison.Ordinal);
            Assert.Contains("& dotnet", script, StringComparison.Ordinal);
        }

        [Fact]
        public void Start_script_dry_run_prefers_self_contained_executable()
        {
            var packageDirectory = CreatePackageDirectory();
            try
            {
                File.WriteAllText(Path.Combine(packageDirectory, "KoLite.LocalApp.exe"), string.Empty);
                File.WriteAllText(Path.Combine(packageDirectory, "KoLite.LocalApp.dll"), string.Empty);

                var output = RunStartScriptDryRun(packageDirectory);

                Assert.Contains(Path.Combine(packageDirectory, "KoLite.LocalApp.exe"), output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain($"dotnet \"{Path.Combine(packageDirectory, "KoLite.LocalApp.dll")}\"", output, StringComparison.OrdinalIgnoreCase);
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
                File.WriteAllText(Path.Combine(packageDirectory, "KoLite.LocalApp.dll"), string.Empty);

                var output = RunStartScriptDryRun(packageDirectory);

                Assert.Contains($"dotnet \"{Path.Combine(packageDirectory, "KoLite.LocalApp.dll")}\"", output, StringComparison.OrdinalIgnoreCase);
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
                File.WriteAllText(Path.Combine(packageDirectory, "KoLite.LocalApp.dll"), string.Empty);
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
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "New-KoLiteReleaseHighlights.ps1"));

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
            Assert.Contains("ko-lite-release-workflow:", script, StringComparison.Ordinal);
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
                Path.Combine(FindRepositoryRoot(), "scripts", "New-KoLiteReleaseHighlights.ps1"),
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
                if (File.Exists(Path.Combine(current.FullName, "KoLite.Local.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("Could not find ko-lite repository root.");
        }

        private static string CreatePackageDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "ko-lite-start-script-tests", Guid.NewGuid().ToString("N"));
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
            startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "scripts", "Start-KoLiteApp.ps1"));
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

            Assert.True(process.ExitCode == 0, $"Start-KoLiteApp.ps1 failed with exit code {process.ExitCode}: {standardError}");
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
    }
}
