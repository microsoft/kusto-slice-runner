// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.State;
using Ksr.Local.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class SqliteDiagnosticsReadModelRepositoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "diagnostics-readmodel-tests", Guid.NewGuid().ToString("N"));
        private readonly KsrSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteChunkStateRepository chunkState;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;

        public SqliteDiagnosticsReadModelRepositoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "diagnostics.db")));
            new KsrSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            chunkState = new SqliteChunkStateRepository(factory);
            observability = new SqliteOperationalReadModelRepository(factory);
            diagnostics = new SqliteDiagnosticsReadModelRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void GetRunningSlices_returns_all_jobs_oldest_first_and_caps_take()
        {
            catalog.Create(Schedule("run.a"));
            catalog.Create(Schedule("run.b"));
            state.AcquireLease("a1", JobId("run.a"), At(0), At(5), "worker-1", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow.AddHours(-9));
            state.AcquireLease("a2", JobId("run.a"), At(5), At(10), "worker-2", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow.AddHours(-8));
            state.AcquireLease("b1", JobId("run.b"), At(0), At(5), "worker-3", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow.AddHours(-7));

            var all = diagnostics.GetRunningSlices(jobId: null, DateTimeOffset.UtcNow, take: 100);
            Assert.Equal(3, all.Count);
            Assert.Equal("worker-1", all[0].LeaseOwner);
            Assert.True(all.All(s => s.LeaseExpired));
            Assert.True(all[0].UpdatedAtUtc <= all[1].UpdatedAtUtc && all[1].UpdatedAtUtc <= all[2].UpdatedAtUtc);

            var capped = diagnostics.GetRunningSlices(jobId: null, DateTimeOffset.UtcNow, take: 2);
            Assert.Equal(2, capped.Count);

            var perJob = diagnostics.GetRunningSlices(JobId("run.b"), DateTimeOffset.UtcNow, take: 100);
            Assert.Equal("worker-3", Assert.Single(perJob).LeaseOwner);
        }

        [Fact]
        public void GetRunningSlices_exposes_running_attempt_start()
        {
            catalog.Create(Schedule("run.start"));
            var jobId = JobId("run.start");
            state.AcquireLease("s1", jobId, At(0), At(5), "worker-1", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow.AddHours(-1));
            var startedAt = new DateTimeOffset(2026, 1, 1, 8, 30, 0, TimeSpan.Zero);
            observability.RecordAttempt("s1-att", jobId, At(0), At(5), 1, "Started", "worker-1", startedAt, completedAtUtc: null);

            // A second running slice with no in-flight Started attempt row -> null start.
            state.AcquireLease("s2", jobId, At(5), At(10), "worker-1", TimeSpan.FromMinutes(22), DateTimeOffset.UtcNow.AddHours(-1));

            var slices = diagnostics.GetRunningSlices(jobId, DateTimeOffset.UtcNow, take: 100);
            Assert.Equal(startedAt, slices.Single(s => s.SliceStartUtc == At(0)).StartedAtUtc);
            Assert.Null(slices.Single(s => s.SliceStartUtc == At(5)).StartedAtUtc);
        }

        [Fact]
        public void GetRunningActivitySnapshot_uses_one_capped_parent_set()
        {
            var first = catalog.Create(Schedule("run.chunk.first", chunks: 2, maxParallelism: 2));
            var second = catalog.Create(Schedule("run.chunk.second", chunks: 2, maxParallelism: 2));
            var firstSlice = new SliceRange(first.JobId, At(0), At(5));
            var secondSlice = new SliceRange(second.JobId, At(0), At(5));
            var firstChild = chunkState.EnsureWindow(firstSlice, 2, "test")[0];
            var secondChild = chunkState.EnsureWindow(secondSlice, 2, "test")[0];
            chunkState.MarkQueued("first-queued", firstChild.Execution, actor: "test");
            chunkState.MarkQueued("second-queued", secondChild.Execution, actor: "test");
            chunkState.AcquireLease("first-lease", firstChild.Execution, "worker-first", TimeSpan.FromMinutes(30), At(10));
            chunkState.AcquireLease("second-lease", secondChild.Execution, "worker-second", TimeSpan.FromMinutes(30), At(11));

            var snapshot = diagnostics.GetRunningActivitySnapshot(At(20), take: 1);

            Assert.Equal(first.JobId, Assert.Single(snapshot.Slices).JobId);
            Assert.All(snapshot.ChunkExecutions, execution => Assert.Equal(first.JobId, execution.JobId));
            Assert.Contains(snapshot.ChunkExecutions, execution => execution.ChunkId == 0 && execution.WorkerId == "worker-first");
            Assert.DoesNotContain(snapshot.ChunkExecutions, execution => execution.JobId == second.JobId);
        }

        [Fact]
        public void GetTypicalSuccessfulDurationsByJob_returns_median_of_recent_successes()
        {
            catalog.Create(Schedule("dur.a"));
            catalog.Create(Schedule("dur.b"));
            catalog.Create(Schedule("dur.none"));
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var anchor = DateTimeOffset.UtcNow.AddHours(-1);

            // Job A: in-window durations 4, 20, 6 min -> median 6 min. A Failed attempt and an
            // out-of-window success must both be ignored.
            SeedSuccessAttempt("dur.a", At(0), anchor.AddMinutes(-30), TimeSpan.FromMinutes(4));
            SeedSuccessAttempt("dur.a", At(5), anchor.AddMinutes(-20), TimeSpan.FromMinutes(20));
            SeedSuccessAttempt("dur.a", At(10), anchor.AddMinutes(-10), TimeSpan.FromMinutes(6));
            SeedFailedAttempt("dur.a", At(15), anchor, TimeSpan.FromMinutes(99));
            SeedSuccessAttempt("dur.a", At(20), DateTimeOffset.UtcNow.AddDays(-30), TimeSpan.FromMinutes(1));

            // Job B: durations 8, 12 min -> even-count median = 10 min.
            SeedSuccessAttempt("dur.b", At(0), anchor.AddMinutes(-15), TimeSpan.FromMinutes(8));
            SeedSuccessAttempt("dur.b", At(5), anchor.AddMinutes(-5), TimeSpan.FromMinutes(12));

            var jobIds = new[] { JobId("dur.a"), JobId("dur.b"), JobId("dur.none") };
            var result = diagnostics.GetTypicalSuccessfulDurationsByJob(jobIds, since, perJobSampleCap: 50);

            Assert.Equal(TimeSpan.FromMinutes(6), result[JobId("dur.a")]);
            Assert.Equal(TimeSpan.FromMinutes(10), result[JobId("dur.b")]);
            Assert.False(result.ContainsKey(JobId("dur.none")));
        }

        [Fact]
        public void GetTypicalSuccessfulDurationsByJob_caps_to_newest_samples_per_job()
        {
            catalog.Create(Schedule("dur.cap"));
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var anchor = DateTimeOffset.UtcNow.AddHours(-1);
            SeedSuccessAttempt("dur.cap", At(0), anchor.AddMinutes(-30), TimeSpan.FromMinutes(30));
            SeedSuccessAttempt("dur.cap", At(5), anchor.AddMinutes(-5), TimeSpan.FromMinutes(4));

            var jobIds = new[] { JobId("dur.cap") };

            // cap=1 keeps only the newest success (4 min); the full set medians 4 and 30 -> 17 min.
            Assert.Equal(TimeSpan.FromMinutes(4), diagnostics.GetTypicalSuccessfulDurationsByJob(jobIds, since, perJobSampleCap: 1)[JobId("dur.cap")]);
            Assert.Equal(TimeSpan.FromMinutes(17), diagnostics.GetTypicalSuccessfulDurationsByJob(jobIds, since, perJobSampleCap: 50)[JobId("dur.cap")]);
        }

        [Fact]
        public void GetTypicalCompletedSliceDurationsByJob_measures_whole_chunked_window_with_retry()
        {
            catalog.Create(Schedule("dur.chunks", chunks: 2, maxParallelism: 2));
            var jobId = JobId("dur.chunks");
            var start = At(0);
            var end = At(5);
            var firstStarted = DateTimeOffset.UtcNow.AddHours(-1);
            state.Append("dur-chunks-completed", jobId, start, end, DurableSliceStatus.Completed, expectedVersion: 0);
            observability.RecordAttempt("chunk-0-success", jobId, start, end, 1, "Succeeded", "worker-0", firstStarted, firstStarted.AddMinutes(4), chunkId: 0, totalChunks: 2);
            observability.RecordAttempt("chunk-1-failed", jobId, start, end, 1, "FailedRetryable", "worker-1", firstStarted.AddMinutes(1), firstStarted.AddMinutes(3), chunkId: 1, totalChunks: 2);
            observability.RecordAttempt("chunk-1-success", jobId, start, end, 2, "Succeeded", "worker-1", firstStarted.AddMinutes(5), firstStarted.AddMinutes(10), chunkId: 1, totalChunks: 2);

            var result = diagnostics.GetTypicalCompletedSliceDurationsByJob(
                [jobId],
                DateTimeOffset.UtcNow.AddDays(-1),
                perJobSampleCap: 50);

            Assert.Equal(TimeSpan.FromMinutes(10), result[jobId]);
        }

        [Fact]
        public void GetTypicalCompletedSliceDurationsByJob_caps_candidates_before_loading_attempt_histories()
        {
            catalog.Create(Schedule("dur.logical.cap"));
            var since = DateTimeOffset.UtcNow.AddDays(-1);
            var anchor = DateTimeOffset.UtcNow.AddHours(-1);
            SeedSuccessAttempt("dur.logical.cap", At(0), anchor.AddMinutes(-30), TimeSpan.FromMinutes(30));
            SeedSuccessAttempt("dur.logical.cap", At(5), anchor.AddMinutes(-5), TimeSpan.FromMinutes(4));
            var jobId = JobId("dur.logical.cap");

            Assert.Equal(
                TimeSpan.FromMinutes(4),
                diagnostics.GetTypicalCompletedSliceDurationsByJob([jobId], since, perJobSampleCap: 1)[jobId]);
            Assert.Equal(
                TimeSpan.FromMinutes(17),
                diagnostics.GetTypicalCompletedSliceDurationsByJob([jobId], since, perJobSampleCap: 50)[jobId]);
        }

        [Fact]
        public void GetThroughputSeries_groups_by_job_when_requested()
        {
            catalog.Create(Schedule("tp.a"));
            catalog.Create(Schedule("tp.b"));
            var bucketAnchor = DateTimeOffset.UtcNow.AddMinutes(-20);
            Complete("tp.a", At(0), At(5), "a-1", bucketAnchor);
            Complete("tp.a", At(5), At(10), "a-2", bucketAnchor.AddMinutes(1));
            Complete("tp.b", At(0), At(5), "b-1", bucketAnchor.AddMinutes(2));

            var from = DateTimeOffset.UtcNow.AddHours(-1);
            var to = DateTimeOffset.UtcNow.AddMinutes(1);

            var combined = diagnostics.GetThroughputSeries(jobId: null, from, to, bucketSeconds: 3600, groupByJob: false, take: 100);
            Assert.Equal(3, combined.Sum(b => b.SucceededCount));
            Assert.All(combined, b => Assert.Null(b.JobId));

            var grouped = diagnostics.GetThroughputSeries(jobId: null, from, to, bucketSeconds: 3600, groupByJob: true, take: 100);
            Assert.Equal(2, grouped.GroupBy(b => b.JobId).Count());
            Assert.Equal(2, grouped.Where(b => b.JobId == JobId("tp.a")).Sum(b => b.SucceededCount));
            Assert.Equal(1, grouped.Where(b => b.JobId == JobId("tp.b")).Sum(b => b.SucceededCount));
        }

        [Fact]
        public void GetSlices_filters_by_state_and_slice_window()
        {
            catalog.Create(Schedule("sl.job"));
            state.Append("done-0", JobId("sl.job"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("block-5", JobId("sl.job"), At(5), At(10), DurableSliceStatus.DependencyBlocked, expectedVersion: 0);
            state.Append("done-10", JobId("sl.job"), At(10), At(15), DurableSliceStatus.Completed, expectedVersion: 0);

            var completed = diagnostics.GetSlices(JobId("sl.job"), state: "Completed", fromUtc: null, toUtc: null, DateTimeOffset.UtcNow, take: 100);
            Assert.Equal(2, completed.Count);
            Assert.All(completed, s => Assert.Equal("Completed", s.State));

            var windowed = diagnostics.GetSlices(JobId("sl.job"), state: null, fromUtc: At(5), toUtc: At(11), DateTimeOffset.UtcNow, take: 100);
            Assert.Equal(2, windowed.Count);
            Assert.DoesNotContain(windowed, s => s.SliceStartUtc == At(0));
        }

        [Fact]
        public void GetLogsPage_uses_timestamp_and_log_id_keyset()
        {
            catalog.Create(Schedule("logs.page"));
            var jobId = JobId("logs.page");
            const string timestamp = "2026-01-01T00:00:00.0000000Z";
            foreach (var logId in new[] { "log-c", "log-b", "log-a" })
            {
                using var connection = factory.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO operational_logs
                        (log_id,job_id,level,message,properties_json,recorded_at_utc)
                    VALUES ($id,$job,'Information',$id,'{}',$time);
                    """;
                command.Parameters.AddWithValue("$id", logId);
                command.Parameters.AddWithValue("$job", jobId);
                command.Parameters.AddWithValue("$time", timestamp);
                command.ExecuteNonQuery();
            }

            var first = diagnostics.GetLogsPage(
                jobId,
                level: null,
                category: null,
                fromUtc: null,
                toUtc: null,
                cursorRecordedAtUtc: null,
                cursorId: null,
                take: 2);
            var second = diagnostics.GetLogsPage(
                jobId,
                level: null,
                category: null,
                fromUtc: null,
                toUtc: null,
                cursorRecordedAtUtc: first[^1].RecordedAtUtc,
                cursorId: first[^1].LogId,
                take: 2);

            Assert.Equal(new[] { "log-c", "log-b" }, first.Select(item => item.LogId));
            Assert.Equal("log-a", Assert.Single(second).LogId);
        }

        [Fact]
        public void GetThroughputSeries_keeps_most_recent_buckets_when_capped()
        {
            catalog.Create(Schedule("tp.cap"));
            Complete("tp.cap", At(0), At(5), "old-1", DateTimeOffset.UtcNow.AddHours(-3));
            Complete("tp.cap", At(5), At(10), "new-1", DateTimeOffset.UtcNow.AddMinutes(-30));

            var from = DateTimeOffset.UtcNow.AddHours(-6);
            var to = DateTimeOffset.UtcNow.AddMinutes(1);
            var capped = diagnostics.GetThroughputSeries(jobId: null, from, to, bucketSeconds: 3600, groupByJob: false, take: 1);

            // With two hourly buckets and take=1, the retained bucket must be the most recent one, so a
            // capped series never looks like "completions stopped" at the recent end. (Hour buckets are
            // epoch-aligned, so the -30m completion lands in a bucket up to ~90m old; -2h cleanly
            // separates it from the -3h bucket.)
            var bucket = Assert.Single(capped);
            Assert.True(bucket.BucketStartUtc >= DateTimeOffset.UtcNow.AddHours(-2));
        }

        private void Complete(string activityId, DateTimeOffset start, DateTimeOffset end, string attemptId, DateTimeOffset completedAtUtc)
        {
            state.Append($"done-{attemptId}", JobId(activityId), start, end, DurableSliceStatus.Completed, expectedVersion: 0);
            observability.RecordAttempt(attemptId, JobId(activityId), start, end, 1, "Succeeded", "worker", completedAtUtc.AddMinutes(-1), completedAtUtc);
        }

        private void SeedSuccessAttempt(string activityId, DateTimeOffset start, DateTimeOffset completedAt, TimeSpan duration)
        {
            var end = start.AddMinutes(5);
            state.Append($"ok-{activityId}-{start.Ticks}", JobId(activityId), start, end, DurableSliceStatus.Completed, expectedVersion: 0);
            observability.RecordAttempt($"att-{activityId}-{start.Ticks}", JobId(activityId), start, end, 1, "Succeeded", "worker", completedAt - duration, completedAt);
        }

        private void SeedFailedAttempt(string activityId, DateTimeOffset start, DateTimeOffset completedAt, TimeSpan duration)
        {
            var end = start.AddMinutes(5);
            state.Append($"fail-{activityId}-{start.Ticks}", JobId(activityId), start, end, DurableSliceStatus.Failed, expectedVersion: 0);
            observability.RecordAttempt($"attf-{activityId}-{start.Ticks}", JobId(activityId), start, end, 1, "Failed", "worker", completedAt - duration, completedAt);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, int? chunks = null, int maxParallelism = 1) =>
            "{\n" +
            $"  \"id\": \"{JobId(activityId)}\",\n" +
            $"  \"activityId\": \"{activityId}\",\n" +
            "  \"functionName\": \"DiagFunction\",\n" +
            "  \"outputTable\": \"Output\",\n" +
            "  \"queryWindowSize\": \"00:05:00\",\n" +
            "  \"delayFromUtcNow\": \"00:00:00\",\n" +
            $"  \"maxParallelism\": {maxParallelism},\n" +
            "  \"queryTimeout\": \"00:01:00\",\n" +
            (chunks is null ? string.Empty : $"  \"chunks\": {chunks},\n") +
            "  \"isPaused\": false,\n" +
            "  \"startFrom\": \"2026-01-01T00:00:00Z\",\n" +
            "  \"target\": { \"clusterUri\": \"https://ksr-example.invalid\", \"database\": \"DemoDb\" }\n" +
            "}";
    }
}
