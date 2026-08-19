using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
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
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            catalog.Create(Schedule("job.obs"));
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
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

            var cutoff = DateTimeOffset.UtcNow.AddDays(1);
            var result = readModels.CleanupOldReadModels(cutoff, cutoff, batchSize: 100);

            Assert.True(result.LogsDeleted >= 1);
            Assert.True(result.AttemptsDeleted >= 1);
            Assert.True(result.ScheduledSlicesDeleted >= 1);
            Assert.Equal(3, Count("slice_state_events"));
            Assert.Equal(2, Count("current_slice_state"));
            Assert.Equal(1, Count("work_queue"));
            Assert.Equal(1, Count("job_definition_events"));
            Assert.Equal(1, Count("retention_runs"));
        }

        [Fact]
        public void Retention_prunes_terminal_queue_rows_but_preserves_active_and_leased()
        {
            // Leased row (claimed, not completed) — must survive retention.
            state.Append("leased-state", JobId("job.obs"), At(5), At(10), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.obs"), At(5), At(10), "leased-key", At(0));
            queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(0));

            // Completed row — terminal and old, must be pruned.
            state.Append("done-state", JobId("job.obs"), At(10), At(15), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.obs"), At(10), At(15), "done-key", At(0));
            var claimedDone = queue.Claim("default", "worker", TimeSpan.FromMinutes(5), At(0));
            Assert.True(queue.Complete(claimedDone!.QueueItemId, "worker"));

            // Queued row (never claimed) — must survive retention.
            state.Append("queued-state", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.obs"), At(0), At(5), "queued-key", At(0));

            var cutoff = DateTimeOffset.UtcNow.AddDays(1);
            var result = readModels.CleanupOldReadModels(cutoff, cutoff, batchSize: 100);

            Assert.Equal(1, result.QueueRowsDeleted);
            Assert.Equal(2, Count("work_queue"));
            Assert.Equal(0, CountWhere("work_queue", "state IN ('Completed','DeadLettered')"));
            Assert.Equal(1, CountWhere("work_queue", "state = 'Leased'"));
            Assert.Equal(1, CountWhere("work_queue", "state = 'Queued'"));
            // Authoritative window-history is never touched.
            Assert.Equal(3, Count("current_slice_state"));
        }

        [Fact]
        public void Retention_protects_chart_backing_attempts_below_max_chart_range()
        {
            state.Append("recent-state", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("recent", JobId("job.obs"), At(0), At(5), 1, "Succeeded", "worker", DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));

            // Standard cutoff (now) would delete the attempt, but the older protected cutoff (60d ago)
            // keeps chart-backing data within the dashboard's max selectable range.
            var protectedResult = readModels.CleanupOldReadModels(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-60), batchSize: 100);
            Assert.Equal(0, protectedResult.AttemptsDeleted);
            Assert.Equal(1, Count("slice_attempts"));

            // Without the clamp (both cutoffs at now), the same attempt is pruned.
            var unclampedResult = readModels.CleanupOldReadModels(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, batchSize: 100);
            Assert.Equal(1, unclampedResult.AttemptsDeleted);
            Assert.Equal(0, Count("slice_attempts"));
        }

        [Fact]
        public void Recent_throughput_counts_only_succeeded_completions_within_window()
        {
            foreach (var (start, end) in new[] { (At(0), At(5)), (At(5), At(10)), (At(10), At(15)), (At(15), At(20)), (At(20), At(25)) })
            {
                state.Append($"slice-{start:HHmm}", JobId("job.obs"), start, end, DurableSliceStatus.Completed, expectedVersion: 0);
            }

            readModels.RecordAttempt("succ-1", JobId("job.obs"), At(0), At(5), 1, "Succeeded", "worker", At(60), At(70));
            readModels.RecordAttempt("succ-2", JobId("job.obs"), At(5), At(10), 1, "Succeeded", "worker", At(80), At(90));
            readModels.RecordAttempt("succ-3", JobId("job.obs"), At(10), At(15), 1, "Succeeded", "worker", At(100), At(110));
            readModels.RecordAttempt("succ-old", JobId("job.obs"), At(15), At(20), 1, "Succeeded", "worker", At(10), At(20));
            readModels.RecordAttempt("retry-in-window", JobId("job.obs"), At(20), At(25), 1, "FailedRetryable", "worker", At(95), At(96));

            var sample = readModels.GetRecentSucceededThroughput(JobId("job.obs"), At(60));

            Assert.Equal(3, sample.SucceededCount);
            Assert.Equal(At(70), sample.FirstCompletedUtc);
            Assert.Equal(At(110), sample.LastCompletedUtc);
        }

        [Fact]
        public void Recent_throughput_is_empty_when_no_completions_in_window()
        {
            state.Append("slice-empty", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("succ-1", JobId("job.obs"), At(0), At(5), 1, "Succeeded", "worker", At(60), At(70));

            var sample = readModels.GetRecentSucceededThroughput(JobId("job.obs"), At(1000));

            Assert.Equal(0, sample.SucceededCount);
            Assert.Null(sample.FirstCompletedUtc);
            Assert.Null(sample.LastCompletedUtc);
        }

        [Fact]
        public void GetSliceAttemptsPage_uses_activity_time_and_attempt_id_keyset()
        {
            state.Append("attempt-state-c", JobId("job.obs"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("attempt-state-b", JobId("job.obs"), At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("attempt-state-a", JobId("job.obs"), At(10), At(15), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("attempt-c", JobId("job.obs"), At(0), At(5), 1, "Succeeded", "worker", At(10), At(11));
            readModels.RecordAttempt("attempt-b", JobId("job.obs"), At(5), At(10), 1, "Succeeded", "worker", At(10), At(11));
            readModels.RecordAttempt("attempt-a", JobId("job.obs"), At(10), At(15), 1, "Succeeded", "worker", At(10), At(11));

            var first = readModels.GetSliceAttemptsPage(
                JobId("job.obs"),
                sliceStartUtc: null,
                sliceEndUtc: null,
                cursorActivityAtUtc: null,
                cursorId: null,
                take: 2);
            var second = readModels.GetSliceAttemptsPage(
                JobId("job.obs"),
                sliceStartUtc: null,
                sliceEndUtc: null,
                cursorActivityAtUtc: first[^1].CompletedAtUtc,
                cursorId: first[^1].AttemptId,
                take: 2);

            Assert.Equal(new[] { "attempt-c", "attempt-b" }, first.Select(item => item.AttemptId));
            Assert.Equal("attempt-a", Assert.Single(second).AttemptId);
        }

        [Fact]
        public void Recent_throughput_counts_complete_logical_windows_not_successful_chunks()
        {
            catalog.Create(Schedule("job.obs.chunks", chunks: 2));
            state.Append("chunk-window-0", JobId("job.obs.chunks"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("chunk-window-1", JobId("job.obs.chunks"), At(5), At(10), DurableSliceStatus.Queued, expectedVersion: 0);
            readModels.RecordAttempt("chunk-0-0", JobId("job.obs.chunks"), At(0), At(5), 1, "Succeeded", "worker", At(50), At(59), chunkId: 0, totalChunks: 2);
            readModels.RecordAttempt("chunk-0-1", JobId("job.obs.chunks"), At(0), At(5), 1, "Succeeded", "worker", At(61), At(71), chunkId: 1, totalChunks: 2);
            readModels.RecordAttempt("chunk-1-0", JobId("job.obs.chunks"), At(5), At(10), 1, "Succeeded", "worker", At(80), At(90), chunkId: 0, totalChunks: 2);

            var sample = readModels.GetRecentSucceededThroughput(JobId("job.obs.chunks"), At(60));

            Assert.Equal(1, sample.SucceededCount);
            Assert.Equal(At(71), sample.FirstCompletedUtc);
            Assert.Equal(At(71), sample.LastCompletedUtc);
        }

        [Fact]
        public void Recent_slice_states_returns_newest_first_bounded_by_window()
        {
            catalog.Create(Schedule("job.obs2"));

            // 12 windows for job.obs; newest two are DeadLettered (At55) then Failed (At50).
            for (var i = 0; i < 12; i++)
            {
                var start = At(i * 5);
                var status = i switch
                {
                    11 => DurableSliceStatus.DeadLettered,
                    10 => DurableSliceStatus.Failed,
                    _ => DurableSliceStatus.Completed
                };
                state.Append($"obs-{i}", JobId("job.obs"), start, start.AddMinutes(5), status, expectedVersion: 0);
            }

            // A second job with only two windows, to confirm per-job partitioning.
            state.Append("obs2-0", JobId("job.obs2"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("obs2-1", JobId("job.obs2"), At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0);

            var recent = readModels.GetRecentSliceStates(10);

            var obs = recent[JobId("job.obs")];
            Assert.Equal(10, obs.Count);
            Assert.Equal("DeadLettered", obs[0]);
            Assert.Equal("Failed", obs[1]);
            Assert.All(obs.Skip(2), s => Assert.Equal("Completed", s));

            var obs2 = recent[JobId("job.obs2")];
            Assert.Equal(2, obs2.Count);
            Assert.Equal("Failed", obs2[0]);
            Assert.Equal("Completed", obs2[1]);
        }

        private int Count(string table)
        {
            using var c = factory.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM {table};"; return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private int CountWhere(string table, string predicate)
        {
            using var c = factory.OpenConnection(); using var cmd = c.CreateCommand(); cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {predicate};"; return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, int? chunks = null) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "ObsFunction",
          "outputTable": "ObsOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 2,
          "queryTimeout": "00:01:00",
          {{(chunks is null ? string.Empty : $"\"chunks\": {chunks},")}}
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
