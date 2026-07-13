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
            startInfo.ArgumentList.Add("-DryRun");

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start powershell.exe.");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"Start-KoLiteApp.ps1 failed with exit code {process.ExitCode}: {standardError}");
            return standardOutput;
        }
    }
}
