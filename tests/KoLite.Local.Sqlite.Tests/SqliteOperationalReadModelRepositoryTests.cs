using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteOperationalReadModelRepositoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "readmodel-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;

        public SqliteOperationalReadModelRepositoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "readmodels.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(factory).Migrate();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            catalog.Create(Schedule("job.obs"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        [Fact]
        public void Read_models_reflect_state_events_queue_attempts()
        {
            state.Append("queued", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            var lease = state.AcquireLease("running", JobId("job.obs"), At(0), At(5), "worker", TimeSpan.FromMinutes(5), At(10));
            state.CompleteLease("complete", JobId("job.obs"), At(0), At(5), "worker", lease!.LeaseToken!, At(11), "{\"rows\":1}");
            state.Append("failed", JobId("job.obs"), At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("queued-10", JobId("job.obs"), At(10), At(15), DurableSliceStatus.Queued, expectedVersion: 0);
            state.Append("queued-15", JobId("job.obs"), At(15), At(20), DurableSliceStatus.Queued, expectedVersion: 0);
            state.Append("retry-complete", JobId("job.obs"), At(20), At(25), DurableSliceStatus.Completed, expectedVersion: 0);
            queue.Enqueue(JobId("job.obs"), At(10), At(15), "queued-key", At(20));
            var leasedQueue = queue.Enqueue(JobId("job.obs"), At(15), At(20), "leased-key", At(20));
            queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(20));
            readModels.RecordScheduledSlice(JobId("job.obs"), At(0), At(5), "Completed", At(0), At(5));
            readModels.RecordAttempt("attempt-1", JobId("job.obs"), At(0), At(5), 1, "Succeeded", "worker", At(10), At(11));
            readModels.RecordAttempt("attempt-retry-1", JobId("job.obs"), At(20), At(25), 1, "FailedRetryable", "worker", At(20), At(21));
            readModels.RecordAttempt("attempt-retry-2", JobId("job.obs"), At(20), At(25), 2, "Succeeded", "worker", At(22), At(23));
            readModels.RecordLog("Error", "boom", "test", JobId("job.obs"), At(5), At(10));

            var summary = readModels.GetJobStatusSummaries().Single(j => j.JobId == JobId("job.obs"));
            var queueStatus = readModels.GetQueueStatus("default", At(21));
            var failure = readModels.GetRecentFailures().Single(f => f.JobId == JobId("job.obs"));
            var events = readModels.GetRecentSliceEvents();
            var slice = readModels.GetSliceStatus(JobId("job.obs")).Single(s => s.SliceStartUtc == At(0));
            var retrySlice = readModels.GetSliceStatus(JobId("job.obs")).Single(s => s.SliceStartUtc == At(20));

            Assert.Equal(2, summary.CompletedCount);
            Assert.Equal(1, summary.FailedCount);
            Assert.Equal(1, queueStatus.QueuedCount);
            Assert.Equal(1, queueStatus.LeasedCount);
            Assert.Equal("boom", failure.Reason);
            Assert.Contains(events, e => e.JobId == JobId("job.obs") && e.EventType == "Completed");
            Assert.Equal(1, slice.SuccessfulAttempt);
            Assert.Equal("Succeeded", slice.LatestAttemptStatus);
            Assert.Equal(2, retrySlice.SuccessfulAttempt);
            Assert.Equal("Succeeded", retrySlice.LatestAttemptStatus);
            Assert.NotNull(leasedQueue);
        }

        [Fact]
        public void Retention_deletes_only_non_authoritative_old_read_model_rows()
        {
            state.Append("queued", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            state.Append("complete", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 1);
            state.Append("active-queue-state", JobId("job.obs"), At(5), At(10), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.obs"), At(5), At(10), "active-queue", At(10));
            readModels.RecordScheduledSlice(JobId("job.obs"), At(0), At(5), "Completed", At(0), At(5));
            readModels.RecordAttempt("old-attempt", JobId("job.obs"), At(0), At(5), 1, "Succeeded", "worker", At(0), At(1));
            readModels.RecordLog("Information", "old log", "test", JobId("job.obs"), At(0), At(5));

            var result = readModels.CleanupOldReadModels(DateTimeOffset.UtcNow.AddDays(1), batchSize: 100);

            Assert.True(result.LogsDeleted >= 1);
            Assert.True(result.AttemptsDeleted >= 1);
            Assert.True(result.ScheduledSlicesDeleted >= 1);
            Assert.Equal(3, Count("slice_state_events"));
            Assert.Equal(2, Count("current_slice_state"));
            Assert.Equal(1, Count("work_queue"));
            Assert.Equal(1, Count("job_definition_events"));
            Assert.Equal(1, Count("retention_runs"));
        }

        private int Count(string table)
        {
            using var c = factory.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM {table};"; return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
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
          "functionName": "ObsFunction",
          "outputTable": "ObsOutput",
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
