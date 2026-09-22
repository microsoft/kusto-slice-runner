// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    // Regression guard for the second hard-delete hang root cause (distinct from shared cache): the
    // foreign key current_slice_state.last_event_id -> slice_state_events(event_id) ON DELETE SET NULL
    // had no index on the referencing column. So deleting a job's slice_state_events forced SQLite to
    // full-scan current_slice_state once per deleted event to enforce the SET NULL. On a real
    // high-volume job (observed live: 12,021 events x 98,812 states ~= 1.2 billion row scans) the purge
    // ran for many minutes, holding the single WAL writer and starving the worker (SQLITE_BUSY
    // "database is locked"). The schema indexes the FK column; these tests fail without it.
    public sealed class HardDeleteForeignKeyIndexTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "hard-delete-fk-index-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;

        public HardDeleteForeignKeyIndexTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "fk.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        // Deterministic root-cause guard: the FK enforcement lookup must be index-backed. Without the
        // FK-column index the query plan is "SCAN current_slice_state" (the quadratic purge); with it
        // the plan is "SEARCH ... USING INDEX ix_current_slice_state_last_event".
        [Fact]
        public void Fk_set_null_lookup_on_current_slice_state_uses_an_index_not_a_full_scan()
        {
            using var c = factory.OpenConnection();
            var plan = QueryPlan(c, "SELECT 1 FROM current_slice_state WHERE last_event_id = 'x';");

            Assert.Contains("ix_current_slice_state_last_event", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN current_slice_state", plan, StringComparison.Ordinal);
        }

        // The other unindexed ON DELETE SET NULL FK (work_queue delete scans repair_slices).
        [Fact]
        public void Fk_set_null_lookup_on_repair_slices_uses_an_index_not_a_full_scan()
        {
            using var c = factory.OpenConnection();
            var plan = QueryPlan(c, "SELECT 1 FROM repair_slices WHERE enqueued_queue_item_id = 'x';");

            Assert.Contains("ix_repair_slices_enqueued_queue_item", plan, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN repair_slices", plan, StringComparison.Ordinal);
        }

        // Behavioral guard at production-like scale: a job with many slice_state_events, hard-deleted
        // while a large current_slice_state table exists, must purge quickly. Without the FK index this
        // is O(events x states) and runs for tens of seconds to minutes (the hang); with it the purge is
        // a handful of index seeks and completes well within the bound.
        [Fact]
        public void Hard_delete_of_a_high_volume_job_is_not_quadratic()
        {
            var catalog = new SqliteJobCatalogRepository(factory);
            var lifecycle = new SqliteJobLifecycleService(factory, catalog);

            catalog.Create(Schedule("noise"));
            var victim = catalog.Create(Schedule("victim"));
            catalog.SetEnabled(victim.JobId, enabled: false, expectedVersion: victim.CatalogVersion);

            // Mirror the live shape that hung: a large current_slice_state table (noise job) and many
            // slice_state_events for the victim. Without the index the purge scans all states per event.
            const int noiseStates = 90_000;
            const int victimEvents = 12_000;
            SeedCurrentSliceStates(JobId("noise"), noiseStates);
            SeedSliceStateEvents(victim.JobId, victimEvents);

            var sw = Stopwatch.StartNew();
            lifecycle.HardDelete(victim.JobId, $"DELETE {victim.DisplayName}", actor: "test", reason: "fk-index-regression");
            sw.Stop();

            Assert.True(
                sw.Elapsed < TimeSpan.FromSeconds(20),
                $"Hard delete of a {victimEvents}-event job against {noiseStates} slice states took {sw.Elapsed.TotalSeconds:0.0}s (unindexed FK quadratic-purge regression).");
            Assert.Null(catalog.Get(victim.JobId));
        }

        private static string QueryPlan(SqliteConnection c, string sql)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var r = cmd.ExecuteReader();
            var details = new List<string>();
            while (r.Read()) details.Add(r.GetString(3));
            return string.Join(" | ", details);
        }

        private void SeedCurrentSliceStates(string jobId, int count)
        {
            using var c = factory.OpenConnection();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO current_slice_state (job_id,slice_start_utc,slice_end_utc,state,attempt,last_event_id,updated_at_utc) VALUES ($j,$s,$e,'Completed',0,NULL,$u);";
            var s = cmd.CreateParameter(); s.ParameterName = "$s"; cmd.Parameters.Add(s);
            var e = cmd.CreateParameter(); e.ParameterName = "$e"; cmd.Parameters.Add(e);
            var j = cmd.CreateParameter(); j.ParameterName = "$j"; j.Value = jobId; cmd.Parameters.Add(j);
            var u = cmd.CreateParameter(); u.ParameterName = "$u"; u.Value = Ts(0); cmd.Parameters.Add(u);
            for (var i = 0; i < count; i++)
            {
                s.Value = Ts(i);
                e.Value = Ts(i, minutes: 5);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        private void SeedSliceStateEvents(string jobId, int count)
        {
            using var c = factory.OpenConnection();
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO slice_state_events (event_id,job_id,slice_start_utc,slice_end_utc,event_type,state,attempt,payload_json,recorded_at_utc) VALUES ($id,$j,$s,$e,'Completed','Completed',1,'{}',$u);";
            var id = cmd.CreateParameter(); id.ParameterName = "$id"; cmd.Parameters.Add(id);
            var s = cmd.CreateParameter(); s.ParameterName = "$s"; cmd.Parameters.Add(s);
            var e = cmd.CreateParameter(); e.ParameterName = "$e"; cmd.Parameters.Add(e);
            var j = cmd.CreateParameter(); j.ParameterName = "$j"; j.Value = jobId; cmd.Parameters.Add(j);
            var u = cmd.CreateParameter(); u.ParameterName = "$u"; u.Value = Ts(0); cmd.Parameters.Add(u);
            for (var i = 0; i < count; i++)
            {
                id.Value = Guid.NewGuid().ToString("N");
                s.Value = Ts(i);
                e.Value = Ts(i, minutes: 5);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }

        private static string Ts(int index, int minutes = 0) =>
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index + minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "StateFunction",
          "outputTable": "StateOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": true,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": [],
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
