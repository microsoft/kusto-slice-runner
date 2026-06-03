using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Ui;
using Microsoft.Data.Sqlite;

namespace KoLite.LocalApp.Tests
{
    public sealed class JobChartQueryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "job-chart-query-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteOperationalReadModelRepository readModels;

        public JobChartQueryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "charts.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(factory).Migrate();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
        }

        [Fact]
        public void Retry_success_counts_as_half_attempt_success_and_full_final_success()
        {
            catalog.Create(Schedule("job.chart"));
            state.Append("queued-chart", "job.chart", At(60), At(65), DurableSliceStatus.Queued, expectedVersion: 0);
            var firstLease = state.AcquireLease("lease-chart-1", "job.chart", At(60), At(65), "worker", TimeSpan.FromMinutes(10), At(66));
            Assert.NotNull(firstLease);
            Assert.True(state.FailLease("fail-chart-1", "job.chart", At(60), At(65), "worker", firstLease.LeaseToken!, At(67), "transient"));
            var retryLease = state.AcquireLease("lease-chart-2", "job.chart", At(60), At(65), "worker", TimeSpan.FromMinutes(10), At(68));
            Assert.NotNull(retryLease);
            Assert.True(state.CompleteLease("complete-chart-2", "job.chart", At(60), At(65), "worker", retryLease.LeaseToken!, At(69)));
            readModels.RecordAttempt("attempt-chart-1", "job.chart", At(60), At(65), 1, "FailedRetryable", "worker", At(66), At(67), "Transient", "try again");
            readModels.RecordAttempt("attempt-chart-2", "job.chart", At(60), At(65), 2, "Succeeded", "worker", At(68), At(69));
            var query = new JobChartQuery(factory, new ManualClock(At(120)));

            var charts = query.GetDashboardCharts(TimeSpan.FromDays(1));
            var attemptPoint = charts.FirstAttemptSuccess.Series.Single(s => s.Name == "job.chart").Points.Single(p => p.Denominator == 2);
            var finalPoint = charts.SuccessAfterRetries.Series.Single(s => s.Name == "job.chart").Points.Single(p => p.Denominator == 1);

            Assert.Equal(1, attemptPoint.Numerator);
            Assert.Equal(50.0, attemptPoint.Percent);
            Assert.Equal(1, finalPoint.Numerator);
            Assert.Equal(100.0, finalPoint.Percent);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string Schedule(string activityId) => $$"""
        {
          "activityId": "{{activityId}}",
          "functionName": "ChartFunction",
          "outputTable": "ChartOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 2,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
