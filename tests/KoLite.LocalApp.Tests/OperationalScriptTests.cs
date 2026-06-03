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
    }
}
