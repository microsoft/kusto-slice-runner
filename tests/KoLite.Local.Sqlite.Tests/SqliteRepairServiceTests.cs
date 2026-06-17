using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Repair;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteRepairServiceTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "repair-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly ManualClock clock = new(At(20));

        public SqliteRepairServiceTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "repair.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(factory).Migrate();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
        }

        [Fact]
        public void Creates_repair_batch_for_missing_and_failed_slices_with_idempotent_enqueue()
        {
            catalog.Create(Schedule("job.repair"));
            state.Append("failed", JobId("job.repair"), At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            var service = Service();
            var first = service.PlanAndEnqueue(new RepairPlanRequest(JobId("job.repair"), At(0), At(10), "tester", "retry failed output"));
            var second = service.PlanAndEnqueue(new RepairPlanRequest(JobId("job.repair"), At(0), At(10), "tester", "retry failed output"));

            Assert.Equal(first.RepairBatchId, second.RepairBatchId);
            Assert.Equal(2, first.Queued);
            Assert.Equal(2, queue.List(JobId("job.repair")).Count);
            Assert.All(queue.List(JobId("job.repair")), item => Assert.StartsWith("repair|", item.IdempotencyKey, StringComparison.Ordinal));
            Assert.Contains(service.GetRepairSlices(first.RepairBatchId), s => s.Status == RepairSliceStatus.Queued && s.Slice.StartUtc == At(0));
            Assert.Contains(service.GetRepairSlices(first.RepairBatchId), s => s.Status == RepairSliceStatus.Queued && s.Slice.StartUtc == At(5));
        }

        [Fact]
        public void Dependency_blocked_repair_slice_is_persisted_without_queue_row()
        {
            catalog.Create(Schedule("upstream"));
            catalog.Create(Schedule("downstream", dependsOn: "upstream"));

            var result = Service().PlanAndEnqueue(new RepairPlanRequest(JobId("downstream"), At(0), At(5), "tester", "try blocked repair"));

            Assert.Equal(0, result.Queued);
            Assert.Equal(1, result.Blocked);
            Assert.Empty(queue.List(JobId("downstream")));
            Assert.Single(Service().GetRepairSlices(result.RepairBatchId), s => s.Status == RepairSliceStatus.Blocked);
        }

        [Fact]
        public async Task Repair_work_uses_injected_worker_executor_and_repairs_original_slice_state()
        {
            catalog.Create(Schedule("job.execute"));
            state.Append("failed", JobId("job.execute"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            Service().PlanAndEnqueue(new RepairPlanRequest(JobId("job.execute"), At(0), At(5), "tester", "fake execute"));
            var executor = new RecordingExecutor();
            var worker = new SqliteLocalWorker(catalog, state, queue, readModels, executor, clock, new LocalWorkerOptions(WorkerId: "repair-worker"));

            var run = await worker.RunOnceAsync();

            Assert.True(run.Executed);
            Assert.True(run.Succeeded);
            Assert.Single(executor.Requests, r => r.JobId == JobId("job.execute") && r.StartUtc == At(0));
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.execute"), At(0), At(5)).Status);
            Assert.Equal(DurableWorkQueueState.Completed, queue.List(JobId("job.execute")).Single().State);
        }

        [Fact]
        public async Task Later_repair_of_same_slice_after_terminal_queue_item_enqueues_claimable_new_work()
        {
            catalog.Create(Schedule("job.rerepair"));
            state.Append("failed", JobId("job.rerepair"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            Service().PlanAndEnqueue(new RepairPlanRequest(JobId("job.rerepair"), At(0), At(5), "tester", "first repair"));
            var executor = new RecordingExecutor();
            var worker = new SqliteLocalWorker(catalog, state, queue, readModels, executor, clock, new LocalWorkerOptions(WorkerId: "repair-worker"));

            var firstRun = await worker.RunOnceAsync();
            var later = Service().PlanAndEnqueue(new RepairPlanRequest(JobId("job.rerepair"), At(0), At(5), "tester", "second repair"));
            var repeatedLater = Service().PlanAndEnqueue(new RepairPlanRequest(JobId("job.rerepair"), At(0), At(5), "tester", "second repair"));
            var secondRun = await worker.RunOnceAsync();

            Assert.True(firstRun.Succeeded);
            Assert.Equal(later.RepairBatchId, repeatedLater.RepairBatchId);
            Assert.Equal(2, queue.List(JobId("job.rerepair")).Count);
            Assert.Single(queue.List(JobId("job.rerepair")), item => item.State == DurableWorkQueueState.Completed && item.QueueItemId == firstRun.QueueItemId);
            Assert.True(secondRun.Executed);
            Assert.True(secondRun.Succeeded);
            Assert.Equal(2, executor.Requests.Count);
            Assert.All(queue.List(JobId("job.rerepair")), item => Assert.Equal(DurableWorkQueueState.Completed, item.State));
        }

        [Fact]
        public void Hard_delete_removes_childless_skipped_active_repair_batch_and_recreate_can_plan_again()
        {
            var created = catalog.Create(Schedule("job.childless"));
            state.Append("queued", JobId("job.childless"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0, reason: "scheduled");
            queue.Enqueue(JobId("job.childless"), At(0), At(5), "original-work", At(0));
            var request = new RepairPlanRequest(JobId("job.childless"), At(0), At(5), "tester", "same repair");

            var first = Service().PlanAndEnqueue(request);
            var firstRequestedAt = QueryString("SELECT requested_at_utc FROM repair_batches WHERE repair_batch_id=$id;", ("$id", first.RepairBatchId));

            Assert.Equal(0, first.Queued);
            Assert.Equal(1, first.Skipped);
            Assert.Empty(Service().GetRepairSlices(first.RepairBatchId));
            Assert.DoesNotContain(queue.List(JobId("job.childless")), item => item.IdempotencyKey.StartsWith("repair|", StringComparison.Ordinal));
            Assert.Equal(1, QueryInt($"SELECT COUNT(*) FROM repair_batches WHERE repair_batch_id=$id AND job_id='{JobId("job.childless")}';", ("$id", first.RepairBatchId)));

            var originalWork = queue.Claim("default", "purge-worker", TimeSpan.FromMinutes(5), At(1));
            Assert.NotNull(originalWork);
            new SqliteJobLifecycleService(factory, catalog).SoftDelete(JobId("job.childless"), created.CatalogVersion, "tester", "disable before purge");
            Assert.True(queue.Complete(originalWork.QueueItemId, "purge-worker"));

            var purge = new SqliteJobLifecycleService(factory, catalog).HardDelete(JobId("job.childless"), $"DELETE {JobId("job.childless")}", "tester", "purge childless repair batch");

            Assert.Equal(1, purge.DeletedRepairBatches);
            Assert.Equal(0, QueryInt($"SELECT COUNT(*) FROM repair_batches WHERE repair_batch_id=$id OR job_id='{JobId("job.childless")}';", ("$id", first.RepairBatchId)));
            Assert.Equal(0, QueryInt($"SELECT COUNT(*) FROM repair_slices WHERE job_id='{JobId("job.childless")}';"));

            clock.Advance(TimeSpan.FromMinutes(1));
            catalog.Create(Schedule("job.childless"));
            state.Append("queued-again", JobId("job.childless"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0, reason: "scheduled again");
            queue.Enqueue(JobId("job.childless"), At(0), At(5), "original-work-again", At(0));

            var second = Service().PlanAndEnqueue(request);
            var secondRequestedAt = QueryString("SELECT requested_at_utc FROM repair_batches WHERE repair_batch_id=$id;", ("$id", second.RepairBatchId));

            Assert.Equal(first.RepairBatchId, second.RepairBatchId);
            Assert.Equal(0, second.Queued);
            Assert.Equal(1, second.Skipped);
            Assert.Empty(Service().GetRepairSlices(second.RepairBatchId));
            Assert.NotEqual(firstRequestedAt, secondRequestedAt);
            Assert.Equal(1, QueryInt($"SELECT COUNT(*) FROM repair_batches WHERE repair_batch_id=$id AND job_id='{JobId("job.childless")}';", ("$id", second.RepairBatchId)));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        private SqliteRepairService Service() => new(factory, catalog, state, queue, clock);
        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private int QueryInt(string sql, params (string Name, object Value)[] parameters)
        {
            return Convert.ToInt32(QueryScalar(sql, parameters), System.Globalization.CultureInfo.InvariantCulture);
        }

        private string QueryString(string sql, params (string Name, object Value)[] parameters)
        {
            return Convert.ToString(QueryScalar(sql, parameters), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        private object? QueryScalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var c = factory.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                cmd.Parameters.AddWithValue(name, value);
            }

            return cmd.ExecuteScalar();
        }

        private static string Schedule(string activityId, string? dependsOn = null) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "RepairFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 10,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;

        private sealed class RecordingExecutor : ILocalSliceOutputExecutor
        {
            public List<SliceRange> Requests { get; } = [];
            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                Requests.Add(slice);
                return Task.FromResult(LocalSliceOutputResult.Success("test://repair"));
            }
        }
    }
}
