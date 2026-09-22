// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SchemaRetirementTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "schema-retirement-tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public void Fresh_schema_has_no_throttle_observation_storage()
        {
            var factory = CreateFactory();
            new KoLiteSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();
            AssertRetiredStorageAbsent(connection);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Upgrade_drops_legacy_observations_and_preserves_other_records(bool hasChunkColumns)
        {
            var factory = CreateFactory();
            var schema = new KoLiteSqliteSchema(factory);
            schema.EnsureSchema();
            using var connection = factory.OpenConnection();
            var chunkColumns = hasChunkColumns ? ", chunk_id INTEGER NULL, total_chunks INTEGER NULL" : string.Empty;
            Execute(connection, $$"""
                CREATE TABLE ingestion_throttle_observations (
                    observation_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NOT NULL,
                    cluster_uri TEXT NOT NULL, slice_start_utc TEXT NOT NULL, slice_end_utc TEXT NOT NULL,
                    attempt INTEGER NOT NULL DEFAULT 0, reported_capacity INTEGER NULL,
                    observed_at_utc TEXT NOT NULL, terminal INTEGER NOT NULL DEFAULT 0{{chunkColumns}});
                CREATE INDEX ix_ingestion_throttle_cluster_observed ON ingestion_throttle_observations(cluster_uri,observed_at_utc);
                CREATE INDEX ix_ingestion_throttle_job ON ingestion_throttle_observations(job_id);
                CREATE INDEX ix_ingestion_throttle_terminal ON ingestion_throttle_observations(terminal,observed_at_utc);
                INSERT INTO ingestion_throttle_observations
                    (observation_id,job_id,cluster_uri,slice_start_utc,slice_end_utc,observed_at_utc,terminal)
                    VALUES ('old-hit','job','https://example.invalid','2026-01-01','2026-01-02','2026-01-03',1);

                INSERT INTO job_definitions (job_id,activity_id,display_name,schedule_json)
                    VALUES ('job','existing.job','Existing job','{}');
                INSERT INTO job_definition_events (event_id,job_id,catalog_version,event_type,payload_json)
                    VALUES ('catalog-event','job',1,'Created','{"actor":"original"}');
                INSERT INTO slice_state_events (event_id,job_id,slice_start_utc,slice_end_utc,event_type,state,attempt,reason,payload_json)
                    VALUES ('parent-event','job','2026-01-01','2026-01-02','Failed','Failed',1,
                        'CapacityPolicy/Ingestion','{"ErrorCode":"KustoRequestThrottledException"}');
                INSERT INTO current_slice_state (job_id,slice_start_utc,slice_end_utc,state,attempt,last_event_id,last_error_message)
                    VALUES ('job','2026-01-01','2026-01-02','Failed',1,'parent-event','CapacityPolicy/Ingestion');
                INSERT INTO slice_chunk_state_events
                    (event_id,job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,state,attempt,reason)
                    VALUES ('child-event','job','2026-01-01','2026-01-02',0,2,'Failed',1,'CapacityPolicy/Ingestion');
                INSERT INTO current_slice_chunk_state
                    (job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,state,attempt,last_event_id,last_error_message)
                    VALUES ('job','2026-01-01','2026-01-02',0,2,'Failed',1,'child-event','CapacityPolicy/Ingestion');
                INSERT INTO work_queue
                    (queue_item_id,job_id,slice_start_utc,slice_end_utc,state,available_at_utc,idempotency_key,chunk_id,total_chunks)
                    VALUES ('queued','job','2026-01-01','2026-01-02','Queued','2026-01-03','queued-key',0,2);
                INSERT INTO work_queue
                    (queue_item_id,job_id,slice_start_utc,slice_end_utc,state,available_at_utc,idempotency_key,locked_by,locked_until_utc,chunk_id,total_chunks)
                    VALUES ('leased','job','2026-01-01','2026-01-02','Leased','2026-01-03','leased-key','worker','2099-01-01',1,2);
                INSERT INTO slice_attempts
                    (attempt_id,job_id,slice_start_utc,slice_end_utc,attempt,status,error_code,error_message,chunk_id,total_chunks)
                    VALUES ('attempt','job','2026-01-01','2026-01-02',1,'FailedRetryable',
                        'KustoRequestThrottledException','CapacityPolicy/Ingestion',0,2);
                INSERT INTO operational_logs (log_id,job_id,level,message,category)
                    VALUES ('log','job','Warning','CapacityPolicy/Ingestion','worker');
                INSERT INTO system_audit (audit_id,actor,action,subject_type,subject_id,payload_json)
                    VALUES ('audit','original','HardDeleted','Job','old-job','{"throttleObservationRows":7}');
                INSERT INTO retention_runs (retention_run_id,policy_name,status,cutoff_utc,details_json)
                    VALUES ('retention','read-model-retention','Completed','2026-01-01','{"ingestionThrottlesDeleted":2}');
                INSERT INTO repair_batches (repair_batch_id,job_id,reason,status)
                    VALUES ('repair','job','Original repair','Queued');
                INSERT INTO repair_slices (repair_slice_id,repair_batch_id,job_id,slice_start_utc,slice_end_utc,status,enqueued_queue_item_id)
                    VALUES ('repair-slice','repair','job','2026-01-01','2026-01-02','Queued','queued');
                INSERT INTO repair_chunk_executions
                    (repair_chunk_execution_id,repair_batch_id,job_id,slice_start_utc,slice_end_utc,chunk_id,total_chunks,previous_state,previous_attempt,status)
                    VALUES ('repair-child','repair','job','2026-01-01','2026-01-02',0,2,'Failed',1,'Queued');
                INSERT INTO rerun_batches (rerun_batch_id,root_job_id,root_start_utc,root_end_utc,reason,status)
                    VALUES ('rerun','job','2026-01-01','2026-01-02','Original rerun','Planned');
                INSERT INTO rerun_slices (rerun_slice_id,rerun_batch_id,job_id,slice_start_utc,slice_end_utc,role,status,snapshot_json)
                    VALUES ('rerun-slice','rerun','job','2026-01-01','2026-01-02','Root','Planned','{"error":"CapacityPolicy/Ingestion"}');
                """);
            if (hasChunkColumns)
            {
                Execute(connection, "UPDATE ingestion_throttle_observations SET chunk_id=0,total_chunks=2;");
            }

            var preservedTables = new[]
            {
                "job_definitions", "job_definition_events", "slice_state_events", "current_slice_state",
                "slice_chunk_state_events", "current_slice_chunk_state", "work_queue", "slice_attempts",
                "operational_logs", "system_audit", "retention_runs", "repair_batches", "repair_slices",
                "repair_chunk_executions", "rerun_batches", "rerun_slices"
            };
            var before = preservedTables.ToDictionary(table => table, table => SnapshotRows(connection, table));

            schema.EnsureSchema();
            schema.EnsureSchema();

            AssertRetiredStorageAbsent(connection);
            foreach (var table in preservedTables)
            {
                Assert.Equal(before[table], SnapshotRows(connection, table));
            }
        }

        [Fact]
        public void Retirement_preserves_performance_measurements_and_collection_checkpoint()
        {
            using var store = new PerformanceTestStore();
            var job = store.CreateJob("retirement-performance");
            store.Capture("performance-attempt", job, PerformanceTestStore.At(10), PerformanceTestStore.At(11));
            var pending = store.Repository.GetPendingAttempts(PerformanceTestStore.At(20));
            store.Repository.ApplyStatistics(
                pending, [PerformanceTestStore.Statistics(Assert.Single(pending), cpu: 12, duration: 3, memory: 4096)],
                PerformanceTestStore.At(20));
            store.Repository.BeginHistoryReconciliation(PerformanceTestStore.At(20));
            store.Repository.RecordPassSuccess(PerformanceTestStore.At(20));
            store.Execute("""
                CREATE TABLE ingestion_throttle_observations (
                    observation_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NOT NULL,
                    cluster_uri TEXT NOT NULL, slice_start_utc TEXT NOT NULL, slice_end_utc TEXT NOT NULL,
                    attempt INTEGER NOT NULL DEFAULT 0, reported_capacity INTEGER NULL,
                    observed_at_utc TEXT NOT NULL, terminal INTEGER NOT NULL DEFAULT 0);
                INSERT INTO ingestion_throttle_observations
                    (observation_id,job_id,cluster_uri,slice_start_utc,slice_end_utc,observed_at_utc)
                    VALUES ('old-hit',$job,'https://example.invalid','2026-01-01','2026-01-02','2026-01-03');
                """, ("$job", job.JobId));
            using var connection = store.Factory.OpenConnection();
            var measurements = SnapshotRows(connection, "performance_attempts");
            var checkpoint = SnapshotRows(connection, "performance_collection_state");

            new KoLiteSqliteSchema(store.Factory).EnsureSchema();

            AssertRetiredStorageAbsent(connection);
            Assert.Equal(measurements, SnapshotRows(connection, "performance_attempts"));
            Assert.Equal(checkpoint, SnapshotRows(connection, "performance_collection_state"));
        }

        public void Dispose() => TestCleanup.DeleteDirectoryWithRetry(testDirectory);

        private KoLiteSqliteConnectionFactory CreateFactory()
        {
            Directory.CreateDirectory(testDirectory);
            return new KoLiteSqliteConnectionFactory(
                new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "upgrade.db")));
        }

        private static void AssertRetiredStorageAbsent(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name='ingestion_throttle_observations'
                   OR name IN ('ix_ingestion_throttle_cluster_observed','ix_ingestion_throttle_job','ix_ingestion_throttle_terminal');
                """;
            Assert.Equal(0L, command.ExecuteScalar());
        }

        private static string SnapshotRows(SqliteConnection connection, string table)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rows.Add(values);
            }
            return JsonSerializer.Serialize(rows);
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
