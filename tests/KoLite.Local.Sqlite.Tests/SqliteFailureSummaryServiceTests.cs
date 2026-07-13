using KoLite.Local.Core.FailureSummaries;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.FailureSummaries;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteFailureSummaryServiceTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "summary-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;

        public SqliteFailureSummaryServiceTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "summary.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
        }

        [Fact]
        public async Task Generates_persists_redacted_cached_summary_without_mutating_scheduler_state()
        {
            catalog.Create(Schedule("job.summary"));
            state.Append("failure", JobId("job.summary"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "AccountKey=abc123; token=secret");
            queue.Enqueue(JobId("job.summary"), At(0), At(5), "normal|job.summary|0", At(0));
            var runner = new CapturingRunner();
            var service = new SqliteFailureSummaryService(factory, runner);

            var first = await service.SummarizeRecentFailuresAsync(JobId("job.summary"));
            var second = await service.SummarizeRecentFailuresAsync(JobId("job.summary"));

            Assert.Equal("Completed", first.Status);
            Assert.Equal(first.RunId, second.RunId);
            Assert.Single(runner.Prompts);
            Assert.DoesNotContain("abc123", runner.Prompts[0], StringComparison.Ordinal);
            Assert.DoesNotContain("secret", runner.Prompts[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("<redacted>", runner.Prompts[0], StringComparison.Ordinal);
            Assert.DoesNotContain("abc123", first.PromptPreview, StringComparison.Ordinal);
            Assert.Contains("Fake deterministic summary", first.SummaryMarkdown);
            Assert.Equal(DurableSliceStatus.Failed, state.Get(JobId("job.summary"), At(0), At(5)).Status);
            Assert.Single(queue.List(JobId("job.summary")));
            Assert.Equal(DurableWorkQueueState.Queued, queue.List(JobId("job.summary")).Single().State);
        }

        [Fact]
        public async Task Runner_failure_is_visible_in_persisted_summary_run()
        {
            catalog.Create(Schedule("job.summary.fail"));
            state.Append("failure", JobId("job.summary.fail"), At(0), At(5), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "boom");
            var service = new SqliteFailureSummaryService(factory, new FailingRunner());

            var run = await service.SummarizeRecentFailuresAsync(JobId("job.summary.fail"));

            Assert.Equal("Failed", run.Status);
            Assert.Null(run.SummaryMarkdown);
            Assert.Single(service.ListRuns(JobId("job.summary.fail")));
        }

        [Fact]
        public async Task Failed_summary_run_is_not_reused_for_same_input_hash()
        {
            catalog.Create(Schedule("job.summary.retry"));
            state.Append("failure", JobId("job.summary.retry"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "same boom");
            var runner = new FailThenSucceedRunner();
            var service = new SqliteFailureSummaryService(factory, runner);

            var failed = await service.SummarizeRecentFailuresAsync(JobId("job.summary.retry"));
            var completed = await service.SummarizeRecentFailuresAsync(JobId("job.summary.retry"));

            Assert.Equal("Failed", failed.Status);
            Assert.Equal("Completed", completed.Status);
            Assert.NotEqual(failed.RunId, completed.RunId);
            Assert.Equal(failed.InputHash, completed.InputHash);
            Assert.Equal(2, runner.Calls);
            Assert.Equal(2, service.ListRuns(JobId("job.summary.retry")).Count);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "SummaryFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;

        private sealed class CapturingRunner : IFailureSummaryRunner
        {
            public List<string> Prompts { get; } = [];
            public Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
            {
                Prompts.Add(prompt);
                return Task.FromResult(FailureSummaryRunnerResult.Success("## Fake deterministic summary"));
            }
        }

        private sealed class FailingRunner : IFailureSummaryRunner
        {
            public Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default) =>
                Task.FromResult(FailureSummaryRunnerResult.Failure("copilot unavailable", 127));
        }

        private sealed class FailThenSucceedRunner : IFailureSummaryRunner
        {
            public int Calls { get; private set; }

            public Task<FailureSummaryRunnerResult> RunAsync(string prompt, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(Calls == 1
                    ? FailureSummaryRunnerResult.Failure("copilot unavailable", 127)
                    : FailureSummaryRunnerResult.Success("## Retry summary"));
            }
        }
    }
}
