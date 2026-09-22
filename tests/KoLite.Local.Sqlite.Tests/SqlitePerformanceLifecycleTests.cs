// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Performance;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.State;
using static KoLite.Local.Sqlite.Tests.PerformanceTestStore;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqlitePerformanceLifecycleTests : IDisposable
    {
        private readonly PerformanceTestStore store = new();

        public void Dispose() => store.Dispose();

        [Fact]
        public void Pause_soft_delete_and_restore_preserve_facts_and_confirmed_hard_delete_audits_them()
        {
            var job = store.CreateJob();
            var survivor = store.CreateJob("survivor");
            store.Capture("deleted-attempt", job, At(10), At(11));
            store.Capture("surviving-attempt", survivor, At(12), At(13));
            var pending = store.Repository.GetPendingAttempts(At(20));
            var service = new SqliteJobLifecycleService(store.Factory, store.Catalog);
            var paused = store.Catalog.SetEnabled(job.JobId, false, job.CatalogVersion);
            Assert.Equal(2, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            var deleted = service.SoftDelete(job.JobId, paused.CatalogVersion, "test", "test");
            var restored = service.Restore(job.JobId, deleted.CatalogVersion, "test", "test");
            Assert.Equal(2, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            store.Catalog.SetEnabled(job.JobId, false, restored.CatalogVersion);

            var result = service.HardDelete(job.JobId, $"DELETE {job.DisplayName}", "test", "remove");
            store.Repository.ApplyStatistics(pending, pending.Select(attempt => Statistics(attempt)).ToArray(), At(20));

            Assert.Equal(1, result.DeletedJobs);
            Assert.Equal(1, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            Assert.Equal(survivor.JobId, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).JobId);
            Assert.Equal(1, store.Count("""
                SELECT json_extract(payload_json,'$.PerformanceAttemptRows') FROM system_audit
                WHERE action='HardDeleted' AND subject_id=$job;
                """, ("$job", job.JobId)));
            Assert.Equal(1, store.Count("SELECT json_extract(details_json,'$.PerformanceAttemptRows') FROM purge_runs WHERE purge_run_id=$id;",
                ("$id", result.PurgeRunId)));
        }

        [Fact]
        public void Bulk_hard_delete_counts_performance_rows_without_changing_its_public_result_contract()
        {
            var first = store.CreateJob("first");
            var second = store.CreateJob("second");
            store.Capture("first", first, At(10), At(11));
            store.Capture("second", second, At(10), At(11));
            var service = new SqliteJobLifecycleService(store.Factory, store.Catalog);
            var firstDeleted = service.SoftDelete(first.JobId, first.CatalogVersion, "test", "test");
            var secondDeleted = service.SoftDelete(second.JobId, second.CatalogVersion, "test", "test");

            var result = service.HardDeleteBatch(
                [new(first.JobId, firstDeleted.CatalogVersion), new(second.JobId, secondDeleted.CatalogVersion)],
                "DELETE 2 JOBS", "test", "bulk test");

            Assert.Equal(2, result.DeletedJobs);
            Assert.Equal(0, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            Assert.Equal(2, store.Count("SELECT SUM(json_extract(payload_json,'$.PerformanceAttemptRows')) FROM system_audit WHERE action='HardDeleted';"));
            Assert.Equal(2, store.Count("SELECT json_extract(details_json,'$.PerformanceAttemptRows') FROM purge_runs WHERE purge_run_id=$id;",
                ("$id", result.PurgeRunId)));
        }

        [Fact]
        public void Retention_honors_longer_protected_windows_and_prunes_old_incomplete_facts_only_without_live_leases()
        {
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-60);
            var completed = store.CreateJob("completed");
            store.Capture("old-completed", completed, old.AddMinutes(-1), old);
            store.Capture("recent-completed", completed, now.AddDays(-29).AddMinutes(-1), now.AddDays(-29));
            var orphan = store.CreateJob("orphan");
            store.Capture("orphan", orphan, old, null, "Started");
            store.Capture("no-start", orphan, null, null, "Started");
            store.Execute("UPDATE performance_attempts SET recorded_at_utc=$old WHERE attempt_id='no-start';", ("$old", SqliteStorage.Utc(old)));
            var liveParent = store.CreateJob("live-parent");
            store.Capture("live-parent", liveParent, old, null, "Started");
            store.State.AcquireLease("live", liveParent.JobId, At(0), At(5), "live-worker", TimeSpan.FromHours(1), now);
            var liveChunk = store.CreateJob("live-chunk", chunks: 2);
            var chunks = new SqliteChunkStateRepository(store.Factory);
            var child = chunks.EnsureWindow(new SliceRange(liveChunk.JobId, At(0), At(5)), 2, "test")[0];
            chunks.MarkQueued("chunk-queued", child.Execution, actor: "test");
            chunks.AcquireLease("chunk-live", child.Execution, "live-worker", TimeSpan.FromHours(1), now);
            store.Capture("live-chunk", liveChunk, old, null, "Started", chunk: 0);
            var liveQueue = store.CreateJob("live-queue");
            var queue = new SqliteWorkQueueRepository(store.Factory);
            queue.Enqueue(liveQueue.JobId, At(0), At(5), "queue-live", now);
            Assert.NotNull(queue.Claim("default", "live-worker", TimeSpan.FromHours(1), now));
            store.Capture("live-queue", liveQueue, old, null, "Started");

            var longer = store.Observability.CleanupOldReadModels(now.AddDays(-1), now.AddDays(-90), batchSize: 1);
            Assert.Equal(0, longer.PerformanceAttemptsDeleted);
            var protectedResult = store.Observability.CleanupOldReadModels(now.AddDays(-1), now.AddDays(-30), batchSize: 1);

            Assert.Equal(3, protectedResult.PerformanceAttemptsDeleted);
            Assert.Equal(4, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            Assert.Equal(3, store.Count("SELECT COUNT(*) FROM performance_attempts WHERE completed_at_utc IS NULL;"));
            Assert.Equal(DurableSliceStatus.Running, store.State.Get(liveParent.JobId, At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Running, chunks.Get(child.Execution)!.Status);
            Assert.Equal(DurableWorkQueueState.Leased, Assert.Single(queue.List(liveQueue.JobId)).State);
            Assert.Equal(5, store.Count("SELECT COUNT(*) FROM job_definitions;"));
            Assert.Equal(3, store.Count("SELECT json_extract(details_json,'$.performanceAttemptsDeleted') FROM retention_runs WHERE retention_run_id=$id;",
                ("$id", protectedResult.RetentionRunId)));
            Assert.Equal(protectedResult.TotalDeleted, store.Count("SELECT rows_deleted FROM retention_runs WHERE retention_run_id=$id;",
                ("$id", protectedResult.RetentionRunId)));
        }

        [Fact]
        public void Repair_does_not_reset_successful_sibling_resource_facts_or_prior_terminal_attempts()
        {
            var job = store.CreateJob(chunks: 2);
            var chunks = new SqliteChunkStateRepository(store.Factory);
            var slice = new SliceRange(job.JobId, At(0), At(5));
            foreach (var child in chunks.EnsureWindow(slice, 2, "test"))
            {
                chunks.MarkQueued($"queued-{child.ChunkId}", child.Execution, actor: "test");
                var lease = chunks.AcquireLease($"lease-{child.ChunkId}", child.Execution, "worker", TimeSpan.FromMinutes(5), At(10))!;
                if (child.ChunkId == 0)
                {
                    chunks.CompleteLease("completed-child", child.Execution, "worker", lease.LeaseToken!, At(11));
                    store.Capture("successful-sibling", job, At(10), At(11), chunk: 0);
                }
                else
                {
                    chunks.DeadLetterLease("failed-child", child.Execution, "worker", lease.LeaseToken!, At(11), "test", "Test");
                    store.Capture("failed-child", job, At(10), At(11), "DeadLettered", chunk: 1);
                }
            }

            var pending = store.Repository.GetPendingAttempts(At(20));
            store.Repository.ApplyStatistics(pending, [Statistics(Assert.Single(pending), 8, 9, 10)], At(20));
            var repair = new SqliteRepairService(store.Factory, store.Catalog, store.State, new SqliteWorkQueueRepository(store.Factory), new ManualClock(At(20)), chunks);
            repair.PlanAndEnqueue(new RepairPlanRequest(job.JobId, At(0), At(5), "test", "repair", Scope: RepairSliceScope.FailedAndDeadLetteredOnly));

            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(30)), row => row.IsJobTotal);
            Assert.Equal(2, total.CompletedAttempts);
            Assert.Equal(1, total.SucceededAttempts);
            Assert.Equal(new PerformancePercentiles(1, 8, 8, 8), total.CpuSeconds);
            Assert.Equal(DurableSliceStatus.Completed, chunks.Get(SliceExecutionUnit.Chunk(slice, 0, 2))!.Status);
        }
    }
}
