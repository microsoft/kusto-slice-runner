using KoLite.Local.Core.Time;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Tests
{
    public sealed class ActivityQueryTests : IDisposable
    {
        private const string Cluster = "https://kolite-activity.invalid";

        // Fixed "now" well past the 2026-01-01 slice base so trailing 1d/7d/30d windows are positive.
        private static readonly DateTimeOffset Now = At(60 * 24 * 60);

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "activity-query-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteChunkStateRepository chunkState;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;

        public ActivityQueryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "activity.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            chunkState = new SqliteChunkStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
            diagnostics = new SqliteDiagnosticsReadModelRepository(factory);
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }

        [Fact]
        public void Aggregates_running_all_time_windowed_and_chart_counts()
        {
            var jobId = catalog.Create(Schedule("activity.job")).JobId;

            // All-time outcome per slice (from current_slice_state, never pruned).
            SeedSlice(jobId, At(0), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(5), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(10), DurableSliceStatus.Failed);
            SeedSlice(jobId, At(15), DurableSliceStatus.DeadLettered);
            SeedSlice(jobId, At(20), DurableSliceStatus.Running);
            SeedSlice(jobId, At(25), DurableSliceStatus.Queued);
            // A completed slice whose only success attempt is older than 30 days: it counts toward the
            // all-time total but must be excluded from the attempt-based 1d/7d/30d windows and chart.
            SeedSlice(jobId, At(30), DurableSliceStatus.Completed);

            // Recent attempt completions (within the last day) drive the windows and the chart.
            RecordAttempt(jobId, At(0), "Succeeded", Now.AddMinutes(-5));
            RecordAttempt(jobId, At(5), "Succeeded", Now.AddMinutes(-10));
            RecordAttempt(jobId, At(10), "Failed", Now.AddMinutes(-15));
            RecordAttempt(jobId, At(15), "DeadLettered", Now.AddMinutes(-20));
            RecordAttempt(jobId, At(30), "Succeeded", Now.AddDays(-40));

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.RunningNow.RunningCount);
            Assert.Equal(1, data.RunningNow.QueuedCount);
            Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal("activity.job", data.RunningNow.RunningSlices[0].Slice.ActivityId);
            // No in-flight Started attempt was recorded, so the start falls back to the last state change.
            Assert.NotEqual(default, data.RunningNow.RunningSlices[0].StartedAtUtc);

            // All-time: Completed = 3 (includes the 40-day-old one); Failed + DeadLettered = 2.
            Assert.Equal(3, data.AllTime.Succeeded);
            Assert.Equal(2, data.AllTime.Failed);

            // Windows are attempt-based and exclude the 40-day-old success.
            Assert.Equal(2, data.LastDay.Succeeded);
            Assert.Equal(2, data.LastDay.Failed);
            Assert.Equal(2, data.Last7Days.Succeeded);
            Assert.Equal(2, data.Last7Days.Failed);
            Assert.Equal(2, data.Last30Days.Succeeded);
            Assert.Equal(2, data.Last30Days.Failed);

            Assert.True(data.Chart.HasData);
            Assert.Equal(2, data.Chart.Points.Sum(p => p.SucceededCount));
            Assert.Equal(2, data.Chart.Points.Sum(p => p.FailedCount));
        }

        [Fact]
        public void Reports_zeroes_for_an_empty_store()
        {
            catalog.Create(Schedule("activity.empty"));

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(0, data.RunningNow.RunningCount);
            Assert.Equal(0, data.RunningNow.QueuedCount);
            Assert.Empty(data.RunningNow.RunningSlices);
            Assert.Equal(0, data.AllTime.Total);
            Assert.Equal(0, data.LastDay.Total);
            Assert.Equal(0, data.Last7Days.Total);
            Assert.Equal(0, data.Last30Days.Total);
            Assert.False(data.Chart.HasData);
        }

        [Fact]
        public void Running_slices_expose_start_time_and_median_eta()
        {
            var jobId = catalog.Create(Schedule("eta.job")).JobId;

            // Prior successful runs (each needs a slice-state row for the attempt foreign key).
            // Durations 6, 20, 10 minutes -> median 10 minutes.
            SeedSlice(jobId, At(0), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(5), DurableSliceStatus.Completed);
            SeedSlice(jobId, At(10), DurableSliceStatus.Completed);
            RecordSucceeded(jobId, At(0), Now.AddHours(-3), TimeSpan.FromMinutes(6));
            RecordSucceeded(jobId, At(5), Now.AddHours(-2), TimeSpan.FromMinutes(20));
            RecordSucceeded(jobId, At(10), Now.AddHours(-1), TimeSpan.FromMinutes(10));

            // The in-flight slice with a Started attempt.
            var runningStart = At(100);
            SeedSlice(jobId, runningStart, DurableSliceStatus.Running);
            var startedAt = Now.AddMinutes(-4);
            RecordStarted(jobId, runningStart, startedAt);

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(startedAt, view.StartedAtUtc);
            Assert.Equal(startedAt + TimeSpan.FromMinutes(10), view.EtaUtc);
            Assert.Equal(Now, data.GeneratedAtUtc);
        }

        [Fact]
        public void Running_slice_without_successful_history_has_no_eta()
        {
            var jobId = catalog.Create(Schedule("eta.nohistory")).JobId;
            var runningStart = At(100);
            SeedSlice(jobId, runningStart, DurableSliceStatus.Running);
            var startedAt = Now.AddMinutes(-2);
            RecordStarted(jobId, runningStart, startedAt);

            var query = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics);
            var data = query.GetActivity(TimeSpan.FromDays(1));

            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(startedAt, view.StartedAtUtc);
            Assert.Null(view.EtaUtc);
        }

        [Fact]
        public void Chunked_running_window_counts_executions_and_lists_every_chunk_worker()
        {
            var job = catalog.Create(Schedule("activity.chunks.running", chunks: 4, maxParallelism: 4));
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 4, "test");
            var starts = new[]
            {
                Now.AddMinutes(-10),
                Now.AddMinutes(-9),
                Now.AddMinutes(-8),
                Now.AddMinutes(-7)
            };
            foreach (var child in children)
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                queue.Enqueue(
                    job.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    $"normal|{child.Execution.ExecutionKey}",
                    Now.AddHours(-1),
                    chunkId: child.ChunkId,
                    totalChunks: child.TotalChunks);
                var workerId = $"worker-{child.ChunkId}";
                var work = queue.Claim("default", workerId, TimeSpan.FromMinutes(30), Now)!;
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, workerId, TimeSpan.FromMinutes(30), Now)!;
                Assert.NotNull(lease);
                readModels.RecordAttempt(
                    $"{work.QueueItemId}:1",
                    job.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    1,
                    "Started",
                    workerId,
                    starts[child.ChunkId],
                    completedAtUtc: null,
                    chunkId: child.ChunkId,
                    totalChunks: child.TotalChunks);
            }

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.RunningNow.RunningCount);
            Assert.Equal(0, data.RunningNow.QueuedCount);
            Assert.Equal(4, data.RunningNow.RunningExecutionCount);
            Assert.Equal(0, data.RunningNow.QueuedExecutionCount);
            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(4, view.TotalChunks);
            Assert.Equal(0, view.CompletedChunks);
            Assert.Equal(4, view.RunningChunks);
            Assert.Equal(starts[0], view.StartedAtUtc);
            Assert.Equal([0, 1, 2, 3], view.RunningExecutions.Select(execution => execution.ChunkId).ToArray());
            Assert.Equal(["worker-0", "worker-1", "worker-2", "worker-3"], view.RunningExecutions.Select(execution => execution.WorkerId!).ToArray());
        }

        [Fact]
        public void Chunked_running_window_reports_mixed_progress_and_queued_execution_units()
        {
            var job = catalog.Create(Schedule("activity.chunks.mixed", chunks: 16, maxParallelism: 8));
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 16, "test");
            for (var chunkId = 0; chunkId < 3; chunkId++)
            {
                CompleteChunk(children[chunkId], $"complete-{chunkId}");
            }

            for (var chunkId = 3; chunkId < 5; chunkId++)
            {
                var child = children[chunkId];
                var worker = $"running-worker-{chunkId}";
                chunkState.MarkQueued($"running-{chunkId}-queued", child.Execution, actor: "test");
                chunkState.AcquireLease($"running-{chunkId}-lease", child.Execution, worker, TimeSpan.FromMinutes(30), Now);
                readModels.RecordAttempt(
                    $"running-{chunkId}-attempt",
                    job.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    1,
                    "Started",
                    worker,
                    Now.AddMinutes(-chunkId),
                    completedAtUtc: null,
                    chunkId: child.ChunkId,
                    totalChunks: child.TotalChunks);
            }

            for (var chunkId = 5; chunkId < 9; chunkId++)
            {
                var child = children[chunkId];
                chunkState.MarkQueued($"queued-{chunkId}", child.Execution, actor: "test");
                queue.Enqueue(
                    job.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    $"normal|{child.Execution.ExecutionKey}",
                    Now.AddMinutes(5),
                    chunkId: child.ChunkId,
                    totalChunks: child.TotalChunks);
            }

            MakeTerminal(children[9], DurableSliceStatus.DeadLettered, "dead");
            MakeTerminal(children[10], DurableSliceStatus.Failed, "failed");

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.RunningNow.RunningCount);
            Assert.Equal(2, data.RunningNow.RunningExecutionCount);
            Assert.Equal(4, data.RunningNow.QueuedExecutionCount);
            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(3, view.CompletedChunks);
            Assert.Equal(2, view.RunningChunks);
            Assert.Equal(4, view.QueuedChunks);
            Assert.Equal(1, view.FailedChunks);
            Assert.Equal(1, view.DeadLetteredChunks);
            Assert.Equal(5, view.MissingChunks);
            Assert.Equal([3, 4], view.RunningExecutions.Select(execution => execution.ChunkId).ToArray());
        }

        [Fact]
        public void Chunked_eta_uses_earliest_active_start_and_recent_whole_window_duration()
        {
            var job = catalog.Create(Schedule("activity.chunks.eta", chunks: 2, maxParallelism: 2));
            var historicalStart = At(0);
            var historicalEnd = At(5);
            var historicalFirstStart = Now.AddHours(-2);
            state.Append("historical-completed", job.JobId, historicalStart, historicalEnd, DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("historical-0", job.JobId, historicalStart, historicalEnd, 1, "Succeeded", "worker-0", historicalFirstStart, historicalFirstStart.AddMinutes(4), chunkId: 0, totalChunks: 2);
            readModels.RecordAttempt("historical-1-failed", job.JobId, historicalStart, historicalEnd, 1, "FailedRetryable", "worker-1", historicalFirstStart.AddMinutes(1), historicalFirstStart.AddMinutes(3), chunkId: 1, totalChunks: 2);
            readModels.RecordAttempt("historical-1-success", job.JobId, historicalStart, historicalEnd, 2, "Succeeded", "worker-1", historicalFirstStart.AddMinutes(5), historicalFirstStart.AddMinutes(10), chunkId: 1, totalChunks: 2);

            var current = new SliceRange(job.JobId, At(100), At(105));
            var currentChildren = chunkState.EnsureWindow(current, 2, "test");
            var activeStarts = new[] { Now.AddMinutes(-4), Now.AddMinutes(-3) };
            foreach (var child in currentChildren)
            {
                var worker = $"current-worker-{child.ChunkId}";
                chunkState.MarkQueued($"current-{child.ChunkId}-queued", child.Execution, actor: "test");
                chunkState.AcquireLease($"current-{child.ChunkId}-lease", child.Execution, worker, TimeSpan.FromMinutes(30), Now);
                readModels.RecordAttempt(
                    $"current-{child.ChunkId}",
                    job.JobId,
                    current.StartUtc,
                    current.EndUtc,
                    1,
                    "Started",
                    worker,
                    activeStarts[child.ChunkId],
                    completedAtUtc: null,
                    chunkId: child.ChunkId,
                    totalChunks: child.TotalChunks);
            }

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Equal(activeStarts[0], view.StartedAtUtc);
            Assert.Equal(activeStarts[0].AddMinutes(10), view.EtaUtc);
        }

        [Fact]
        public void Sixteen_successful_chunks_count_as_one_processed_logical_slice()
        {
            var job = catalog.Create(Schedule("activity.chunks.processed", chunks: 16, maxParallelism: 16));
            var start = At(0);
            var end = At(5);
            state.Append("chunked-logical-completed", job.JobId, start, end, DurableSliceStatus.Completed, expectedVersion: 0);
            for (var chunkId = 0; chunkId < 16; chunkId++)
            {
                var completedAt = Now.AddMinutes(-20 + chunkId);
                readModels.RecordAttempt(
                    $"processed-{chunkId}",
                    job.JobId,
                    start,
                    end,
                    1,
                    "Succeeded",
                    $"worker-{chunkId}",
                    completedAt.AddMinutes(-1),
                    completedAt,
                    chunkId: chunkId,
                    totalChunks: 16);
            }

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.AllTime.Succeeded);
            Assert.Equal(1, data.LastDay.Succeeded);
            Assert.Equal(1, data.Last7Days.Succeeded);
            Assert.Equal(1, data.Last30Days.Succeeded);
            Assert.Equal(1, data.Chart.Points.Sum(point => point.SucceededCount));
        }

        [Fact]
        public void Unchunked_running_slice_remains_one_execution_with_its_worker()
        {
            var job = catalog.Create(Schedule("activity.unchunked.worker"));
            var startedAt = Now.AddMinutes(-3);
            state.AcquireLease("unchunked-lease", job.JobId, At(0), At(5), "unchunked-worker", TimeSpan.FromMinutes(30), Now);
            readModels.RecordAttempt("unchunked-started", job.JobId, At(0), At(5), 1, "Started", "unchunked-worker", startedAt, completedAtUtc: null);

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.RunningNow.RunningExecutionCount);
            var view = Assert.Single(data.RunningNow.RunningSlices);
            Assert.Null(view.TotalChunks);
            var execution = Assert.Single(view.RunningExecutions);
            Assert.Null(execution.ChunkId);
            Assert.Equal("unchunked-worker", execution.WorkerId);
            Assert.Equal(startedAt, execution.StartedAtUtc);
        }

        [Fact]
        public void Expired_running_chunk_lease_is_visible_in_Activity_execution_details()
        {
            var job = catalog.Create(Schedule("activity.chunk.orphan", chunks: 2, maxParallelism: 2));
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var child = chunkState.EnsureWindow(slice, 2, "test")[0];
            chunkState.MarkQueued("orphan-queued", child.Execution, actor: "test");
            chunkState.AcquireLease("orphan-lease", child.Execution, "orphan-worker", TimeSpan.FromMinutes(5), Now.AddMinutes(-10));

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            var execution = Assert.Single(Assert.Single(data.RunningNow.RunningSlices).RunningExecutions);
            Assert.Equal(0, execution.ChunkId);
            Assert.Equal("orphan-worker", execution.WorkerId);
            Assert.True(execution.LeaseExpired);
        }

        [Fact]
        public void Paused_job_keeps_its_durable_queued_execution_counted()
        {
            var job = catalog.Create(Schedule("activity.chunk.paused", chunks: 2, maxParallelism: 2));
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var child = chunkState.EnsureWindow(slice, 2, "test")[0];
            chunkState.MarkQueued("paused-queued", child.Execution, actor: "test");
            queue.Enqueue(
                job.JobId,
                slice.StartUtc,
                slice.EndUtc,
                $"normal|{child.Execution.ExecutionKey}",
                Now,
                chunkId: child.ChunkId,
                totalChunks: child.TotalChunks);
            catalog.SetEnabled(job.JobId, enabled: false, expectedVersion: job.CatalogVersion);

            var data = new ActivityQuery(factory, new ManualClock(Now), readModels, diagnostics)
                .GetActivity(TimeSpan.FromDays(1));

            Assert.Equal(1, data.RunningNow.QueuedCount);
            Assert.Equal(1, data.RunningNow.QueuedExecutionCount);
            Assert.Empty(data.RunningNow.RunningSlices);
        }

        private void SeedSlice(string jobId, DateTimeOffset sliceStart, DurableSliceStatus status)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            state.Append($"{jobId}-st-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, status, expectedVersion: 0);
        }

        private void CompleteChunk(DurableChunkState child, string prefix)
        {
            chunkState.MarkQueued($"{prefix}-queued", child.Execution, actor: "test");
            var lease = chunkState.AcquireLease($"{prefix}-lease", child.Execution, prefix, TimeSpan.FromMinutes(30), Now)!;
            Assert.True(chunkState.CompleteLease($"{prefix}-complete", child.Execution, prefix, lease.LeaseToken!, Now.AddMinutes(1)));
        }

        private void MakeTerminal(DurableChunkState child, DurableSliceStatus status, string prefix)
        {
            chunkState.MarkQueued($"{prefix}-queued", child.Execution, actor: "test");
            var lease = chunkState.AcquireLease($"{prefix}-lease", child.Execution, prefix, TimeSpan.FromMinutes(30), Now)!;
            if (status == DurableSliceStatus.DeadLettered)
            {
                Assert.True(chunkState.DeadLetterLease($"{prefix}-terminal", child.Execution, prefix, lease.LeaseToken!, Now.AddMinutes(1), prefix, prefix));
            }
            else
            {
                Assert.True(chunkState.FailLease($"{prefix}-terminal", child.Execution, prefix, lease.LeaseToken!, Now.AddMinutes(1), prefix, prefix));
            }
        }

        private void RecordAttempt(string jobId, DateTimeOffset sliceStart, string status, DateTimeOffset completedAt)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, status, "worker", completedAt.AddMinutes(-1), completedAt);
        }

        // In-flight attempt: started, not yet completed (drives the running slice's StartedAtUtc).
        private void RecordStarted(string jobId, DateTimeOffset sliceStart, DateTimeOffset startedAt)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, "Started", "worker", startedAt, completedAtUtc: null);
        }

        // A completed successful attempt with an explicit wall-clock duration (drives the ETA median).
        private void RecordSucceeded(string jobId, DateTimeOffset sliceStart, DateTimeOffset completedAt, TimeSpan duration)
        {
            var sliceEnd = sliceStart.AddMinutes(5);
            readModels.RecordAttempt($"{jobId}-att-{sliceStart.Ticks}", jobId, sliceStart, sliceEnd, 1, "Succeeded", "worker", completedAt - duration, completedAt);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, int? chunks = null, int maxParallelism = 1) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "ActivityFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": {{maxParallelism}},
          "queryTimeout": "00:01:00",
          {{(chunks is null ? string.Empty : $"\"chunks\": {chunks},")}}
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "{{Cluster}}", "database": "DemoDb" }
        }
        """;
    }
}
