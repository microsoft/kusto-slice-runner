// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Rerun;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Infrastructure;
using Ksr.Local.Sqlite.Performance;
using Ksr.Local.Sqlite.Rerun;
using Ksr.Local.Sqlite.State;
using static Ksr.Local.Sqlite.Tests.PerformanceTestStore;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class SqlitePerformanceBackfillTests : IDisposable
    {
        private readonly PerformanceTestStore store = new();

        public void Dispose() => store.Dispose();

        [Fact]
        public void Interrupted_current_and_archive_batches_resume_and_deduplicate_original_attempt_ids()
        {
            var job = store.CreateJob();
            store.Legacy("current-a", job, At(10), At(11));
            store.Legacy("current-b", job, At(12), At(13), "FailedRetryable");
            store.Archive("archive-a", job,
            [
                ArchivedAttempt("current-a", job, At(10), At(11)),
                ArchivedAttempt("archived-c", job, At(14), At(15)),
                ArchivedAttempt("archived-d", job, At(16), At(17), "DeadLettered")
            ]);
            store.Archive("archive-b", job, [ArchivedAttempt("archived-c", job, At(14), At(15))]);
            store.Repository.BeginHistoryReconciliation(At(60));
            Assert.False(store.Repository.BackfillBatch(At(60), 1));
            Assert.False(store.Repository.GetCollectionStatus().HistoryInitialized);
            Assert.Equal(1, store.Count("SELECT history_rows_processed FROM performance_collection_state;"));

            var resumed = new SqlitePerformanceRepository(store.Factory);
            resumed.BeginHistoryReconciliation(At(61));
            Assert.Equal(1, store.Count("SELECT history_rows_processed FROM performance_collection_state;"));
            Assert.False(resumed.BackfillBatch(At(61), 1));
            Assert.False(resumed.BackfillBatch(At(61), 1));
            Assert.False(resumed.BackfillBatch(At(61), 1));
            Assert.Equal(1, store.Count("SELECT archive_attempt_offset FROM performance_collection_state;"));

            new SqlitePerformanceRepository(store.Factory).BeginHistoryReconciliation(At(62));
            Assert.Equal(1, store.Count("SELECT archive_attempt_offset FROM performance_collection_state;"));
            store.Reconcile(At(62), batchSize: 1, begin: false);

            Assert.True(store.Repository.GetCollectionStatus().HistoryInitialized);
            Assert.Equal(At(62), store.Repository.GetCollectionStatus().LastHistorySyncUtc);
            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(63)));
            Assert.Equal(4, total.CompletedAttempts);
            Assert.Equal(2, total.SucceededAttempts);
            Assert.Equal(2, store.Count("SELECT COUNT(*) FROM slice_attempts;"));
            Assert.Equal(2, store.Count("SELECT COUNT(*) FROM rerun_slices;"));
        }

        [Fact]
        public void Later_reconciliation_catches_older_app_writes_behind_prior_cursors_without_hiding_complete_history()
        {
            var job = store.CreateJob();
            store.Legacy("original", job, At(20), At(21));
            store.Reconcile();
            store.Legacy("old-app-late-write", job, At(10), At(11));
            store.Archive("old-app-rerun", job, [ArchivedAttempt("old-app-archive", job, At(12), At(13))], At(65));

            var nextStartup = new SqlitePerformanceRepository(store.Factory);
            nextStartup.BeginHistoryReconciliation(At(70));
            Assert.True(nextStartup.GetCollectionStatus().HistoryInitialized);
            Assert.Equal(At(60), nextStartup.GetCollectionStatus().LastHistorySyncUtc);
            store.Reconcile(At(70), batchSize: 1, begin: false);

            Assert.Equal(3, Assert.Single(nextStartup.GetAggregates(At(0), At(71))).CompletedAttempts);
            Assert.Equal(At(70), nextStartup.GetCollectionStatus().LastHistorySyncUtc);
        }

        [Fact]
        public void Captured_dispatch_target_and_resources_win_over_current_and_archived_legacy_inference()
        {
            var job = store.CreateJob();
            store.Capture("exact", job, At(10), At(11), suppressed: false);
            var pending = store.Repository.GetPendingAttempts(At(20));
            var server = Guid.NewGuid();
            store.Repository.ApplyStatistics(pending, [Statistics(Assert.Single(pending), 5, 6, 7, server)], At(20));
            store.Archive("duplicate", job, [ArchivedAttempt("exact", job, null, At(11))]);
            var updated = store.Catalog.Update(job.JobId, Schedule(job.ActivityId, cluster: "https://new-target.invalid"), job.CatalogVersion);
            store.Execute("UPDATE job_definition_events SET recorded_at_utc=$at WHERE job_id=$job AND catalog_version=$version;",
                ("$at", SqliteStorage.Utc(At(9))), ("$job", job.JobId), ("$version", updated.CatalogVersion));

            store.Reconcile();

            Assert.Equal(Cluster, store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id='exact';"));
            Assert.Equal(1, store.Count("SELECT catalog_version FROM performance_attempts WHERE attempt_id='exact';"));
            Assert.Equal(0, store.Count("SELECT legacy_correlation FROM performance_attempts WHERE attempt_id='exact';"));
            Assert.Equal(server.ToString("N"), store.Scalar("SELECT server_activity_id FROM performance_attempts WHERE attempt_id='exact';"));
            Assert.Equal(5d, Assert.Single(store.Repository.GetAggregates(At(0), At(60))).CpuSeconds.P50);
        }

        [Fact]
        public void Historical_targets_come_from_versioned_definitions_and_changes_during_execution_are_ambiguous()
        {
            var job = store.CreateJob();
            store.Legacy("old-target", job, At(10), At(11));
            store.Legacy("changed-mid-attempt", job, At(15), At(25));
            store.Legacy("new-target", job, At(30), At(31));
            var changed = store.Catalog.Update(job.JobId, Schedule(job.ActivityId, cluster: "https://later-target.invalid", database: "LaterDb"), job.CatalogVersion);
            store.Execute("UPDATE job_definition_events SET recorded_at_utc=$at WHERE job_id=$job AND catalog_version=$version;",
                ("$at", SqliteStorage.Utc(At(20))), ("$job", job.JobId), ("$version", changed.CatalogVersion));

            store.Reconcile();

            Assert.Equal(Cluster, store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id='old-target';"));
            Assert.Equal(Database, store.Scalar("SELECT database_name FROM performance_attempts WHERE attempt_id='old-target';"));
            Assert.Equal("https://later-target.invalid", store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id='new-target';"));
            Assert.Equal(2, store.Count("SELECT catalog_version FROM performance_attempts WHERE attempt_id='new-target';"));
            Assert.Equal("Unavailable", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='changed-mid-attempt';"));
            Assert.Contains("ambiguous", (string)store.Scalar("SELECT capture_error FROM performance_attempts WHERE attempt_id='changed-mid-attempt';")!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, Assert.Single(store.Repository.GetAggregates(At(0), At(60))).CompletedAttempts);
            Assert.Equal("old-target", Assert.Single(store.Repository.GetPendingAttempts(At(60), oldestFirst: true)).AttemptId);
        }

        [Fact]
        public void Conflicting_targets_at_the_same_catalog_timestamp_are_not_nearest_picked()
        {
            var job = store.CreateJob();
            store.Legacy("ambiguous", job, At(10), At(11));
            var changed = store.Catalog.Update(job.JobId, Schedule(job.ActivityId, cluster: "https://conflict.invalid"), job.CatalogVersion);
            store.Execute("UPDATE job_definition_events SET recorded_at_utc=$at WHERE job_id=$job;",
                ("$at", SqliteStorage.Utc(At(9))), ("$job", job.JobId));

            store.Reconcile();

            Assert.Empty(store.Repository.GetPendingAttempts(At(60)));
            Assert.Equal("Unavailable", store.Scalar("SELECT collection_status FROM performance_attempts WHERE attempt_id='ambiguous';"));
            Assert.Equal(1, Assert.Single(store.Repository.GetAggregates(At(0), At(60))).CompletedAttempts);
            Assert.Equal(2, changed.CatalogVersion);
        }

        [Fact]
        public void Missing_chunk_start_never_uses_the_parent_running_event_and_missing_target_still_counts()
        {
            var job = store.CreateJob(chunks: 2);
            store.Legacy("missing-start", job, null, At(11), chunk: 0, chunks: 2);
            store.State.AcquireLease("parent-running", job.JobId, At(0), At(5), "worker", TimeSpan.FromMinutes(5), At(8));
            var noHistory = store.CreateJob("missing-target");
            store.Legacy("missing-target", noHistory, At(10), At(11));
            store.Execute("DELETE FROM job_definition_events WHERE job_id=$job;", ("$job", noHistory.JobId));

            store.Reconcile();

            Assert.Null(store.Scalar("SELECT started_at_utc FROM performance_attempts WHERE attempt_id='missing-start';"));
            Assert.Empty(store.Repository.GetPendingAttempts(At(60)));
            Assert.Equal(2, store.Repository.GetAggregates(At(0), At(60)).Where(row => row.IsJobTotal).Sum(row => row.CompletedAttempts));
            Assert.Contains("start", (string)store.Scalar("SELECT capture_error FROM performance_attempts WHERE attempt_id='missing-start';")!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Raw_archive_null_and_missing_columns_preserve_known_completed_attempts_without_inventing_metrics()
        {
            var job = store.CreateJob();
            var missing = ArchivedAttempt("missing-columns", job, null, At(11));
            missing.Remove("chunk_id");
            missing.Remove("total_chunks");
            missing["attempt"] = null;
            missing["slice_start_utc"] = null;
            missing.Remove("slice_end_utc");
            store.Archive("raw", job,
            [
                missing,
                ArchivedAttempt("running", job, At(10), null, "Started"),
                ArchivedAttempt("future", job, At(10), At(11), "Unknown")
            ]);

            store.Reconcile(batchSize: 1);

            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(60)));
            Assert.Equal(1, total.CompletedAttempts);
            Assert.Equal(PerformancePercentiles.Empty, total.CpuSeconds);
            Assert.Empty(store.Repository.GetPendingAttempts(At(60)));
            Assert.Equal(0, store.Count("SELECT attempt FROM performance_attempts WHERE attempt_id='missing-columns';"));
        }

        [Fact]
        public void Backfill_is_recent_bounded_and_never_reimports_pruned_archive_attempts()
        {
            var job = store.CreateJob();
            store.Legacy("too-old", job, At(10).AddDays(-31), At(11).AddDays(-31));
            store.Legacy("recent", job, At(10), At(11));
            store.Archive("old-attempt-recent-snapshot", job,
                [ArchivedAttempt("archived-too-old", job, At(10).AddDays(-31), At(11).AddDays(-31))]);
            store.Reconcile();
            Assert.Equal(1, store.Count("SELECT COUNT(*) FROM performance_attempts;"));

            store.Archive("recent-archive", job, [ArchivedAttempt("recent", job, At(10), At(11))]);
            store.Observability.CleanupOldReadModels(At(12), At(12), batchSize: 1);
            store.Reconcile(At(70), batchSize: 1);

            Assert.Equal(0, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            Assert.Equal(2, store.Count("SELECT COUNT(*) FROM rerun_slices;"));
            Assert.Equal(1, store.Count("SELECT COUNT(*) FROM current_slice_state;"));
        }

        [Fact]
        public void Rerun_during_initial_import_preserves_unseeded_legacy_facts_before_deleting_current_attempts()
        {
            var job = store.CreateJob();
            store.Legacy("legacy-before-rerun", job, At(10), At(11));
            var current = store.State.Get(job.JobId, At(0), At(5));
            store.State.Append("complete", job.JobId, At(0), At(5), DurableSliceStatus.Completed, current.Version);
            var service = new SqliteRerunService(store.Factory, store.Catalog, new ManualClock(At(20)));
            var plan = service.CreatePlan(new RerunPlanRequest(job.JobId, At(0), At(5), "test", "test rerun"));

            service.Execute(new RerunExecuteRequest(plan.RerunBatchId, "test", KustoCleanupAcknowledged: true));

            Assert.Equal(0, store.Count("SELECT COUNT(*) FROM slice_attempts;"));
            Assert.Equal(1, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CompletedAttempts);
            store.Reconcile();
            Assert.Equal(1, Assert.Single(store.Repository.GetAggregates(At(0), At(60))).CompletedAttempts);
        }

        [Fact]
        public void Rerun_keeps_previous_resources_and_new_attempts_remain_separate_samples()
        {
            var job = store.CreateJob();
            store.Capture("before-rerun", job, At(10), At(11));
            var before = store.Repository.GetPendingAttempts(At(15));
            store.Repository.ApplyStatistics(before, [Statistics(Assert.Single(before), 5, 6, 7)], At(15));
            var state = store.State.Get(job.JobId, At(0), At(5));
            store.State.Append("completed", job.JobId, At(0), At(5), DurableSliceStatus.Completed, state.Version);
            var service = new SqliteRerunService(store.Factory, store.Catalog, new ManualClock(At(20)));
            var plan = service.CreatePlan(new RerunPlanRequest(job.JobId, At(0), At(5), "test", "new output"));

            service.Execute(new RerunExecuteRequest(plan.RerunBatchId, "test", KustoCleanupAcknowledged: true));

            Assert.Equal(1, store.Repository.GetCollectionStatus().AvailableSamples);
            store.EnsureSlice(job.JobId);
            store.Capture("after-rerun", job, At(23), At(24));
            var after = store.Repository.GetPendingAttempts(At(30));
            store.Repository.ApplyStatistics(after, [Statistics(Assert.Single(after), 9, 10, 11)], At(30));
            store.Reconcile();
            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(60)));
            Assert.Equal(2, total.CompletedAttempts);
            Assert.Equal(new PerformancePercentiles(2, 5, 9, 9), total.CpuSeconds);
            Assert.Equal(2, store.Count("SELECT COUNT(DISTINCT server_activity_id) FROM performance_attempts;"));
        }

        [Fact]
        public void Legacy_client_identity_includes_exact_raw_chunk_id_and_total()
        {
            var job = store.CreateJob(chunks: 16);
            store.Legacy("chunk-15", job, At(10), At(11), chunk: 15, chunks: 16);
            store.Reconcile();
            var pending = Assert.Single(store.Repository.GetPendingAttempts(At(60)));
            var expected = SliceExecutionUnit.Chunk(new SliceRange(job.JobId, At(0), At(5)), 15, 16);
            Assert.True(pending.LegacyCorrelation);
            Assert.Equal($"Ksr.Local.Output;output|{expected.ExecutionKey}", pending.ClientRequestId);
        }
    }
}
