using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Throttling;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteIngestionThrottleRepositoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "throttle-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteIngestionThrottleRepository repository;

        public SqliteIngestionThrottleRepositoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "throttle.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            repository = new SqliteIngestionThrottleRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void Schema_creates_the_observations_table()
        {
            using var c = factory.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='ingestion_throttle_observations';";
            Assert.Equal("ingestion_throttle_observations", cmd.ExecuteScalar());
        }

        [Fact]
        public void Summarize_window_counts_distinct_slices_and_observations_per_cluster()
        {
            var now = DateTimeOffset.Parse("2026-06-23T22:00:00Z");
            // Cluster A: two distinct slices, one of them throttled twice (retry) => 3 observations, 2 slices.
            Record("a", "https://a.kusto.windows.net", "2026-06-23T06:55:00Z", now.AddMinutes(-2), capacity: 18);
            Record("a", "https://a.kusto.windows.net", "2026-06-23T06:55:00Z", now.AddMinutes(-1), capacity: 18);
            Record("a", "https://a.kusto.windows.net", "2026-06-23T07:00:00Z", now.AddMinutes(-1), capacity: 12);
            // Cluster B: one slice once.
            Record("b", "https://b.kusto.windows.net", "2026-06-23T07:45:00Z", now.AddMinutes(-3), capacity: 4);

            var summary = repository.SummarizeWindow(now.AddMinutes(-30));

            var a = summary.Single(s => s.ClusterUri == "https://a.kusto.windows.net");
            Assert.Equal(2, a.ThrottledSliceCount);
            Assert.Equal(3, a.ObservationCount);
            Assert.Equal(12, a.LatestReportedCapacity);

            var b = summary.Single(s => s.ClusterUri == "https://b.kusto.windows.net");
            Assert.Equal(1, b.ThrottledSliceCount);
            Assert.Equal(1, b.ObservationCount);
            Assert.Equal(4, b.LatestReportedCapacity);
        }

        [Fact]
        public void Summarize_window_excludes_observations_before_the_window()
        {
            var now = DateTimeOffset.Parse("2026-06-23T22:00:00Z");
            Record("a", "https://a.kusto.windows.net", "2026-06-23T05:00:00Z", now.AddHours(-2), capacity: 18);
            Record("a", "https://a.kusto.windows.net", "2026-06-23T06:00:00Z", now.AddMinutes(-5), capacity: 18);

            var summary = repository.SummarizeWindow(now.AddMinutes(-15));

            var a = Assert.Single(summary);
            Assert.Equal(1, a.ObservationCount);
        }

        [Fact]
        public void Reported_capacity_may_be_null()
        {
            var now = DateTimeOffset.Parse("2026-06-23T22:00:00Z");
            Record("a", "https://a.kusto.windows.net", "2026-06-23T06:55:00Z", now.AddMinutes(-1), capacity: null);

            var a = Assert.Single(repository.SummarizeWindow(now.AddMinutes(-30)));
            Assert.Null(a.LatestReportedCapacity);
        }

        private void Record(string jobId, string clusterUri, string sliceStart, DateTimeOffset observedAt, int? capacity)
        {
            var start = DateTimeOffset.Parse(sliceStart);
            repository.Record(new IngestionThrottleObservation(jobId, clusterUri, start, start.AddMinutes(5), Attempt: 2, capacity, observedAt));
        }
    }
}
