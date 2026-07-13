using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.State;
using KoLite.Local.Sqlite.Throttling;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Tests
{
    public sealed class ThrottleSeverityQueryTests : IDisposable
    {
        private const string Cluster = "https://kolite-severity.invalid";

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "throttle-severity-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteIngestionThrottleRepository throttle;

        public ThrottleSeverityQueryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "severity.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            throttle = new SqliteIngestionThrottleRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }

        [Fact]
        public void Computes_headline_rate_and_bucketed_points()
        {
            var jobId = catalog.Create(Schedule("job.sev")).JobId;
            // now = At(120). One clean attempt and two throttled attempts within the last 30 minutes.
            SeedAttempt(jobId, At(100), "Succeeded", At(100), throttled: false);
            SeedAttempt(jobId, At(110), "FailedRetryable", At(110), throttled: true);
            SeedAttempt(jobId, At(115), "FailedRetryable", At(115), throttled: true);

            var query = new ThrottleSeverityQuery(factory, new ManualClock(At(120)));
            var chart = query.GetSeverity(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));

            Assert.True(chart.HasData);
            Assert.True(chart.HasThrottling);
            Assert.Equal(2, chart.HeadlineThrottledAttempts);
            Assert.Equal(3, chart.HeadlineTotalAttempts);
            Assert.Equal(66.7, chart.HeadlinePercent);
            Assert.Equal(2, chart.Points.Sum(p => p.ThrottledAttempts));
            Assert.Equal(3, chart.Points.Sum(p => p.TotalAttempts));
        }

        [Fact]
        public void Reports_no_data_for_an_empty_range()
        {
            catalog.Create(Schedule("job.sev"));
            var query = new ThrottleSeverityQuery(factory, new ManualClock(At(120)));

            var chart = query.GetSeverity(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5));

            Assert.False(chart.HasData);
            Assert.False(chart.HasThrottling);
            Assert.Null(chart.HeadlinePercent);
            Assert.All(chart.Points, point => Assert.Equal(0, point.TotalAttempts));
        }

        private void SeedAttempt(string jobId, DateTimeOffset sliceStart, string status, DateTimeOffset completedAt, bool throttled)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            state.Append($"{jobId}-st-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, DurableSliceStatus.Running, expectedVersion: 0);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, status, "worker", completedAt.AddMinutes(-1), completedAt);
            if (throttled)
            {
                throttle.Record(new IngestionThrottleObservation(jobId, Cluster, sliceStart, sliceEnd, Attempt: 1, ReportedCapacity: 18, completedAt, Terminal: false));
            }
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
          "functionName": "SeverityFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "{{Cluster}}", "database": "DemoDb" }
        }
        """;
    }
}
