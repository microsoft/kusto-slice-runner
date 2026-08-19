using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Rerun;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Rerun;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteRerunServiceTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "rerun-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteChunkStateRepository chunkState;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly ManualClock clock = new(At(5));

        public SqliteRerunServiceTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "rerun.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            chunkState = new SqliteChunkStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
        }

        [Fact]
        public void Plan_expands_root_slice_to_transitive_downstream_and_generates_cleanup_commands()
        {
            catalog.Create(Schedule("root", "RootOutput"));
            catalog.Create(Schedule("downstream", "DownstreamOutput", dependsOn: "root"));
            state.Append("root-completed", JobId("root"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("down-completed", JobId("downstream"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);

            var plan = Service().Plan(new RerunPlanRequest(JobId("root"), At(0), At(5), "tester", "rerun bad data"));

            Assert.Equal(RerunBatchStatus.Planned, plan.Status);
            Assert.True(plan.CanExecute);
            Assert.Contains(plan.Slices, s => s.JobId == JobId("root") && s.Role == RerunSliceRole.Root && s.PreviousState == "Completed");
            Assert.Contains(plan.Slices, s => s.JobId == JobId("downstream") && s.Role == RerunSliceRole.Downstream && s.PreviousState == "Completed");
            Assert.Contains(".delete table RootOutput records <|", plan.KustoCleanupCommands);
            Assert.Contains(".delete table DownstreamOutput records <|", plan.KustoCleanupCommands);
            Assert.Contains("StartTime < datetime(2026-01-01T00:05:00.0000000Z)", plan.KustoCleanupCommands);
            Assert.Contains("EndTime > datetime(2026-01-01T00:00:00.0000000Z)", plan.KustoCleanupCommands);
        }

        [Fact]
        public void Plan_blocks_active_queued_or_running_slices()
        {
            catalog.Create(Schedule("active", "ActiveOutput"));
            state.Append("queued", JobId("active"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("active"), At(0), At(5), "active-work", At(0));

            var plan = Service().Plan(new RerunPlanRequest(JobId("active"), At(0), At(5), "tester", "rerun active"));

            Assert.Equal(RerunBatchStatus.Blocked, plan.Status);
            Assert.False(plan.CanExecute);
            Assert.Contains(plan.BlockedSlices, s => s.JobId == JobId("active") && s.BlockerReason is not null);
        }

        [Fact]
        public void Plan_blocks_chunked_slice_with_active_child_queue_work()
        {
            catalog.Create(Schedule("active-chunk", "ActiveChunkOutput", chunks: 2));
            var slice = new SliceRange(JobId("active-chunk"), At(0), At(5));
            var child = chunkState.EnsureWindow(slice, 2, "test")[0];
            chunkState.MarkQueued("queued-child", child.Execution, actor: "test");
            queue.Enqueue(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                $"normal|{child.Execution.ExecutionKey}",
                At(0),
                chunkId: child.ChunkId,
                totalChunks: child.TotalChunks);

            var plan = Service().Plan(new RerunPlanRequest(slice.JobId, slice.StartUtc, slice.EndUtc, "tester", "active child"));

            Assert.Equal(RerunBatchStatus.Blocked, plan.Status);
            Assert.Contains(plan.BlockedSlices, item => item.JobId == slice.JobId && item.BlockerReason!.Contains("Queued", StringComparison.Ordinal));
        }

        [Fact]
        public void Execute_requires_cleanup_acknowledgement()
        {
            catalog.Create(Schedule("ack", "AckOutput"));
            state.Append("completed", JobId("ack"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            var plan = Service().CreatePlan(new RerunPlanRequest(JobId("ack"), At(0), At(5), "tester", "rerun with cleanup"));

            var exception = Assert.Throws<InvalidOperationException>(() =>
                Service().Execute(new RerunExecuteRequest(plan.RerunBatchId, "tester", KustoCleanupAcknowledged: false)));
            Assert.Contains("Kusto cleanup", exception.Message);
        }

        [Fact]
        public async Task Execute_snapshots_old_details_resets_state_and_scheduler_rebuilds_through_dependencies()
        {
            catalog.Create(Schedule("root", "RootOutput", maxParallelism: 10));
            catalog.Create(Schedule("downstream", "DownstreamOutput", dependsOn: "root", maxParallelism: 10));
            state.Append("root-completed", JobId("root"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("down-completed", JobId("downstream"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            readModels.RecordAttempt("old-attempt-root", JobId("root"), At(0), At(5), 1, "Succeeded", "worker", At(0), At(1));
            readModels.RecordLog("Information", "old root log", "test", JobId("root"), At(0), At(5));
            readModels.RecordAttempt("old-attempt-down", JobId("downstream"), At(0), At(5), 1, "Succeeded", "worker", At(0), At(1));
            var plan = Service().CreatePlan(new RerunPlanRequest(JobId("root"), At(0), At(5), "tester", "bad source data"));

            var result = Service().Execute(new RerunExecuteRequest(plan.RerunBatchId, "tester", KustoCleanupAcknowledged: true));

            Assert.Equal(RerunBatchStatus.Completed, result.Status);
            Assert.Equal(2, result.ResetSlices);
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("root"), At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("downstream"), At(0), At(5)).Status);
            Assert.Empty(readModels.GetSliceStatus(JobId("root")));
            Assert.Empty(new OperationalDetailsReadModelShim(factory).GetAttempts(JobId("root"), At(0), At(5), 10));
            Assert.Equal(2, QueryInt("SELECT COUNT(*) FROM rerun_slices WHERE rerun_batch_id=$id AND status='Reset';", ("$id", plan.RerunBatchId)));
            Assert.Contains("old-attempt-root", QueryString($"SELECT snapshot_json FROM rerun_slices WHERE rerun_batch_id=$id AND job_id='{JobId("root")}';", ("$id", plan.RerunBatchId)));

            var scheduler = new SqliteLocalScheduler(catalog, state, queue, readModels, clock, new LocalSchedulerOptions(MaxSlicesPerTick: 10));
            var firstTick = scheduler.Tick();
            Assert.Equal(1, firstTick.Enqueued);
            Assert.Equal(1, firstTick.DependencyBlocked);

            var executor = new RecordingExecutor();
            var worker = new SqliteLocalWorker(catalog, state, queue, readModels, executor, clock, new LocalWorkerOptions(WorkerId: "rerun-worker"));
            Assert.True((await worker.RunOnceAsync()).Succeeded);

            var secondTick = scheduler.Tick();
            Assert.Equal(1, secondTick.Enqueued);
            Assert.True((await worker.RunOnceAsync()).Succeeded);
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("root"), At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("downstream"), At(0), At(5)).Status);
        }

        [Fact]
        public void Rerun_resets_every_chunk_in_the_logical_window()
        {
            catalog.Create(Schedule("chunked-root", "ChunkedOutput", maxParallelism: 2, chunks: 2));
            var slice = new SliceRange(JobId("chunked-root"), At(0), At(5));
            foreach (var child in chunkState.EnsureWindow(slice, 2, "test"))
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, "worker", TimeSpan.FromMinutes(5), At(1))!;
                Assert.True(chunkState.CompleteLease($"complete-{child.ChunkId}", child.Execution, "worker", lease.LeaseToken!, At(2)));
            }

            var plan = Service().CreatePlan(new RerunPlanRequest(JobId("chunked-root"), At(0), At(5), "tester", "recompute chunked output"));
            var result = Service().Execute(new RerunExecuteRequest(plan.RerunBatchId, "tester", KustoCleanupAcknowledged: true));

            Assert.Equal(1, result.ResetSlices);
            Assert.Empty(chunkState.List(slice));
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("chunked-root"), At(0), At(5)).Status);

            var scheduler = new SqliteLocalScheduler(catalog, state, queue, readModels, clock, new LocalSchedulerOptions(MaxSlicesPerTick: 10), chunkState);
            Assert.Equal(2, scheduler.Tick().Enqueued);
            Assert.Equal([0, 1], queue.List(JobId("chunked-root")).Select(item => item.ChunkId).Order().ToArray());
        }

        [Fact]
        public void Rerun_snapshot_preserves_mixed_chunk_state_and_events_before_reset()
        {
            catalog.Create(Schedule("chunked-mixed", "ChunkedMixedOutput", maxParallelism: 3, chunks: 3));
            var slice = new SliceRange(JobId("chunked-mixed"), At(0), At(5));
            foreach (var child in chunkState.EnsureWindow(slice, 3, "test"))
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", TimeSpan.FromMinutes(5), At(1))!;
                if (child.ChunkId == 0)
                {
                    chunkState.CompleteLease("complete-0", child.Execution, "worker-0", lease.LeaseToken!, At(2));
                }
                else if (child.ChunkId == 1)
                {
                    chunkState.DeadLetterLease("dead-1", child.Execution, "worker-1", lease.LeaseToken!, At(2), "permanent", "Permanent");
                }
                else
                {
                    chunkState.FailLease("failed-2", child.Execution, "worker-2", lease.LeaseToken!, At(2), "transient", "Transient");
                }
            }

            var plan = Service().CreatePlan(new RerunPlanRequest(slice.JobId, slice.StartUtc, slice.EndUtc, "tester", "archive mixed chunks"));
            Service().Execute(new RerunExecuteRequest(plan.RerunBatchId, "tester", KustoCleanupAcknowledged: true));

            var snapshot = QueryString(
                "SELECT snapshot_json FROM rerun_slices WHERE rerun_batch_id=$id AND job_id=$job;",
                ("$id", plan.RerunBatchId),
                ("$job", slice.JobId));
            using var document = System.Text.Json.JsonDocument.Parse(snapshot);
            var chunkStates = document.RootElement.GetProperty("chunkStates").EnumerateArray().ToArray();
            Assert.Equal(3, chunkStates.Length);
            Assert.Equal([0, 1, 2], chunkStates.Select(row => row.GetProperty("chunk_id").GetInt32()).Order().ToArray());
            var chunkEvents = document.RootElement.GetProperty("chunkEvents").EnumerateArray().ToArray();
            Assert.Contains(chunkEvents, row => row.GetProperty("chunk_id").GetInt32() == 1 && row.GetProperty("state").GetString() == "DeadLettered");
            Assert.Contains(chunkEvents, row => row.GetProperty("chunk_id").GetInt32() == 2 && row.GetProperty("state").GetString() == "Failed");
            Assert.Empty(chunkState.List(slice));
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private SqliteRerunService Service() => new(factory, catalog, clock);
        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private int QueryInt(string sql, params (string Name, object Value)[] parameters) =>
            Convert.ToInt32(QueryScalar(sql, parameters), System.Globalization.CultureInfo.InvariantCulture);

        private string QueryString(string sql, params (string Name, object Value)[] parameters) =>
            Convert.ToString(QueryScalar(sql, parameters), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

        private object? QueryScalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = factory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return command.ExecuteScalar();
        }

        private static string Schedule(string activityId, string outputTable, string? dependsOn = null, int maxParallelism = 1, int? chunks = null) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "RerunFunction",
          "outputTable": "{{outputTable}}",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": {{maxParallelism}},
          "queryTimeout": "00:01:00",
          {{(chunks is null ? string.Empty : $"\"chunks\": {chunks},")}}
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;

        private sealed class RecordingExecutor : ILocalSliceOutputExecutor
        {
            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default) =>
                Task.FromResult(LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}"));
        }

        private sealed class OperationalDetailsReadModelShim
        {
            private readonly KoLiteSqliteConnectionFactory factory;

            public OperationalDetailsReadModelShim(KoLiteSqliteConnectionFactory factory)
            {
                this.factory = factory;
            }

            public IReadOnlyList<string> GetAttempts(string jobId, DateTimeOffset start, DateTimeOffset end, int take)
            {
                using var connection = factory.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT attempt_id FROM slice_attempts WHERE job_id=$job AND slice_start_utc=$start AND slice_end_utc=$end LIMIT $take;";
                command.Parameters.AddWithValue("$job", jobId);
                command.Parameters.AddWithValue("$start", start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$end", end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$take", take);
                using var reader = command.ExecuteReader();
                var attempts = new List<string>();
                while (reader.Read()) attempts.Add(reader.GetString(0));
                return attempts;
            }
        }
    }
}
