using KoLite.Local.Core.Orchestration;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Throttling;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class IngestionThrottleObserverTests : IDisposable
    {
        private const string ThrottleMessage = "TooManyRequests (429): CommandType: 'TableSetOrAppend', Capacity: 18, Origin: 'CapacityPolicy/Ingestion'.";

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "throttle-observer-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteIngestionThrottleRepository store;
        private readonly IngestionThrottleObserver observer;

        public IngestionThrottleObserverTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "observer.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(factory).Migrate();
            store = new SqliteIngestionThrottleRepository(factory);
            observer = new IngestionThrottleObserver(store);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        [Fact]
        public void Records_an_observation_for_an_ingestion_throttle_with_known_cluster()
        {
            var recorded = observer.Observe(Event(
                status: LocalWorkerProgressStatus.FailedRetryable,
                errorCode: "KustoRequestThrottledException",
                errorMessage: ThrottleMessage,
                clusterUri: "https://sample-data.centralus.kusto.windows.net"));

            Assert.True(recorded);
            var summary = Assert.Single(store.SummarizeWindow(DateTimeOffset.UtcNow.AddHours(-1)));
            Assert.Equal("https://sample-data.centralus.kusto.windows.net", summary.ClusterUri);
            Assert.Equal(18, summary.LatestReportedCapacity);
        }

        [Fact]
        public void Ignores_a_non_throttle_failure()
        {
            var recorded = observer.Observe(Event(
                status: LocalWorkerProgressStatus.FailedRetryable,
                errorCode: "KustoServiceTimeoutException",
                errorMessage: "Query execution has exceeded the allowed timeout.",
                clusterUri: "https://sample-data.centralus.kusto.windows.net"));

            Assert.False(recorded);
            Assert.Empty(store.SummarizeWindow(DateTimeOffset.UtcNow.AddHours(-1)));
        }

        [Fact]
        public void Ignores_a_throttle_when_the_cluster_is_unknown()
        {
            var recorded = observer.Observe(Event(
                status: LocalWorkerProgressStatus.FailedRetryable,
                errorCode: "KustoRequestThrottledException",
                errorMessage: ThrottleMessage,
                clusterUri: null));

            Assert.False(recorded);
            Assert.Empty(store.SummarizeWindow(DateTimeOffset.UtcNow.AddHours(-1)));
        }

        [Fact]
        public void Records_a_throttle_that_exhausted_retries_and_dead_lettered()
        {
            var recorded = observer.Observe(Event(
                status: LocalWorkerProgressStatus.DeadLettered,
                errorCode: "KustoRequestThrottledException",
                errorMessage: ThrottleMessage,
                clusterUri: "https://sample-data.centralus.kusto.windows.net"));

            Assert.True(recorded);
            Assert.Single(store.SummarizeWindow(DateTimeOffset.UtcNow.AddHours(-1)));
        }

        private static LocalWorkerProgressEvent Event(LocalWorkerProgressStatus status, string? errorCode, string? errorMessage, string? clusterUri)
        {
            var start = DateTimeOffset.UtcNow.AddMinutes(-5);
            return new LocalWorkerProgressEvent(
                JobId: "ccaa54932f874b3c8c15faf7e52bcc70",
                QueueItemId: Guid.NewGuid().ToString("N"),
                SliceStartUtc: start,
                SliceEndUtc: start.AddMinutes(5),
                Attempt: 2,
                WorkerId: "local-web-worker",
                Status: status,
                StartedAtUtc: start,
                CompletedAtUtc: DateTimeOffset.UtcNow,
                ErrorCode: errorCode,
                ErrorMessage: errorMessage,
                IsRetryable: status == LocalWorkerProgressStatus.FailedRetryable,
                DeadLettered: status == LocalWorkerProgressStatus.DeadLettered,
                ClusterUri: clusterUri);
        }
    }
}
