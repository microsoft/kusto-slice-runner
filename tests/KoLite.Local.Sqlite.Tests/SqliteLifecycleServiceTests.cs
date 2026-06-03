using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
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
            new KoLiteSqliteMigrator(factory).Migrate();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            readModels = new SqliteOperationalReadModelRepository(factory);
        }

        [Fact]
        public void Soft_delete_hides_from_active_catalog_and_restore_reenables_with_history_intact()
        {
            var created = catalog.Create(Schedule("job.life"));
            state.Append("complete", "job.life", At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);

            var deleted = Service().SoftDelete("job.life", created.CatalogVersion, "tester", "hide");
            Assert.False(deleted.IsEnabled);
            Assert.DoesNotContain(catalog.List(enabledOnly: true), j => j.JobId == "job.life");

            var restored = Service().Restore("job.life", deleted.CatalogVersion, "tester", "bring back");

            Assert.True(restored.IsEnabled);
            Assert.Contains(catalog.List(enabledOnly: true), j => j.JobId == "job.life");
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.life", At(0), At(5)).Status);
            Assert.Contains(catalog.History("job.life"), e => e.EventType == "Disabled");
            Assert.Contains(ReadScalarTexts("SELECT event_type FROM job_lifecycle_events WHERE job_id='job.life' ORDER BY recorded_at_utc;"), e => e == "SoftDeleted");
            Assert.Contains(ReadScalarTexts("SELECT event_type FROM job_lifecycle_events WHERE job_id='job.life' ORDER BY recorded_at_utc;"), e => e == "Restored");
        }

        [Fact]
        public void Hard_delete_requires_confirmation_purges_local_rows_and_records_external_audit()
        {
            var created = catalog.Create(Schedule("job.purge"));
            state.Append("queued", "job.purge", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue("job.purge", At(0), At(5), "purge-queue", At(0));
            readModels.RecordAttempt("attempt", "job.purge", At(0), At(5), 1, "Started", "worker", At(0), null);
            readModels.RecordLog("Error", "boom", "test", "job.purge", At(0), At(5));

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete("job.purge", "job.purge", "tester", "remove all local state"));
            Assert.Throws<InvalidOperationException>(() => Service().HardDelete("job.purge", "DELETE job.purge", "tester", "remove all local state"));
            var leased = queue.Claim("default", "purge-test-worker", TimeSpan.FromMinutes(5), At(1));
            Assert.NotNull(leased);
            Service().SoftDelete("job.purge", created.CatalogVersion, "tester", "disable before purge");
            queue.Complete(leased.QueueItemId, "purge-test-worker");
            var result = Service().HardDelete("job.purge", "DELETE job.purge", "tester", "remove all local state");

            Assert.Equal(1, result.DeletedJobs);
            Assert.Null(catalog.Get("job.purge"));
            Assert.Empty(queue.List("job.purge"));
            Assert.Equal(DurableSliceStatus.Missing, state.Get("job.purge", At(0), At(5)).Status);
            Assert.Empty(ReadScalarTexts("SELECT attempt_id FROM slice_attempts WHERE job_id='job.purge';"));
            Assert.Contains(ReadScalarTexts("SELECT action FROM system_audit WHERE subject_id='job.purge';"), a => a == "HardDeleted");
            Assert.Contains(ReadScalarTexts("SELECT status FROM purge_runs;"), s => s == "Completed");

            var recreated = catalog.Create(Schedule("job.purge"));
            Assert.Equal(1, recreated.CatalogVersion);
            Assert.Equal(DurableSliceStatus.Missing, state.Get("job.purge", At(0), At(5)).Status);
        }

        [Fact]
        public void Hard_delete_fails_when_disabled_job_has_active_work_or_running_slices()
        {
            var queued = catalog.Create(Schedule("job.active.queued"));
            state.Append("queued-active", "job.active.queued", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue("job.active.queued", At(0), At(5), "active-queued", At(0));
            Service().SoftDelete("job.active.queued", queued.CatalogVersion, "tester", "disable");

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete("job.active.queued", "DELETE job.active.queued", "tester", "purge"));
            Assert.NotNull(catalog.Get("job.active.queued"));

            var leased = catalog.Create(Schedule("job.active.leased"));
            state.Append("leased-active", "job.active.leased", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            queue.Enqueue("job.active.leased", At(0), At(5), "active-leased", At(0));
            var item = queue.Claim("default", "lease-test-worker", TimeSpan.FromMinutes(5), At(1));
            Assert.NotNull(item);
            Service().SoftDelete("job.active.leased", leased.CatalogVersion, "tester", "disable");

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete("job.active.leased", "DELETE job.active.leased", "tester", "purge"));
            Assert.NotNull(catalog.Get("job.active.leased"));

            var running = catalog.Create(Schedule("job.active.running"));
            state.Append("running", "job.active.running", At(0), At(5), DurableSliceStatus.Running, expectedVersion: 0);
            Service().SoftDelete("job.active.running", running.CatalogVersion, "tester", "disable");

            Assert.Throws<InvalidOperationException>(() => Service().HardDelete("job.active.running", "DELETE job.active.running", "tester", "purge"));
            Assert.NotNull(catalog.Get("job.active.running"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
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
        private static string Schedule(string activityId) => $$"""
        {
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
