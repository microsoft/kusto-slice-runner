// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Orchestration;
using Ksr.Local.Core.Repair;
using Ksr.Local.Core.Schedules;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Lifecycle;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Orchestration;
using Ksr.Local.Sqlite.Queue;
using Ksr.Local.Sqlite.Repair;
using Ksr.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class SqliteRepairServiceTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "repair-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KsrSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteChunkStateRepository chunkState;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly ManualClock clock = new(At(20));

        public SqliteRepairServiceTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Path.Combine(testDirectory, "repair.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KsrSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            chunkState = new SqliteChunkStateRepository(factory);
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
        public void Chunked_repair_requeues_only_failed_chunks()
        {
            catalog.Create(Schedule("job.chunk-repair", chunks: 2));
            var slice = new SliceRange(JobId("job.chunk-repair"), At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 2, "test");
            foreach (var child in children)
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    Assert.True(chunkState.CompleteLease("complete-0", child.Execution, "worker", lease.LeaseToken!, At(11)));
                }
                else
                {
                    Assert.True(chunkState.DeadLetterLease("dead-1", child.Execution, "worker", lease.LeaseToken!, At(11), "boom", "Permanent"));
                }
            }

            var request = new RepairPlanRequest(
                JobId("job.chunk-repair"),
                At(0),
                At(5),
                "tester",
                "repair failed chunk",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);
            var preview = Service().Preview(request);
            var result = Service().PlanAndEnqueue(request);

            Assert.Equal(1, preview.Repairable);
            Assert.Equal(1, result.Queued);
            var work = Assert.Single(queue.List(JobId("job.chunk-repair")));
            Assert.Equal(1, work.ChunkId);
            Assert.Equal(2, work.TotalChunks);
            Assert.Equal(DurableSliceStatus.Completed, chunkState.Get(SliceExecutionUnit.Chunk(slice, 0, 2))!.Status);
            Assert.Equal(DurableSliceStatus.Queued, chunkState.Get(SliceExecutionUnit.Chunk(slice, 1, 2))!.Status);
            Assert.Equal(DurableSliceStatus.Queued, state.Get(JobId("job.chunk-repair"), At(0), At(5)).Status);
            Assert.Equal(1, QueryInt(
                "SELECT COUNT(*) FROM repair_chunk_executions WHERE repair_batch_id=$batch AND job_id=$job AND chunk_id=1 AND total_chunks=2 AND previous_state='DeadLettered' AND status='Queued' AND enqueued_queue_item_id IS NOT NULL;",
                ("$batch", result.RepairBatchId),
                ("$job", JobId("job.chunk-repair"))));

            var claimedRepair = queue.Claim("default", "retention-worker", TimeSpan.FromMinutes(5), At(30))!;
            Assert.True(queue.Complete(claimedRepair.QueueItemId, "retention-worker"));
            Assert.Equal(1, readModels.CleanupOldReadModels(DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(1)).QueueRowsDeleted);
            Assert.Empty(queue.List(JobId("job.chunk-repair")));
            Assert.Equal(1, QueryInt(
                "SELECT COUNT(*) FROM repair_chunk_executions WHERE repair_batch_id=$batch AND chunk_id=1 AND enqueued_queue_item_id IS NOT NULL;",
                ("$batch", result.RepairBatchId)));
        }

        [Fact]
        public void Chunked_repair_skips_failed_chunk_with_an_active_automatic_retry()
        {
            catalog.Create(Schedule("job.chunk-retry-pending", chunks: 2));
            var slice = new SliceRange(JobId("job.chunk-retry-pending"), At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 2, "test");

            chunkState.MarkQueued("complete-queued", children[0].Execution, actor: "test");
            var completedLease = chunkState.AcquireLease("complete-lease", children[0].Execution, "worker-0", TimeSpan.FromMinutes(5), At(10))!;
            Assert.True(chunkState.CompleteLease("complete", children[0].Execution, "worker-0", completedLease.LeaseToken!, At(11)));

            chunkState.MarkQueued("retry-queued", children[1].Execution, actor: "test");
            var originalWork = queue.Enqueue(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                $"normal|{children[1].Execution.ExecutionKey}",
                At(10),
                chunkId: 1,
                totalChunks: 2);
            var claimed = queue.Claim("default", "worker-1", TimeSpan.FromMinutes(5), At(10))!;
            var failedLease = chunkState.AcquireLease("retry-lease", children[1].Execution, "worker-1", TimeSpan.FromMinutes(5), At(10))!;
            Assert.True(chunkState.FailLease("retry-failed", children[1].Execution, "worker-1", failedLease.LeaseToken!, At(11), "transient", "Transient"));
            Assert.True(queue.Abandon(claimed.QueueItemId, "worker-1", At(12)));

            var request = new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "do not duplicate retry",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);
            var preview = Service().Preview(request);
            var result = Service().PlanAndEnqueue(request);

            Assert.Equal(0, preview.Repairable);
            Assert.Equal(0, preview.RepairableExecutions);
            Assert.Equal(1, preview.Skipped);
            Assert.Equal(0, result.Queued);
            var remaining = Assert.Single(queue.List(slice.JobId));
            Assert.Equal(originalWork.QueueItemId, remaining.QueueItemId);
            Assert.Equal(DurableWorkQueueState.Queued, remaining.State);
            Assert.Equal(DurableSliceStatus.Failed, chunkState.Get(children[1].Execution)!.Status);
        }

        [Fact]
        public void Chunked_repair_selects_terminal_gap_but_not_retry_pending_sibling()
        {
            catalog.Create(Schedule("job.chunk-mixed-failures", chunks: 3));
            var slice = new SliceRange(JobId("job.chunk-mixed-failures"), At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 3, "test");
            foreach (var child in children)
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    Assert.True(chunkState.CompleteLease("complete-0", child.Execution, "worker-0", lease.LeaseToken!, At(11)));
                }
                else if (child.ChunkId == 1)
                {
                    Assert.True(chunkState.DeadLetterLease("dead-1", child.Execution, "worker-1", lease.LeaseToken!, At(11), "permanent", "Permanent"));
                }
                else
                {
                    Assert.True(chunkState.FailLease("failed-2", child.Execution, "worker-2", lease.LeaseToken!, At(11), "transient", "Transient"));
                }
            }

            var retryWork = queue.Enqueue(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                $"normal|{children[2].Execution.ExecutionKey}",
                At(12),
                chunkId: 2,
                totalChunks: 3);
            var request = new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "repair terminal gap only",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);

            var preview = Service().Preview(request);
            var result = Service().PlanAndEnqueue(request);

            var previewSlice = Assert.Single(preview.Slices, item => item.Outcome == RepairSliceOutcome.Repairable);
            Assert.Equal([1], previewSlice.ChunkIds);
            Assert.Equal(1, preview.RepairableExecutions);
            Assert.Equal(1, result.Queued);
            var work = queue.List(slice.JobId);
            Assert.Equal(2, work.Count);
            Assert.Contains(work, item => item.QueueItemId == retryWork.QueueItemId && item.ChunkId == 2);
            Assert.Contains(work, item => item.ChunkId == 1 && item.IdempotencyKey.StartsWith("repair|", StringComparison.Ordinal));
            Assert.Equal(DurableSliceStatus.Completed, chunkState.Get(children[0].Execution)!.Status);
            Assert.Equal(DurableSliceStatus.Queued, chunkState.Get(children[1].Execution)!.Status);
            Assert.Equal(DurableSliceStatus.Failed, chunkState.Get(children[2].Execution)!.Status);
        }

        [Fact]
        public async Task Concurrent_chunk_repairs_create_only_one_runnable_queue_row()
        {
            catalog.Create(Schedule("job.chunk-concurrent-repair", chunks: 1));
            var slice = new SliceRange(JobId("job.chunk-concurrent-repair"), At(0), At(5));
            var child = Assert.Single(chunkState.EnsureWindow(slice, 1, "test"));
            chunkState.MarkQueued("queued", child.Execution, actor: "test");
            var lease = chunkState.AcquireLease("lease", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
            Assert.True(chunkState.DeadLetterLease("dead", child.Execution, "worker", lease.LeaseToken!, At(11), "permanent", "Permanent"));

            var requests = new[]
            {
                new RepairPlanRequest(slice.JobId, slice.StartUtc, slice.EndUtc, "tester-a", "repair-a", Scope: RepairSliceScope.FailedAndDeadLetteredOnly),
                new RepairPlanRequest(slice.JobId, slice.StartUtc, slice.EndUtc, "tester-b", "repair-b", Scope: RepairSliceScope.FailedAndDeadLetteredOnly),
            };

            var results = await Task.WhenAll(requests.Select(request => Task.Run(() => Service().PlanAndEnqueue(request))));

            Assert.Equal(1, results.Sum(result => result.Queued));
            Assert.Single(queue.List(slice.JobId), item => item.State == DurableWorkQueueState.Queued && item.ChunkId == 0);
            Assert.Equal(1, QueryInt("SELECT COUNT(*) FROM repair_chunk_executions WHERE job_id=$job;", ("$job", slice.JobId)));
        }

        [Fact]
        public async Task Repaired_terminal_chunks_complete_parent_without_reexecuting_successful_sibling()
        {
            catalog.Create(Schedule("job.chunk-repair-execute", chunks: 3));
            var slice = new SliceRange(JobId("job.chunk-repair-execute"), At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 3, "test");
            foreach (var child in children)
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    chunkState.CompleteLease("complete-0", child.Execution, "worker-0", lease.LeaseToken!, At(11));
                }
                else
                {
                    chunkState.DeadLetterLease($"dead-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", lease.LeaseToken!, At(11), "permanent", "Permanent");
                }
            }

            var repair = Service().PlanAndEnqueue(new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "repair terminal chunks",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly));
            var executor = new ChunkRecordingExecutor();
            var worker = new SqliteLocalWorker(
                catalog,
                state,
                queue,
                readModels,
                executor,
                clock,
                new LocalWorkerOptions(WorkerId: "repair-chunk-worker"),
                chunkState: chunkState);

            Assert.True((await worker.RunOnceAsync()).Succeeded);
            Assert.True((await worker.RunOnceAsync()).Succeeded);

            Assert.Equal([1, 2], executor.Executions.Select(execution => execution.ChunkId).Order().ToArray());
            Assert.Equal(DurableSliceStatus.Completed, state.Get(slice.JobId, slice.StartUtc, slice.EndUtc).Status);
            var final = chunkState.List(slice);
            Assert.All(final, child => Assert.Equal(DurableSliceStatus.Completed, child.Status));
            Assert.Equal(1, final.Single(child => child.ChunkId == 0).Attempt);
            Assert.Equal(2, final.Single(child => child.ChunkId == 1).Attempt);
            Assert.Equal(2, final.Single(child => child.ChunkId == 2).Attempt);
            Assert.All(Service().GetRepairChunkExecutions(repair.RepairBatchId), repaired => Assert.Equal(RepairSliceStatus.Completed, repaired.Status));
            Assert.Equal(RepairSliceStatus.Completed, Assert.Single(Service().GetRepairSlices(repair.RepairBatchId)).Status);
            Assert.Equal("Completed", QueryString("SELECT status FROM repair_batches WHERE repair_batch_id=$id;", ("$id", repair.RepairBatchId)));
        }

        [Fact]
        public async Task Repaired_chunk_that_fails_again_keeps_successful_sibling_untouched()
        {
            catalog.Create(Schedule("job.chunk-repair-refail", chunks: 2));
            var slice = new SliceRange(JobId("job.chunk-repair-refail"), At(0), At(5));
            var children = chunkState.EnsureWindow(slice, 2, "test");
            foreach (var child in children)
            {
                chunkState.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunkState.AcquireLease($"lease-{child.ChunkId}", child.Execution, $"worker-{child.ChunkId}", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    chunkState.CompleteLease("complete-0", child.Execution, "worker-0", lease.LeaseToken!, At(11));
                }
                else
                {
                    chunkState.DeadLetterLease("dead-1", child.Execution, "worker-1", lease.LeaseToken!, At(11), "permanent", "Permanent");
                }
            }

            var repair = Service().PlanAndEnqueue(new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "repair then fail",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly));
            var executor = new ChunkRecordingExecutor(LocalSliceOutputResult.Failure("StillBroken", "still broken", isRetryable: false));
            var worker = new SqliteLocalWorker(
                catalog,
                state,
                queue,
                readModels,
                executor,
                clock,
                new LocalWorkerOptions(WorkerId: "repair-refail-worker"),
                chunkState: chunkState);

            var run = await worker.RunOnceAsync();

            Assert.True(run.DeadLettered);
            Assert.Equal([1], executor.Executions.Select(execution => execution.ChunkId).ToArray());
            Assert.Equal(DurableSliceStatus.DeadLettered, state.Get(slice.JobId, slice.StartUtc, slice.EndUtc).Status);
            Assert.Equal(DurableSliceStatus.Completed, chunkState.Get(children[0].Execution)!.Status);
            Assert.Equal(1, chunkState.Get(children[0].Execution)!.Attempt);
            Assert.Equal(DurableSliceStatus.DeadLettered, chunkState.Get(children[1].Execution)!.Status);
            Assert.Equal(2, chunkState.Get(children[1].Execution)!.Attempt);
            Assert.Equal(RepairSliceStatus.Failed, Assert.Single(Service().GetRepairChunkExecutions(repair.RepairBatchId)).Status);
            Assert.Equal(RepairSliceStatus.Failed, Assert.Single(Service().GetRepairSlices(repair.RepairBatchId)).Status);
            Assert.Equal("Failed", QueryString("SELECT status FROM repair_batches WHERE repair_batch_id=$id;", ("$id", repair.RepairBatchId)));
        }

        [Fact]
        public async Task Dead_lettered_repair_can_be_repaired_again_with_same_reason()
        {
            catalog.Create(Schedule("job.chunk-rerepair-same-reason", chunks: 1));
            var slice = new SliceRange(JobId("job.chunk-rerepair-same-reason"), At(0), At(5));
            var child = Assert.Single(chunkState.EnsureWindow(slice, 1, "test"));
            chunkState.MarkQueued("queued", child.Execution, actor: "test");
            var lease = chunkState.AcquireLease("lease", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
            chunkState.DeadLetterLease("dead", child.Execution, "worker", lease.LeaseToken!, At(11), "permanent", "Permanent");
            var request = new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "same reason",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);
            var first = Service().PlanAndEnqueue(request);
            var firstWorker = new SqliteLocalWorker(
                catalog,
                state,
                queue,
                readModels,
                new ChunkRecordingExecutor(LocalSliceOutputResult.Failure("StillBroken", "still broken", isRetryable: false)),
                clock,
                new LocalWorkerOptions(WorkerId: "first-repair-worker"),
                chunkState: chunkState);
            Assert.True((await firstWorker.RunOnceAsync()).DeadLettered);

            var second = Service().PlanAndEnqueue(request);

            Assert.NotEqual(first.RepairBatchId, second.RepairBatchId);
            Assert.Equal(1, second.Queued);
            var work = queue.List(slice.JobId);
            Assert.Equal(2, work.Count);
            Assert.Single(work, item => item.State == DurableWorkQueueState.DeadLettered);
            Assert.Single(work, item => item.State == DurableWorkQueueState.Queued);
        }

        [Fact]
        public void Pausing_chunked_job_after_preview_prevents_repair_enqueue()
        {
            var created = catalog.Create(Schedule("job.chunk-pause-after-preview", chunks: 1));
            var slice = new SliceRange(created.JobId, At(0), At(5));
            var child = Assert.Single(chunkState.EnsureWindow(slice, 1, "test"));
            chunkState.MarkQueued("queued", child.Execution, actor: "test");
            var lease = chunkState.AcquireLease("lease", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
            chunkState.DeadLetterLease("dead", child.Execution, "worker", lease.LeaseToken!, At(11), "permanent", "Permanent");
            var request = new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "pause race",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);
            Assert.Equal(1, Service().Preview(request).RepairableExecutions);
            catalog.SetEnabled(created.JobId, enabled: false, expectedVersion: created.CatalogVersion);

            var error = Assert.Throws<InvalidOperationException>(() => Service().PlanAndEnqueue(request));

            Assert.Contains("paused", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(queue.List(slice.JobId));
        }

        [Fact]
        public void Chunk_repair_parent_exists_before_queue_row_becomes_visible()
        {
            catalog.Create(Schedule("job.chunk-parent-first", chunks: 1));
            var slice = new SliceRange(JobId("job.chunk-parent-first"), At(0), At(5));
            var child = Assert.Single(chunkState.EnsureWindow(slice, 1, "test"));
            chunkState.MarkQueued("queued", child.Execution, actor: "test");
            var lease = chunkState.AcquireLease("lease", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
            chunkState.DeadLetterLease("dead", child.Execution, "worker", lease.LeaseToken!, At(11), "permanent", "Permanent");
            ExecuteNonQuery("""
                CREATE TRIGGER repair_parent_before_queue
                BEFORE INSERT ON work_queue
                WHEN NEW.idempotency_key LIKE 'repair|%'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM repair_slices parent
                      WHERE parent.job_id=NEW.job_id
                        AND parent.slice_start_utc=NEW.slice_start_utc
                        AND parent.slice_end_utc=NEW.slice_end_utc
                  )
                BEGIN
                    SELECT RAISE(ABORT, 'repair parent missing before queue insert');
                END;
                """);

            var result = Service().PlanAndEnqueue(new RepairPlanRequest(
                slice.JobId,
                slice.StartUtc,
                slice.EndUtc,
                "tester",
                "parent first",
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly));

            Assert.Equal(1, result.Queued);
            Assert.Equal(RepairSliceStatus.Queued, Assert.Single(Service().GetRepairSlices(result.RepairBatchId)).Status);
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

        private SqliteRepairService Service() => new(factory, catalog, state, queue, clock, chunkState);
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

        private void ExecuteNonQuery(string sql)
        {
            using var connection = factory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static string Schedule(string activityId, string? dependsOn = null, int? chunks = null) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "RepairFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 10,
          "queryTimeout": "00:01:00",
          {{(chunks is null ? string.Empty : $"\"chunks\": {chunks},")}}
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://ksr-example.invalid", "database": "DemoDb" }
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

        private sealed class ChunkRecordingExecutor : ILocalSliceOutputExecutor
        {
            private readonly LocalSliceOutputResult result;

            public ChunkRecordingExecutor(LocalSliceOutputResult? result = null)
            {
                this.result = result ?? LocalSliceOutputResult.Success("test://chunk-repair");
            }

            public List<SliceExecutionUnit> Executions { get; } = [];

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default) =>
                Task.FromResult(LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}"));

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceExecutionUnit execution, CancellationToken cancellationToken = default)
            {
                Executions.Add(execution);
                return Task.FromResult(result);
            }
        }
    }
}
