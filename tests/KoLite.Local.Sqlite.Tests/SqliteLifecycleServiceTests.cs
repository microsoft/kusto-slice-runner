// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteLifecycleServiceTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "lifecycle-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository readModels;

        public SqliteLifecycleServiceTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "lifecycle.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
        }

        [Fact]
        public void Soft_delete_hides_from_active_catalog_and_restore_reenables_with_history_intact()
        {
            var created = catalog.Create(Schedule("job.life"));
            state.Append("complete", JobId("job.life"), At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);

            var deleted = Service().SoftDelete(JobId("job.life"), created.CatalogVersion, "tester", "hide");
            Assert.False(deleted.IsEnabled);
            Assert.DoesNotContain(catalog.List(enabledOnly: true), j => j.JobId == JobId("job.life"));

            var restored = Service().Restore(JobId("job.life"), deleted.CatalogVersion, "tester", "bring back");

            Assert.True(restored.IsEnabled);
            Assert.Contains(catalog.List(enabledOnly: true), j => j.JobId == JobId("job.life"));
            Assert.Equal(DurableSliceStatus.Completed, state.Get(JobId("job.life"), At(0), At(5)).Status);
            Assert.Contains(catalog.History(JobId("job.life")), e => e.EventType == "Disabled");
            Assert.Contains(ReadScalarTexts($"SELECT event_type FROM job_lifecycle_events WHERE job_id='{JobId("job.life")}' ORDER BY recorded_at_utc;"), e => e == "SoftDeleted");
            Assert.Contains(ReadScalarTexts($"SELECT event_type FROM job_lifecycle_events WHERE job_id='{JobId("job.life")}' ORDER BY recorded_at_utc;"), e => e == "Restored");
        }

        [Fact]
        public void Hard_delete_requires_confirmation_purges_local_rows_and_records_external_audit()
        {
            var created = catalog.Create(Schedule("job.purge"));
            state.Append("queued", JobId("job.purge"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.purge"), At(0), At(5), "purge-queue", At(0));
            readModels.RecordAttempt("attempt", JobId("job.purge"), At(0), At(5), 1, "Started", "worker", At(0), null);
            readModels.RecordLog("Error", "boom", "test", JobId("job.purge"), At(0), At(5));

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete(JobId("job.purge"), JobId("job.purge"), "tester", "remove all local state"));
            Assert.Throws<InvalidOperationException>(() => Service().HardDelete(JobId("job.purge"), $"DELETE {created.DisplayName}", "tester", "remove all local state"));
            var leased = queue.Claim("default", "purge-test-worker", TimeSpan.FromMinutes(5), At(1));
            Assert.NotNull(leased);
            Service().SoftDelete(JobId("job.purge"), created.CatalogVersion, "tester", "disable before purge");
            queue.Complete(leased.QueueItemId, "purge-test-worker");
            var result = Service().HardDelete(JobId("job.purge"), $"DELETE {created.DisplayName}", "tester", "remove all local state");

            Assert.Equal(1, result.DeletedJobs);
            Assert.Null(catalog.Get(JobId("job.purge")));
            Assert.Empty(queue.List(JobId("job.purge")));
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("job.purge"), At(0), At(5)).Status);
            Assert.Empty(ReadScalarTexts($"SELECT attempt_id FROM slice_attempts WHERE job_id='{JobId("job.purge")}';"));
            Assert.Contains(ReadScalarTexts($"SELECT action FROM system_audit WHERE subject_id='{JobId("job.purge")}';"), a => a == "HardDeleted");
            Assert.Contains(ReadScalarTexts("SELECT status FROM purge_runs;"), s => s == "Completed");
            Assert.All(ReadScalarTexts("SELECT payload_json FROM system_audit WHERE action='HardDeleted';"),
                payload => Assert.DoesNotContain("throttleObservationRows", payload, StringComparison.OrdinalIgnoreCase));

            var recreated = catalog.Create(Schedule("job.purge"));
            Assert.Equal(1, recreated.CatalogVersion);
            Assert.Equal(DurableSliceStatus.Missing, state.Get(JobId("job.purge"), At(0), At(5)).Status);
        }

        [Fact]
        public void Hard_delete_succeeds_for_a_disabled_job_with_inert_queued_or_expired_lease_work()
        {
            // The reported case: a slice failed and was re-queued (Queued) just before the job was
            // disabled. The retry is inert -- a disabled job is never worked -- so it must not block the
            // purge forever. (Before the fix this threw "active work item(s) are queued or leased".)
            var queued = catalog.Create(Schedule("job.inert.queued"));
            state.Append("queued-inert", JobId("job.inert.queued"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.inert.queued"), At(0), At(5), "inert-queued", At(0));
            Service().SoftDelete(JobId("job.inert.queued"), queued.CatalogVersion, "tester", "disable");

            var result = Service().HardDelete(JobId("job.inert.queued"), $"DELETE {queued.DisplayName}", "tester", "purge");
            Assert.Equal(1, result.DeletedJobs);
            Assert.Null(catalog.Get(JobId("job.inert.queued")));
            Assert.Empty(queue.List(JobId("job.inert.queued")));

            // An expired (abandoned/crashed) lease is also inert for a disabled job: the synthetic claim
            // time is far in the past, so the lease is long expired relative to now.
            var expired = catalog.Create(Schedule("job.inert.expired"));
            state.Append("expired-lease", JobId("job.inert.expired"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.inert.expired"), At(0), At(5), "inert-expired", At(0));
            var stale = queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(5), At(1));
            Assert.NotNull(stale);
            Service().SoftDelete(JobId("job.inert.expired"), expired.CatalogVersion, "tester", "disable");

            Assert.Equal(1, Service().HardDelete(JobId("job.inert.expired"), $"DELETE {expired.DisplayName}", "tester", "purge").DeletedJobs);
            Assert.Null(catalog.Get(JobId("job.inert.expired")));
        }

        [Fact]
        public void Hard_delete_is_blocked_only_while_a_live_lease_or_running_slice_is_active()
        {
            // A work item held by a worker whose lease is still live (claimed "now") blocks the purge.
            var leased = catalog.Create(Schedule("job.live.leased"));
            state.Append("live-leased", JobId("job.live.leased"), At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(JobId("job.live.leased"), At(0), At(5), "live-leased", DateTimeOffset.UtcNow.AddMinutes(-1));
            var liveItem = queue.Claim("default", "live-worker", TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);
            Assert.NotNull(liveItem);
            Service().SoftDelete(JobId("job.live.leased"), leased.CatalogVersion, "tester", "disable");

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete(JobId("job.live.leased"), $"DELETE {leased.DisplayName}", "tester", "purge"));
            Assert.NotNull(catalog.Get(JobId("job.live.leased")));

            // A slice a worker is actively running (live lease) also blocks.
            var running = catalog.Create(Schedule("job.live.running"));
            state.AcquireLease("run-op", JobId("job.live.running"), At(0), At(5), "run-worker", TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow);
            Service().SoftDelete(JobId("job.live.running"), running.CatalogVersion, "tester", "disable");

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete(JobId("job.live.running"), $"DELETE {running.DisplayName}", "tester", "purge"));
            Assert.NotNull(catalog.Get(JobId("job.live.running")));
        }

        [Fact]
        public void Bulk_hard_delete_requires_count_confirmation_and_records_one_atomic_purge()
        {
            var first = catalog.Create(Schedule("job.bulk.first"));
            var second = catalog.Create(Schedule("job.bulk.second"));
            var deletedFirst = Service().SoftDelete(first.JobId, first.CatalogVersion, "tester", "bulk");
            var deletedSecond = Service().SoftDelete(second.JobId, second.CatalogVersion, "tester", "bulk");
            var items = new[]
            {
                new HardDeleteBatchItem(first.JobId, deletedFirst.CatalogVersion),
                new HardDeleteBatchItem(second.JobId, deletedSecond.CatalogVersion)
            };

            Assert.Throws<InvalidOperationException>(() =>
                Service().HardDeleteBatch(items, "DELETE SELECTED JOBS", "tester", "bulk purge"));

            var result = Service().HardDeleteBatch(items, "DELETE 2 JOBS", "tester", "bulk purge");

            Assert.Equal(2, result.DeletedJobs);
            Assert.Null(catalog.Get(first.JobId));
            Assert.Null(catalog.Get(second.JobId));
            Assert.Equal(2, ReadScalarTexts("SELECT action FROM system_audit WHERE action='HardDeleted';").Count);
            Assert.All(ReadScalarTexts("SELECT payload_json FROM system_audit WHERE action='HardDeleted';"),
                payload => Assert.DoesNotContain("throttleObservationRows", payload, StringComparison.OrdinalIgnoreCase));
            var purgeDetails = Assert.Single(ReadScalarTexts("SELECT details_json FROM purge_runs;"));
            Assert.Contains("\"bulk\":true", purgeDetails);
            Assert.Contains(first.JobId, purgeDetails);
            Assert.Contains(second.JobId, purgeDetails);
        }

        [Fact]
        public void Bulk_hard_delete_is_all_or_nothing_when_any_selected_job_changed()
        {
            var eligible = catalog.Create(Schedule("job.bulk.eligible"));
            var changed = catalog.Create(Schedule("job.bulk.changed"));
            var deletedEligible = Service().SoftDelete(eligible.JobId, eligible.CatalogVersion, "tester", "bulk");
            var deletedChanged = Service().SoftDelete(changed.JobId, changed.CatalogVersion, "tester", "bulk");
            Service().Restore(changed.JobId, deletedChanged.CatalogVersion, "tester", "changed after review");

            var exception = Assert.Throws<HardDeleteBatchValidationException>(() =>
                Service().HardDeleteBatch(
                [
                    new HardDeleteBatchItem(eligible.JobId, deletedEligible.CatalogVersion),
                    new HardDeleteBatchItem(changed.JobId, deletedChanged.CatalogVersion)
                ],
                "DELETE 2 JOBS",
                "tester",
                "must roll back"));

            Assert.Contains(exception.Issues, issue => issue.JobId == changed.JobId && issue.Message.Contains("changed since it was selected", StringComparison.Ordinal));
            Assert.Contains(exception.Issues, issue => issue.JobId == changed.JobId && issue.Message.Contains("no longer soft-deleted", StringComparison.Ordinal));
            Assert.NotNull(catalog.Get(eligible.JobId));
            Assert.NotNull(catalog.Get(changed.JobId));
            Assert.Empty(ReadScalarTexts("SELECT purge_run_id FROM purge_runs;"));
            Assert.Empty(ReadScalarTexts("SELECT action FROM system_audit WHERE action='HardDeleted';"));
        }

        [Fact]
        public void Bulk_hard_delete_deduplicates_selection_and_rejects_empty_or_live_work()
        {
            var empty = Assert.Throws<HardDeleteBatchValidationException>(() =>
                Service().HardDeleteBatch([], "DELETE 0 JOBS", "tester", "empty"));
            Assert.Contains(empty.Issues, issue => issue.Message.Contains("Select at least one", StringComparison.Ordinal));

            var eligible = catalog.Create(Schedule("job.bulk.single"));
            var deletedEligible = Service().SoftDelete(eligible.JobId, eligible.CatalogVersion, "tester", "bulk");
            var duplicateResult = Service().HardDeleteBatch(
            [
                new HardDeleteBatchItem(eligible.JobId, deletedEligible.CatalogVersion),
                new HardDeleteBatchItem(eligible.JobId, deletedEligible.CatalogVersion)
            ],
            "DELETE 1 JOB",
            "tester",
            "dedupe");
            Assert.Equal(1, duplicateResult.DeletedJobs);

            var live = catalog.Create(Schedule("job.bulk.live"));
            state.Append("bulk-live", live.JobId, At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue(live.JobId, At(0), At(5), "bulk-live", DateTimeOffset.UtcNow.AddMinutes(-1));
            Assert.NotNull(queue.Claim("default", "bulk-live-worker", TimeSpan.FromMinutes(30), DateTimeOffset.UtcNow));
            var deletedLive = Service().SoftDelete(live.JobId, live.CatalogVersion, "tester", "bulk");

            var blocked = Assert.Throws<HardDeleteBatchValidationException>(() =>
                Service().HardDeleteBatch(
                [
                    new HardDeleteBatchItem(live.JobId, deletedLive.CatalogVersion)
                ],
                "DELETE 1 JOB",
                "tester",
                "live"));
            Assert.Contains(blocked.Issues, issue => issue.Message.Contains("leased by an active worker", StringComparison.Ordinal));
            Assert.NotNull(catalog.Get(live.JobId));
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private SqliteJobLifecycleService Service() => new(factory, catalog);

        private IReadOnlyList<string> ReadScalarTexts(string sql)
        {
            using var c = factory.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();
            var values = new List<string>();
            while (r.Read()) values.Add(r.GetString(0));
            return values;
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
          "functionName": "LifecycleFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
