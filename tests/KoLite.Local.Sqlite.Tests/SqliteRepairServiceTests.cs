using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Repair;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Schema;
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
            new KoLiteSqliteSchema(factory).EnsureSchema();
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

            var purge = new SqliteJobLifecycleService(factory, catalog).HardDelete(JobId("job.childless"), $"DELETE {created.DisplayName}", "tester", "purge childless repair batch");

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
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void Failed_only_scope_repairs_failures_and_leaves_completed_and_missing_slices_alone()
        {
            catalog.Create(Schedule("job.scope"));
            state.Append("failed", JobId("job.scope"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("dead", JobId("job.scope"), At(5), At(10), DurableSliceStatus.DeadLettered, expectedVersion: 0, reason: "gave up");
            state.Append("done", JobId("job.scope"), At(10), At(15), DurableSliceStatus.Completed, expectedVersion: 0);
            // At(15)-At(20) is left untouched, so it reads back as Missing.

            var result = Service().PlanAndEnqueue(new RepairPlanRequest(
                JobId("job.scope"), At(0), At(20), "tester", "rerun failures", Scope: RepairSliceScope.FailedAndDeadLetteredOnly));

            Assert.Equal(2, result.Queued);
            Assert.Equal(2, result.Skipped);
            Assert.Equal(0, result.Blocked);

            var queuedStarts = queue.List(JobId("job.scope")).Select(item => item.SliceStartUtc).ToArray();
            Assert.Equal([At(0), At(5)], queuedStarts.OrderBy(start => start).ToArray());

            // The out-of-scope slices keep their original state and record no repair_slices row.
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.scope"), At(10), At(15)).Status);
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("job.scope"), At(15), At(20)).Status);
            Assert.Equal(2, Service().GetRepairSlices(result.RepairBatchId).Count);
        }

        [Fact]
        public void Preview_reports_what_enqueue_would_do_without_writing_anything()
        {
            catalog.Create(Schedule("job.preview"));
            state.Append("failed", JobId("job.preview"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("done", JobId("job.preview"), At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);
            var request = new RepairPlanRequest(
                JobId("job.preview"), At(0), At(10), "tester", "preview only", Scope: RepairSliceScope.FailedAndDeadLetteredOnly);

            var preview = Service().Preview(request);

            Assert.Equal(1, preview.Repairable);
            Assert.Equal(1, preview.Skipped);
            Assert.Equal(0, preview.Blocked);
            var repairable = Assert.Single(preview.Slices, slice => slice.Outcome == RepairSliceOutcome.Repairable);
            Assert.Equal(At(0), repairable.StartUtc);
            Assert.Equal(DurableSliceStatus.Failed, repairable.CurrentStatus);

            // Nothing was persisted: no batch, no queue work, and the failed slice is still failed.
            Assert.Equal(0, QueryInt("SELECT COUNT(*) FROM repair_batches;"));
            Assert.Equal(0, QueryInt("SELECT COUNT(*) FROM repair_slices;"));
            Assert.Empty(queue.List(JobId("job.preview")));
            Assert.Equal(DurableSliceStatus.Failed, state.Get(JobId("job.preview"), At(0), At(5)).Status);

            // ...and the count it promised is what the enqueue actually does.
            Assert.Equal(preview.Repairable, Service().PlanAndEnqueue(request).Queued);
        }

        [Fact]
        public void Preview_reports_blocked_slices_for_an_unready_dependency()
        {
            catalog.Create(Schedule("upstream.preview"));
            catalog.Create(Schedule("downstream.preview", dependsOn: "upstream.preview"));
            state.Append("failed", JobId("downstream.preview"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            var preview = Service().Preview(new RepairPlanRequest(
                JobId("downstream.preview"), At(0), At(5), "tester", "blocked preview", Scope: RepairSliceScope.FailedAndDeadLetteredOnly));

            Assert.Equal(0, preview.Repairable);
            Assert.Equal(1, preview.Blocked);
            Assert.Single(preview.Slices, slice => slice.Outcome == RepairSliceOutcome.Blocked);
        }

        [Fact]
        public void Unaligned_repair_range_is_rejected_with_the_nearest_aligned_bounds()
        {
            var job = catalog.Create(Schedule("job.aligned")).Definition;

            // The job's window is 5 minutes anchored at At(0), so At(2)..At(7) sits off the grid.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SqliteRepairService.ValidateAlignedRange(job, At(2), At(7)));

            Assert.Contains("align", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("2026-01-01T00:00:00", ex.Message, StringComparison.Ordinal);
            Assert.Contains("2026-01-01T00:10:00", ex.Message, StringComparison.Ordinal);

            SqliteRepairService.ValidateAlignedRange(job, At(0), At(10));
        }

        [Fact]
        public void Repair_range_before_job_start_or_inverted_is_rejected()
        {
            var job = catalog.Create(Schedule("job.bounds")).Definition;

            Assert.Throws<InvalidOperationException>(() => SqliteRepairService.ValidateAlignedRange(job, At(10), At(5)));
            Assert.Throws<InvalidOperationException>(() => SqliteRepairService.ValidateAlignedRange(job, At(-10), At(5)));
        }

        [Fact]
        public void Oversized_repair_range_is_rejected_before_it_scans_the_store()
        {
            var job = catalog.Create(Schedule("job.huge")).Definition;
            var tooMany = At(0).AddMinutes(5 * (SqliteRepairService.MaxRepairSlices + 1));

            var ex = Assert.Throws<InvalidOperationException>(() => SqliteRepairService.ValidateAlignedRange(job, At(0), tooMany));

            Assert.Contains(SqliteRepairService.MaxRepairSlices.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
            SqliteRepairService.ValidateAlignedRange(job, At(0), At(5 * SqliteRepairService.MaxRepairSlices));
        }

        [Fact]
        public void Unaligned_range_at_the_end_of_time_reports_a_clean_error_instead_of_overflowing()
        {
            var job = catalog.Create(Schedule("job.maxdate")).Definition;

            // The aligned ceiling for a value this close to DateTimeOffset.MaxValue is not representable;
            // building the "nearest aligned range" hint must not throw while rejecting the range.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SqliteRepairService.ValidateAlignedRange(job, At(0), DateTimeOffset.MaxValue));

            Assert.Contains("align", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Slice_that_left_scope_between_preview_and_enqueue_is_skipped_without_losing_the_batch()
        {
            catalog.Create(Schedule("job.race"));
            state.Append("failed-a", JobId("job.race"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("failed-b", JobId("job.race"), At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            var service = Service();
            var request = new RepairPlanRequest(
                JobId("job.race"), At(0), At(10), "tester", "racing repair", Scope: RepairSliceScope.FailedAndDeadLetteredOnly);

            var preview = service.Preview(request);
            Assert.Equal(2, preview.Repairable);

            // A worker picks the second slice up between the preview and the enqueue. PlanAndEnqueue
            // re-classifies from live state, so that slice drops out - and the other one still repairs.
            state.Append("claimed", JobId("job.race"), At(5), At(10), DurableSliceStatus.Queued, expectedVersion: 1, reason: "claimed elsewhere");

            var result = service.PlanAndEnqueue(request);

            Assert.Equal(1, result.Queued);
            Assert.Equal(1, result.Skipped);
            var work = Assert.Single(queue.List(JobId("job.race")));
            Assert.Equal(At(0), work.SliceStartUtc);
        }

        [Fact]
        public void Enqueue_writes_an_audit_row_carrying_the_reason_and_counts()
        {
            catalog.Create(Schedule("job.audit"));
            state.Append("failed", JobId("job.audit"), At(0), At(5), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");

            var result = Service().PlanAndEnqueue(new RepairPlanRequest(
                JobId("job.audit"), At(0), At(5), "local-api", "closing a gap", Scope: RepairSliceScope.FailedAndDeadLetteredOnly));

            Assert.Equal(1, QueryInt(
                "SELECT COUNT(*) FROM system_audit WHERE action='RepairEnqueued' AND subject_type='RepairBatch' AND subject_id=$id AND actor='local-api';",
                ("$id", result.RepairBatchId)));
            var payload = QueryString("SELECT payload_json FROM system_audit WHERE subject_id=$id;", ("$id", result.RepairBatchId));
            Assert.Contains("closing a gap", payload, StringComparison.Ordinal);
            Assert.Contains("FailedAndDeadLetteredOnly", payload, StringComparison.Ordinal);
        }

        [Fact]
        public void Recover_orphaned_slice_requeues_an_expired_running_lease_for_enabled_job()
        {
            catalog.Create(Schedule("job.orphan"));
            // Running slice plus a Leased queue row whose locks expired at At(11) (clock is At(20)).
            state.AcquireLease("orphan-lease", JobId("job.orphan"), At(0), At(5), "stale-worker", TimeSpan.FromMinutes(1), At(10));
            queue.Enqueue(JobId("job.orphan"), At(0), At(5), "normal|orphan", At(0));
            Assert.NotNull(queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(1), At(10)));

            var recovered = Service().RecoverOrphanedSlice(JobId("job.orphan"), At(0), At(5), "operator", "manual recovery");

            Assert.True(recovered);
            var item = Assert.Single(queue.List(JobId("job.orphan")));
            Assert.Equal(DurableWorkQueueState.Queued, item.State);
            Assert.Equal(DurableSliceStatus.Queued, state.Get(JobId("job.orphan"), At(0), At(5)).Status);
        }

        [Fact]
        public void Recover_orphaned_slice_refuses_a_paused_job()
        {
            var created = catalog.Create(Schedule("job.orphan.paused"));
            state.AcquireLease("orphan-paused-lease", JobId("job.orphan.paused"), At(0), At(5), "stale-worker", TimeSpan.FromMinutes(1), At(10));
            catalog.SetEnabled(JobId("job.orphan.paused"), enabled: false, expectedVersion: created.CatalogVersion);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                Service().RecoverOrphanedSlice(JobId("job.orphan.paused"), At(0), At(5), "operator", "manual recovery"));

            Assert.Contains("paused", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DurableSliceStatus.Running, state.Get(JobId("job.orphan.paused"), At(0), At(5)).Status);
        }

        [Fact]
        public void Recover_orphaned_slice_returns_false_when_nothing_is_orphaned()
        {
            catalog.Create(Schedule("job.orphan.none"));
            state.Append("completed", JobId("job.orphan.none"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);

            var recovered = Service().RecoverOrphanedSlice(JobId("job.orphan.none"), At(0), At(5), "operator", "manual recovery");

            Assert.False(recovered);
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.orphan.none"), At(0), At(5)).Status);
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
