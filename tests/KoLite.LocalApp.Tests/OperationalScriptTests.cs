namespace KoLite.LocalApp.Tests
{
    public sealed class OperationalScriptTests
    {
        [Theory]
        [InlineData("Install-KoLiteLocalService.ps1")]
        [InlineData("Inspect-KoLiteCrashRecovery.ps1")]
        [InlineData("Test-KoLiteLocalDiagnostics.ps1")]
        public void Operational_scripts_default_to_dry_run_style_and_safe_local_flags(string fileName)
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", fileName));

            Assert.Contains("Set-StrictMode -Version Latest", script, StringComparison.Ordinal);
            Assert.Contains("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);
            Assert.Contains("[switch]$DryRun", script, StringComparison.Ordinal);
            Assert.Contains("$DatabasePath", script, StringComparison.Ordinal);
            Assert.Contains("LiveKustoExecution: Enabled", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Start-Service", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Stop-Service", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("New-Service", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Invoke-Az", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Service_script_requires_apply_before_sc_commands_are_executed()
        {
            var script = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "Install-KoLiteLocalService.ps1"));

            Assert.Contains("if ($DryRun -or -not $Apply)", script, StringComparison.Ordinal);
            Assert.Contains("no service is installed, removed, started, or stopped", script, StringComparison.Ordinal);
            Assert.Contains("--KoLite:Kusto:AuthMode=$KustoAuthMode", script, StringComparison.Ordinal);
            Assert.DoesNotContain("--KoLite:ExecutionMode=Fake", script, StringComparison.Ordinal);
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
