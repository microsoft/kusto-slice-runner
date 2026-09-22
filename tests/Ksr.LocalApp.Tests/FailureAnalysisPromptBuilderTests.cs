// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.State;
using Ksr.LocalApp.FailureAnalysis;
using Ksr.LocalApp.Ui;
using Microsoft.Data.Sqlite;

namespace Ksr.LocalApp.Tests
{
    public sealed class FailureAnalysisPromptBuilderTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "failure-analysis-prompt-tests", Guid.NewGuid().ToString("N"));
        private readonly KsrSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly FailureAnalysisPromptBuilder builder;

        public FailureAnalysisPromptBuilderTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "prompt.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KsrSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            var readModels = new SqliteOperationalReadModelRepository(factory);
            var diagnostics = new SqliteDiagnosticsReadModelRepository(factory);
            builder = new FailureAnalysisPromptBuilder(readModels, diagnostics, new OperationalDetailsReadModel(readModels), SystemClock.Instance);
        }

        [Fact]
        public void Build_includes_failure_evidence_and_redacts_secrets()
        {
            catalog.Create(Schedule("job.analysis.prompt"));
            state.Append("failure-1", JobId("job.analysis.prompt"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "AccountKey=abc123secret leaked");
            state.Append("failure-2", JobId("job.analysis.prompt"), At(5), At(10), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "cross-cluster timeout to azcore5");
            var job = catalog.Get(JobId("job.analysis.prompt"))!;

            var evidence = builder.Build(job);

            Assert.True(evidence.FailureCount >= 2);
            Assert.Contains("job.analysis.prompt", evidence.Prompt);
            Assert.Contains("https://ksr-example.invalid", evidence.Prompt);
            Assert.Contains("Dead-lettered slices", evidence.Prompt);
            Assert.Contains("cross-cluster timeout to azcore5", evidence.Prompt);
            Assert.DoesNotContain("abc123secret", evidence.Prompt);
            Assert.Contains("<redacted>", evidence.Prompt);
        }

        [Fact]
        public void Build_reports_zero_failures_for_a_clean_job()
        {
            catalog.Create(Schedule("job.analysis.clean"));
            var job = catalog.Get(JobId("job.analysis.clean"))!;

            var evidence = builder.Build(job);

            Assert.Equal(0, evidence.FailureCount);
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
          "functionName": "BuildThing",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://ksr-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
