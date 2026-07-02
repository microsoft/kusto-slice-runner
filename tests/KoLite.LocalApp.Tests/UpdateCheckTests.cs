using System.Text.Json;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.LocalApp.Updates;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class UpdateCheckTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "localapp-update-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;

        public UpdateCheckTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "update.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
        }

        [Fact]
        public void Options_default_to_enabled_hourly_check_of_the_default_repository()
        {
            var configuration = new ConfigurationBuilder().Build();
            var options = LocalUpdateCheckOptions.From(configuration);

            Assert.True(options.Enabled);
            Assert.Equal(TimeSpan.FromHours(1), options.Interval);
            Assert.Equal("microsoft/kusto-slice-runner", options.Repository);
            Assert.Equal("main", options.Branch);
        }

        [Fact]
        public void Options_honor_explicit_overrides()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:UpdateCheck:Enabled"] = "false",
                    ["KoLite:UpdateCheck:Interval"] = "00:15:00",
                    ["KoLite:UpdateCheck:Repository"] = "azure-core/other",
                    ["KoLite:UpdateCheck:Branch"] = "release"
                })
                .Build();
            var options = LocalUpdateCheckOptions.From(configuration);

            Assert.False(options.Enabled);
            Assert.Equal(TimeSpan.FromMinutes(15), options.Interval);
            Assert.Equal("azure-core/other", options.Repository);
            Assert.Equal("release", options.Branch);
        }

        [Fact]
        public void Options_reject_non_positive_interval()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["KoLite:UpdateCheck:Interval"] = "00:00:00"
                })
                .Build();

            Assert.Throws<InvalidOperationException>(() => LocalUpdateCheckOptions.From(configuration));
        }

        [Fact]
        public void Initial_snapshot_is_checking_when_enabled_with_a_build_sha()
        {
            var snapshot = UpdateCheckSnapshot.Initial(enabled: true, builtSha: "abc123");

            Assert.Equal(UpdateCheckStatus.Checking, snapshot.Status);
            Assert.Equal(UpdateCheckUnavailableReason.None, snapshot.Reason);
        }

        [Fact]
        public void Initial_snapshot_is_disabled_when_update_checks_are_off()
        {
            var snapshot = UpdateCheckSnapshot.Initial(enabled: false, builtSha: "abc123");

            Assert.Equal(UpdateCheckStatus.Unavailable, snapshot.Status);
            Assert.Equal(UpdateCheckUnavailableReason.Disabled, snapshot.Reason);
        }

        [Fact]
        public void Initial_snapshot_is_unavailable_without_a_build_sha()
        {
            var snapshot = UpdateCheckSnapshot.Initial(enabled: true, builtSha: null);

            Assert.Equal(UpdateCheckStatus.Unavailable, snapshot.Status);
            Assert.Equal(UpdateCheckUnavailableReason.NoBuildSha, snapshot.Reason);
        }

        [Fact]
        public void Badge_for_update_available_links_to_the_compare_view_and_shows_commits_behind()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.UpdateAvailable,
                UpdateCheckUnavailableReason.None,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: "2222222222222222222222222222222222222222",
                CommitsBehind: 3,
                CommitsAhead: null,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: null);
            var badge = BuildBadge(snapshot, DateTimeOffset.Parse("2026-06-05T16:01:00Z"));

            Assert.Equal("update", badge.StatusKey);
            Assert.Contains("3 behind", badge.Label);
            Assert.True(badge.ShowLink);
            Assert.Contains("/compare/1111111111111111111111111111111111111111...main", badge.LinkUrl);
            Assert.False(badge.HasRemediation);
        }

        [Fact]
        public void Badge_for_unavailable_state_includes_targeted_remediation()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.Unavailable,
                UpdateCheckUnavailableReason.GhNotAuthenticated,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: null,
                CommitsBehind: null,
                CommitsAhead: null,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: "gh: not logged in");
            var badge = BuildBadge(snapshot, DateTimeOffset.Parse("2026-06-05T16:00:30Z"));

            Assert.Equal("unavailable", badge.StatusKey);
            Assert.True(badge.HasRemediation);
            Assert.Contains(badge.RemediationSteps, step => step.Contains("gh auth login", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("ahead", 5, 0, RepositoryComparison.RemoteAhead, 5, 0)]
        [InlineData("behind", 0, 3, RepositoryComparison.LocalAhead, 0, 3)]
        [InlineData("diverged", 2, 4, RepositoryComparison.Diverged, 2, 4)]
        [InlineData("identical", 0, 0, RepositoryComparison.Identical, 0, 0)]
        public void MapComparison_translates_github_compare_status(
            string status,
            int aheadBy,
            int behindBy,
            RepositoryComparison expectedComparison,
            int expectedBehind,
            int expectedAhead)
        {
            var (comparison, commitsBehind, commitsAhead) = GhCliRepositoryUpdateChecker.MapComparison(status, aheadBy, behindBy);

            Assert.Equal(expectedComparison, comparison);
            Assert.Equal(expectedBehind, commitsBehind);
            Assert.Equal(expectedAhead, commitsAhead);
        }

        [Fact]
        public void MapComparison_returns_unknown_for_unrecognized_status()
        {
            var (comparison, commitsBehind, commitsAhead) = GhCliRepositoryUpdateChecker.MapComparison("mystery", 1, 1);

            Assert.Equal(RepositoryComparison.Unknown, comparison);
            Assert.Null(commitsBehind);
            Assert.Null(commitsAhead);
        }

        [Fact]
        public void Badge_for_ahead_state_has_no_remediation_or_external_link()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.Ahead,
                UpdateCheckUnavailableReason.None,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: "2222222222222222222222222222222222222222",
                CommitsBehind: null,
                CommitsAhead: 2,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: null);
            var badge = BuildBadge(snapshot, DateTimeOffset.Parse("2026-06-05T16:00:30Z"));

            Assert.Equal("ahead", badge.StatusKey);
            Assert.Contains("Ahead of published", badge.Label);
            Assert.Contains("2 ahead", badge.Label);
            Assert.False(badge.ShowLink);
            Assert.False(badge.HasRemediation);
        }

        [Fact]
        public void Badge_for_ahead_state_notes_an_unpushed_build_when_counts_are_unknown()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.Ahead,
                UpdateCheckUnavailableReason.None,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: "2222222222222222222222222222222222222222",
                CommitsBehind: null,
                CommitsAhead: null,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: null);
            var badge = BuildBadge(snapshot, DateTimeOffset.Parse("2026-06-05T16:00:30Z"));

            Assert.Equal("ahead", badge.StatusKey);
            Assert.Contains(badge.DetailLines, line => line.Contains("unpushed", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Badge_for_diverged_state_shows_both_counts()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.Diverged,
                UpdateCheckUnavailableReason.None,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: "2222222222222222222222222222222222222222",
                CommitsBehind: 1,
                CommitsAhead: 2,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: null);
            var badge = BuildBadge(snapshot, DateTimeOffset.Parse("2026-06-05T16:00:30Z"));

            Assert.Equal("diverged", badge.StatusKey);
            Assert.Contains("2 ahead / 1 behind", badge.Label);
            Assert.False(badge.HasRemediation);
        }

        [Fact]
        public async Task Background_service_flags_ahead_when_the_build_is_ahead_of_the_remote()
        {
            var fake = new FakeRepositoryUpdateChecker(
                RepositoryUpdateCheckResult.Success("remote-sha", RepositoryComparison.LocalAhead, commitsBehind: 0, commitsAhead: 2));
            using var factory = CreateFactory(enableUpdateCheck: true, builtSha: "build-sha", configureServices: services =>
            {
                services.RemoveAll<IRepositoryUpdateChecker>();
                services.AddSingleton<IRepositoryUpdateChecker>(fake);
            });
            using var client = factory.CreateClient();

            var status = await WaitForUpdateStatusAsync(client, "Ahead");

            Assert.Equal("Ahead", status.GetProperty("status").GetString());
            Assert.Equal(2, status.GetProperty("commitsAhead").GetInt32());
        }

        [Fact]
        public async Task Background_service_flags_diverged_when_both_sides_have_unique_commits()
        {
            var fake = new FakeRepositoryUpdateChecker(
                RepositoryUpdateCheckResult.Success("remote-sha", RepositoryComparison.Diverged, commitsBehind: 3, commitsAhead: 1));
            using var factory = CreateFactory(enableUpdateCheck: true, builtSha: "build-sha", configureServices: services =>
            {
                services.RemoveAll<IRepositoryUpdateChecker>();
                services.AddSingleton<IRepositoryUpdateChecker>(fake);
            });
            using var client = factory.CreateClient();

            var status = await WaitForUpdateStatusAsync(client, "Diverged");

            Assert.Equal("Diverged", status.GetProperty("status").GetString());
            Assert.Equal(3, status.GetProperty("commitsBehind").GetInt32());
            Assert.Equal(1, status.GetProperty("commitsAhead").GetInt32());
        }

        [Fact]
        public async Task Top_bar_badge_renders_update_available_state_from_seeded_runtime_state()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.UpdateAvailable,
                UpdateCheckUnavailableReason.None,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: "2222222222222222222222222222222222222222",
                CommitsBehind: 2,
                CommitsAhead: null,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: null);

            using var factory = CreateFactory(enableUpdateCheck: false, configureServices: services =>
            {
                services.RemoveAll<UpdateCheckRuntimeState>();
                services.AddSingleton(new UpdateCheckRuntimeState(snapshot));
            });
            using var client = factory.CreateClient();

            var html = await client.GetStringAsync("/");

            Assert.Contains("update-badge-update", html);
            Assert.Contains("Update available", html);
            Assert.Contains("View changes on GitHub", html);
        }

        [Fact]
        public async Task Top_bar_badge_renders_remediation_for_unavailable_state()
        {
            var snapshot = new UpdateCheckSnapshot(
                UpdateCheckStatus.Unavailable,
                UpdateCheckUnavailableReason.GhMissing,
                BuiltSha: "1111111111111111111111111111111111111111",
                RemoteSha: null,
                CommitsBehind: null,
                CommitsAhead: null,
                LastCheckedUtc: DateTimeOffset.Parse("2026-06-05T16:00:00Z"),
                ErrorMessage: null);

            using var factory = CreateFactory(enableUpdateCheck: false, configureServices: services =>
            {
                services.RemoveAll<UpdateCheckRuntimeState>();
                services.AddSingleton(new UpdateCheckRuntimeState(snapshot));
            });
            using var client = factory.CreateClient();

            var html = await client.GetStringAsync("/");

            Assert.Contains("update-badge-unavailable", html);
            Assert.Contains("Update checks unavailable", html);
            Assert.Contains("Install the GitHub CLI", html);
        }

        [Fact]
        public async Task Health_reports_update_check_block_with_deterministic_defaults()
        {
            using var factory = CreateFactory(enableUpdateCheck: false);
            using var client = factory.CreateClient();

            var health = await client.GetStringAsync("/status/health");
            using var json = JsonDocument.Parse(health);
            var updateCheck = json.RootElement.GetProperty("updateCheck");

            Assert.Equal("Unavailable", updateCheck.GetProperty("status").GetString());
            Assert.Equal("Disabled", updateCheck.GetProperty("reason").GetString());
            Assert.False(updateCheck.GetProperty("enabled").GetBoolean());
            Assert.Equal("microsoft/kusto-slice-runner", updateCheck.GetProperty("repository").GetString());
            Assert.Equal("main", updateCheck.GetProperty("branch").GetString());
        }

        [Fact]
        public async Task Background_service_flags_update_available_when_remote_is_ahead_of_the_build()
        {
            var fake = new FakeRepositoryUpdateChecker(RepositoryUpdateCheckResult.Success("remote-sha", RepositoryComparison.RemoteAhead, commitsBehind: 4, commitsAhead: 0));
            using var factory = CreateFactory(enableUpdateCheck: true, builtSha: "build-sha", configureServices: services =>
            {
                services.RemoveAll<IRepositoryUpdateChecker>();
                services.AddSingleton<IRepositoryUpdateChecker>(fake);
            });
            using var client = factory.CreateClient();

            var status = await WaitForUpdateStatusAsync(client, "UpdateAvailable");

            Assert.Equal("UpdateAvailable", status.GetProperty("status").GetString());
            Assert.Equal("remote-sha", status.GetProperty("remoteSha").GetString());
            Assert.Equal(4, status.GetProperty("commitsBehind").GetInt32());
        }

        [Fact]
        public async Task Background_service_reports_up_to_date_when_remote_matches_the_build()
        {
            var fake = new FakeRepositoryUpdateChecker(RepositoryUpdateCheckResult.Success("build-sha", RepositoryComparison.Identical, commitsBehind: 0, commitsAhead: 0));
            using var factory = CreateFactory(enableUpdateCheck: true, builtSha: "build-sha", configureServices: services =>
            {
                services.RemoveAll<IRepositoryUpdateChecker>();
                services.AddSingleton<IRepositoryUpdateChecker>(fake);
            });
            using var client = factory.CreateClient();

            var status = await WaitForUpdateStatusAsync(client, "UpToDate");

            Assert.Equal("UpToDate", status.GetProperty("status").GetString());
        }

        [Fact]
        public async Task Background_service_surfaces_unavailable_when_the_checker_fails()
        {
            var fake = new FakeRepositoryUpdateChecker(
                RepositoryUpdateCheckResult.Failure(UpdateCheckUnavailableReason.RepoAccessDenied, "HTTP 404"));
            using var factory = CreateFactory(enableUpdateCheck: true, builtSha: "build-sha", configureServices: services =>
            {
                services.RemoveAll<IRepositoryUpdateChecker>();
                services.AddSingleton<IRepositoryUpdateChecker>(fake);
            });
            using var client = factory.CreateClient();

            var status = await WaitForUpdateStatusAsync(client, "Unavailable");

            Assert.Equal("Unavailable", status.GetProperty("status").GetString());
            Assert.Equal("RepoAccessDenied", status.GetProperty("reason").GetString());
        }

        private static async Task<JsonElement> WaitForUpdateStatusAsync(HttpClient client, string expectedStatus)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var health = await client.GetStringAsync("/status/health");
                using var json = JsonDocument.Parse(health);
                var updateCheck = json.RootElement.GetProperty("updateCheck");
                if (string.Equals(updateCheck.GetProperty("status").GetString(), expectedStatus, StringComparison.Ordinal))
                {
                    return updateCheck.Clone();
                }

                await Task.Delay(100);
            }

            throw new TimeoutException($"Update check status did not reach '{expectedStatus}' in time.");
        }

        private UpdateBadgeViewModel BuildBadge(UpdateCheckSnapshot snapshot, DateTimeOffset nowUtc)
        {
            var readModel = new UpdateBadgeReadModel(
                new UpdateCheckRuntimeState(snapshot),
                LocalUpdateCheckOptions.From(new ConfigurationBuilder().Build()),
                new FixedClock(nowUtc));
            return readModel.Get();
        }

        private WebApplicationFactory<Program> CreateFactory(
            bool enableUpdateCheck,
            string? builtSha = null,
            Action<IServiceCollection>? configureServices = null) =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = "false",
                        ["KoLite:UpdateCheck:Enabled"] = enableUpdateCheck.ToString(),
                        ["KoLite:UpdateCheck:Interval"] = "00:00:01"
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.ClearProviders());
                    if (builtSha is not null)
                    {
                        services.RemoveAll<AppBuildVersion>();
                        services.AddSingleton(new AppBuildVersion(builtSha));
                    }
                });
                if (configureServices is not null)
                {
                    builder.ConfigureServices(configureServices);
                }
            });

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private sealed class FixedClock : IClock
        {
            private readonly DateTimeOffset now;

            public FixedClock(DateTimeOffset now) => this.now = now;

            public DateTimeOffset UtcNow => now;
        }

        private sealed class FakeRepositoryUpdateChecker : IRepositoryUpdateChecker
        {
            private readonly RepositoryUpdateCheckResult result;

            public FakeRepositoryUpdateChecker(RepositoryUpdateCheckResult result) => this.result = result;

            public Task<RepositoryUpdateCheckResult> CheckAsync(
                string repository,
                string branch,
                string? builtSha,
                CancellationToken cancellationToken) => Task.FromResult(result);
        }
    }
}
