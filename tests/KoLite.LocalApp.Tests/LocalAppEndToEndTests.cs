using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLite.Local.Core.FailureSummaries;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.FailureSummaries;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalAppEndToEndTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "localapp-e2e-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> factory;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly ManualClock clock = new(At(15));

        public LocalAppEndToEndTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "e2e.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(sqlite).Migrate();
            catalog = new SqliteJobCatalogRepository(sqlite);
            state = new SqliteSliceStateRepository(sqlite);
            queue = new SqliteWorkQueueRepository(sqlite);
            readModels = new SqliteOperationalReadModelRepository(sqlite);
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = "false"
                    });
                });
                builder.ConfigureServices(services => services.AddLogging(logging => logging.ClearProviders()));
            });
        }

        [Fact]
        public async Task Local_stack_e2e_uses_file_backed_sqlite_and_test_executors_only()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            await ImportSchedule(client, Schedule("e2e.downstream", maxParallelism: 10, dependsOn: "e2e.upstream"));
            await ImportSchedule(client, Schedule("e2e.upstream", maxParallelism: 10));
            Assert.NotNull(catalog.Get("e2e.downstream"));
            Assert.NotNull(catalog.Get("e2e.upstream"));

            var scheduler = new SqliteLocalScheduler(catalog, state, queue, readModels, clock, new LocalSchedulerOptions(MaxSlicesPerTick: 20));
            var firstTick = scheduler.Tick();
            Assert.Equal(3, firstTick.DependencyBlocked);
            Assert.Equal(DurableSliceStatus.DependencyBlocked, state.Get("e2e.downstream", At(0), At(5)).Status);

            var executor = new RecordingExecutor();
            var worker = new SqliteLocalWorker(catalog, state, queue, readModels, executor, clock, new LocalWorkerOptions(WorkerId: "e2e-test-worker"));
            await DrainWorker(worker);
            Assert.Equal(3, executor.Requests.Count);
            Assert.All(executor.Requests, r => Assert.Equal("e2e.upstream", r.ActivityId));
            Assert.Equal(DurableSliceStatus.Completed, state.Get("e2e.upstream", At(10), At(15)).Status);

            var secondTick = scheduler.Tick();
            Assert.Equal(3, secondTick.Enqueued);
            await DrainWorker(worker);
            Assert.Equal(6, executor.Requests.Count);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("e2e.downstream", At(0), At(5)).Status);

            var dashboard = await client.GetStringAsync("/");
            var history = await client.GetStringAsync("/jobs/e2e.downstream/history?from=2026-01-01T00%3A00%3A00Z&to=2026-01-01T00%3A15%3A00Z");
            Assert.Contains("KO Lite Local Dashboard", dashboard);
            Assert.Contains("e2e.downstream", dashboard);
            Assert.Contains("Slice History: e2e.downstream", history);
            Assert.Contains("class=\"cell completed\"", history);

            catalog.Create(Schedule("e2e.repair", maxParallelism: 10));
            state.Append("repair-failed", "e2e.repair", At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "test failure");
            var repair = new SqliteRepairService(sqlite, catalog, state, queue, clock);
            var repairPlan = repair.PlanAndEnqueue(new RepairPlanRequest("e2e.repair", At(0), At(10), "e2e-test", "test repair execution"));
            Assert.Equal(2, repairPlan.Queued);
            await DrainWorker(worker);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("e2e.repair", At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("e2e.repair", At(5), At(10)).Status);
            Assert.Contains(executor.Requests, r => r.ActivityId == "e2e.repair");

            var lifecycle = new SqliteJobLifecycleService(sqlite, catalog);
            var life = catalog.Create(Schedule("e2e.lifecycle", maxParallelism: 1));
            var softDeleted = lifecycle.SoftDelete("e2e.lifecycle", life.CatalogVersion, "e2e-test", "dry-run lifecycle safety");
            Assert.False(softDeleted.IsEnabled);
            var restored = lifecycle.Restore("e2e.lifecycle", softDeleted.CatalogVersion, "e2e-test", "restore before purge");
            Assert.True(restored.IsEnabled);
            var disabled = lifecycle.SoftDelete("e2e.lifecycle", restored.CatalogVersion, "e2e-test", "disable before hard-delete");
            var purged = lifecycle.HardDelete("e2e.lifecycle", "DELETE e2e.lifecycle", "e2e-test", "local hard-delete safety path");
            Assert.False(disabled.IsEnabled);
            Assert.Equal(1, purged.DeletedJobs);
            Assert.Null(catalog.Get("e2e.lifecycle"));
            Assert.Equal(1, catalog.Create(Schedule("e2e.lifecycle", maxParallelism: 1)).CatalogVersion);

            catalog.Create(Schedule("e2e.summary", maxParallelism: 1));
            state.Append("summary-failed", "e2e.summary", At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "test summary failure");
            var runner = new CapturingFailureSummaryRunner();
            var summary = await new SqliteFailureSummaryService(sqlite, runner).SummarizeRecentFailuresAsync("e2e.summary");
            Assert.Equal("Completed", summary.Status);
            Assert.Contains("Fake e2e failure summary", summary.SummaryMarkdown);
            Assert.Single(runner.Prompts);

            var health = await client.GetStringAsync("/status/health");
            using var healthJson = JsonDocument.Parse(health);
            Assert.Equal("Healthy", healthJson.RootElement.GetProperty("status").GetString());
            Assert.Equal(databasePath, healthJson.RootElement.GetProperty("databasePath").GetString());
            Assert.Equal("Enabled", healthJson.RootElement.GetProperty("liveKustoExecution").GetString());
            Assert.Equal("AzureCli", healthJson.RootElement.GetProperty("kustoAuthMode").GetString());
            Assert.Equal(10, healthJson.RootElement.GetProperty("scheduler").GetProperty("workerConcurrency").GetInt32());
            Assert.False(healthJson.RootElement.GetProperty("scheduler").GetProperty("logEveryPass").GetBoolean());
            var workerPool = healthJson.RootElement.GetProperty("workerPool");
            Assert.Equal("Fixed", workerPool.GetProperty("mode").GetString());
            Assert.Equal(10, workerPool.GetProperty("maxConcurrency").GetInt32());
            Assert.Equal("Default", workerPool.GetProperty("maxConcurrencySource").GetString());
            Assert.Equal(TimeSpan.FromMilliseconds(250).ToString(), workerPool.GetProperty("idleDelay").GetString());

            Assert.All(executor.OutputReferences, reference => Assert.StartsWith("test://", reference, StringComparison.Ordinal));
            Assert.Equal(8, executor.Requests.Count);
        }

        public void Dispose()
        {
            factory.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        private async Task ImportSchedule(HttpClient client, string scheduleJson)
        {
            var token = await ReadFormToken(client, "/catalog/import");
            var response = await PostForm(client, "/catalog/import", token, new Dictionary<string, string>
            {
                ["scheduleJson"] = scheduleJson
            });
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        private static async Task DrainWorker(SqliteLocalWorker worker)
        {
            for (var i = 0; i < 20; i++)
            {
                var result = await worker.RunOnceAsync();
                if (!result.ClaimedWork) return;
                Assert.True(result.Executed);
                Assert.True(result.Succeeded);
            }

            Assert.Fail("Worker still had claimable fake work after 20 iterations.");
        }

        private async Task<FormToken> ReadFormToken(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant).Groups["token"].Value;
            Assert.False(string.IsNullOrWhiteSpace(token));
            var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(v => v.Split(';', 2)[0]))
                : string.Empty;
            return new FormToken(token, cookie);
        }

        private static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, FormToken token, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = token.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(values)
            };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string Schedule(string activityId, int maxParallelism, string? dependsOn = null) => $$"""
        {
          "activityId": "{{activityId}}",
          "functionName": "E2ETestFunction",
          "outputTable": "E2ETestOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": {{maxParallelism}},
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;

        private sealed record FormToken(string Value, string Cookie);

        private sealed class RecordingExecutor : ILocalSliceOutputExecutor
        {
            public List<SliceRange> Requests { get; } = [];
            public List<string> OutputReferences { get; } = [];

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                Requests.Add(slice);
                var reference = $"test://{job.ActivityId}/{slice.ToKey().Value}";
                OutputReferences.Add(reference);
                return Task.FromResult(LocalSliceOutputResult.Success(reference));
            }
        }

        private sealed class CapturingFailureSummaryRunner : IFailureSummaryRunner
        {
            public List<string> Prompts { get; } = [];

            public Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
            {
                Prompts.Add(prompt);
                return Task.FromResult(FailureSummaryRunnerResult.Success("## Fake e2e failure summary"));
            }
        }
    }
}
