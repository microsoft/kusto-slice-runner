// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace Ksr.Local.Sqlite.Tests
{
    public sealed class KsrSqliteSchemaTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "sqlite-file-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        [Fact]
        public void EnsureSchemaProvisionsAFreshDatabaseWithoutALedger()
        {
            var factory = CreateFactory();

            new KsrSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'job_definitions';"));

            // The schema is provisioned directly, with no migration ledger or version metadata.
            Assert.Equal(0, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'schema_migrations';"));
            Assert.Equal(0, QueryInt(connection, "SELECT COUNT(*) FROM app_metadata WHERE key = 'schema_version';"));
        }

        [Fact]
        public void EnsureSchemaIsIdempotentAndPreservesData()
        {
            var factory = CreateFactory();
            var schema = new KsrSqliteSchema(factory);

            schema.EnsureSchema();
            using (var connection = factory.OpenConnection())
            {
                ExecuteNonQuery(connection, "INSERT INTO job_definitions (job_id, activity_id, display_name, schedule_json) VALUES ('guid1','my.job','my.job','{}');");
            }

            schema.EnsureSchema();

            using (var connection = factory.OpenConnection())
            {
                Assert.Equal("my.job", QueryString(connection, "SELECT activity_id FROM job_definitions WHERE job_id = 'guid1';"));
                Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM job_definitions;"));
            }
        }

        [Fact]
        public void EnsureSchemaUpgradesPreChunkTablesWithoutLosingQueueData()
        {
            var factory = CreateFactory();
            using (var connection = factory.OpenConnection())
            {
                ExecuteNonQuery(connection, """
                    CREATE TABLE job_definitions (
                        job_id TEXT NOT NULL PRIMARY KEY, activity_id TEXT NOT NULL, display_name TEXT NOT NULL,
                        description TEXT NULL, query_ref TEXT NULL, schedule_json TEXT NOT NULL,
                        parameters_json TEXT NOT NULL DEFAULT '{}', is_enabled INTEGER NOT NULL DEFAULT 1,
                        catalog_version INTEGER NOT NULL DEFAULT 1, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
                    CREATE TABLE slice_state_events (
                        event_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NOT NULL, slice_start_utc TEXT NOT NULL,
                        slice_end_utc TEXT NOT NULL, generation_id TEXT NULL, event_type TEXT NOT NULL,
                        state TEXT NULL, reason TEXT NULL, attempt INTEGER NULL, payload_json TEXT NOT NULL DEFAULT '{}',
                        actor TEXT NULL, recorded_at_utc TEXT NOT NULL);
                    CREATE TABLE current_slice_state (
                        job_id TEXT NOT NULL, slice_start_utc TEXT NOT NULL, slice_end_utc TEXT NOT NULL,
                        generation_id TEXT NULL, state TEXT NOT NULL, attempt INTEGER NOT NULL DEFAULT 0,
                        lease_owner TEXT NULL, lease_expires_at_utc TEXT NULL, last_event_id TEXT NULL,
                        last_error_code TEXT NULL, last_error_message TEXT NULL, updated_at_utc TEXT NOT NULL,
                        PRIMARY KEY (job_id, slice_start_utc, slice_end_utc));
                    CREATE TABLE work_queue (
                        queue_item_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NOT NULL, slice_start_utc TEXT NOT NULL,
                        slice_end_utc TEXT NOT NULL, queue_name TEXT NOT NULL DEFAULT 'default', priority INTEGER NOT NULL DEFAULT 0,
                        state TEXT NOT NULL, available_at_utc TEXT NOT NULL, locked_by TEXT NULL, locked_until_utc TEXT NULL,
                        attempts INTEGER NOT NULL DEFAULT 0, max_attempts INTEGER NOT NULL DEFAULT 3,
                        idempotency_key TEXT NOT NULL UNIQUE, payload_json TEXT NOT NULL DEFAULT '{}',
                        created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
                    CREATE TABLE operational_logs (
                        log_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NULL, slice_start_utc TEXT NULL,
                        slice_end_utc TEXT NULL, level TEXT NOT NULL, message TEXT NOT NULL, category TEXT NULL,
                        exception TEXT NULL, properties_json TEXT NOT NULL DEFAULT '{}', recorded_at_utc TEXT NOT NULL);
                    CREATE TABLE slice_attempts (
                        attempt_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NOT NULL, slice_start_utc TEXT NOT NULL,
                        slice_end_utc TEXT NOT NULL, attempt INTEGER NOT NULL, status TEXT NOT NULL, worker_id TEXT NULL,
                        started_at_utc TEXT NULL, completed_at_utc TEXT NULL, error_code TEXT NULL, error_message TEXT NULL,
                        metrics_json TEXT NOT NULL DEFAULT '{}');
                    CREATE TABLE ingestion_throttle_observations (
                        observation_id TEXT NOT NULL PRIMARY KEY, job_id TEXT NOT NULL, cluster_uri TEXT NOT NULL,
                        slice_start_utc TEXT NOT NULL, slice_end_utc TEXT NOT NULL, attempt INTEGER NOT NULL DEFAULT 0,
                        reported_capacity INTEGER NULL, observed_at_utc TEXT NOT NULL, terminal INTEGER NOT NULL DEFAULT 0);

                    INSERT INTO job_definitions (
                        job_id,activity_id,display_name,schedule_json,created_at_utc,updated_at_utc)
                    VALUES ('legacy-job','legacy.job','legacy.job','{}','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                    INSERT INTO current_slice_state (
                        job_id,slice_start_utc,slice_end_utc,state,updated_at_utc)
                    VALUES ('legacy-job','2026-01-01T00:00:00Z','2026-01-01T00:05:00Z','Queued','2026-01-01T00:00:00Z');
                    INSERT INTO work_queue (
                        queue_item_id,job_id,slice_start_utc,slice_end_utc,state,available_at_utc,
                        idempotency_key,created_at_utc,updated_at_utc)
                    VALUES (
                        'legacy-queue','legacy-job','2026-01-01T00:00:00Z','2026-01-01T00:05:00Z',
                        'Queued','2026-01-01T00:00:00Z','legacy-key','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                    """);
            }

            new KsrSqliteSchema(factory).EnsureSchema();

            using var upgraded = factory.OpenConnection();
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM work_queue WHERE queue_item_id='legacy-queue' AND idempotency_key='legacy-key';"));
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM pragma_table_info('work_queue') WHERE name='chunk_id';"));
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM pragma_table_info('slice_attempts') WHERE name='total_chunks';"));
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='current_slice_chunk_state';"));
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM work_queue WHERE chunk_id IS NULL AND total_chunks IS NULL;"));
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='performance_attempts';"));
            Assert.Equal(0, QueryInt(upgraded, "SELECT history_initialized FROM performance_collection_state;"));
            Assert.Equal(0, QueryInt(upgraded, "SELECT COUNT(*) FROM performance_attempts;"));
        }

        [Fact]
        public void RequiredTablesAndIndexesExist()
        {
            var factory = CreateFactory();
            new KsrSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();
            var expectedTables = new[]
            {
                "app_metadata",
                "app_settings",
                "job_definitions",
                "job_definition_events",
                "slice_state_events",
                "current_slice_state",
                "slice_chunk_state_events",
                "current_slice_chunk_state",
                "work_queue",
                "failure_summary_runs",
                "operational_logs",
                "scheduled_slices",
                "slice_attempts",
                "performance_attempts",
                "performance_collection_state",
                "repair_batches",
                "repair_slices",
                "repair_chunk_executions",
                "rerun_batches",
                "rerun_slices",
                "retention_runs",
                "purge_runs",
                "job_lifecycle_events",
                "system_audit",
            };

            foreach (var table in expectedTables)
            {
                Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;", ("$name", table)));
            }

            var expectedIndexes = new[]
            {
                "ix_job_definition_events_job_recorded",
                "ix_slice_state_events_slice_recorded",
                "ix_slice_chunk_state_events_slice_recorded",
                "ix_current_slice_chunk_state_state",
                "ix_current_slice_chunk_state_last_event",
                "ix_current_slice_state_state_due",
                "ix_work_queue_ready",
                "ix_work_queue_slice",
                "ix_failure_summary_runs_job_updated",
                "ix_operational_logs_job_recorded",
                "ix_scheduled_slices_due",
                "ix_slice_attempts_job_completed",
                "ix_slice_attempts_slice_attempt",
                "ix_slice_attempts_completed",
                "ix_performance_attempts_completed",
                "ix_performance_attempts_job_completed",
                "ix_performance_attempts_pending",
                "ix_performance_attempts_target_pending",
                "ux_performance_attempts_server",
                "ix_performance_attempts_correlation",
                "ix_performance_attempts_incomplete",
                "ix_performance_attempts_errors",
                "ix_repair_slices_batch_status",
                "ix_repair_slices_job_slice",
                "ix_repair_chunk_executions_batch_status",
                "ix_repair_chunk_executions_job_slice",
                "ix_repair_chunk_executions_queue_item",
                "ix_repair_batches_job",
                "ix_rerun_batches_root_requested",
                "ix_rerun_slices_batch_status",
                "ix_rerun_slices_job_slice",
                "ix_rerun_slices_history",
                "ix_retention_runs_policy_started",
                "ix_purge_runs_job_requested",
                "ix_job_lifecycle_events_job_recorded",
                "ix_system_audit_subject_recorded",
                "ix_current_slice_state_last_event",
                "ix_repair_slices_enqueued_queue_item",
                "ix_work_queue_slice_chunk",
                "ix_slice_attempts_slice_chunk",
                "ix_current_slice_chunk_state_running_global",
                "ix_work_queue_queued_global",
            };

            foreach (var index in expectedIndexes)
            {
                Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;", ("$name", index)));
            }

            // The GUID-identity schema ships activity_id (NOT NULL) and its unique index directly.
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('job_definitions') WHERE name = 'activity_id' AND \"notnull\" = 1;"));
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_job_definitions_activity_id';"));
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('work_queue') WHERE name = 'chunk_id';"));
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('slice_attempts') WHERE name = 'total_chunks';"));
        }

        [Fact]
        public void PrePerformanceUpgradePreservesCatalogQueueAndRunningLeaseState()
        {
            var factory = CreateFactory();
            var schema = new KsrSqliteSchema(factory);
            schema.EnsureSchema();
            using (var connection = factory.OpenConnection())
            {
                ExecuteNonQuery(connection, """
                    DROP TABLE performance_attempts;
                    DROP TABLE performance_collection_state;
                    INSERT INTO job_definitions(job_id,activity_id,display_name,schedule_json,catalog_version)
                    VALUES ('upgrade-job','upgrade.job','upgrade.job','{}',17);
                    INSERT INTO current_slice_state(
                        job_id,slice_start_utc,slice_end_utc,state,attempt,lease_owner,lease_expires_at_utc,generation_id)
                    VALUES ('upgrade-job','2026-09-01T00:00:00Z','2026-09-01T00:05:00Z','Running',3,'worker','2026-09-01T01:00:00Z','lease-token');
                    INSERT INTO work_queue(
                        queue_item_id,job_id,slice_start_utc,slice_end_utc,state,available_at_utc,attempts,idempotency_key,locked_by,locked_until_utc)
                    VALUES ('queue','upgrade-job','2026-09-01T00:00:00Z','2026-09-01T00:05:00Z',
                            'Leased','2026-09-01T00:00:00Z',3,'unchanged-ingest-identity','worker','2026-09-01T01:00:00Z');
                    INSERT INTO slice_attempts(
                        attempt_id,job_id,slice_start_utc,slice_end_utc,attempt,status,started_at_utc)
                    VALUES ('queue:3','upgrade-job','2026-09-01T00:00:00Z','2026-09-01T00:05:00Z',3,'Started','2026-09-01T00:10:00Z');
                    """);
            }

            schema.EnsureSchema();
            schema.EnsureSchema();

            using var upgraded = factory.OpenConnection();
            Assert.Equal(17, QueryInt(upgraded, "SELECT catalog_version FROM job_definitions;"));
            Assert.Equal("Running", QueryString(upgraded, "SELECT state FROM current_slice_state;"));
            Assert.Equal("lease-token", QueryString(upgraded, "SELECT generation_id FROM current_slice_state;"));
            Assert.Equal("Leased", QueryString(upgraded, "SELECT state FROM work_queue;"));
            Assert.Equal("unchanged-ingest-identity", QueryString(upgraded, "SELECT idempotency_key FROM work_queue;"));
            Assert.Equal("Started", QueryString(upgraded, "SELECT status FROM slice_attempts;"));
            Assert.Equal(0, QueryInt(upgraded, "SELECT COUNT(*) FROM performance_attempts;"));
            Assert.Equal(1, QueryInt(upgraded, "SELECT COUNT(*) FROM pragma_foreign_key_list('performance_attempts');"));
            Assert.Equal("job_definitions", QueryString(upgraded, "SELECT \"table\" FROM pragma_foreign_key_list('performance_attempts');"));
            Assert.Equal("CASCADE", QueryString(upgraded, "SELECT on_delete FROM pragma_foreign_key_list('performance_attempts');"));
        }

        [Fact]
        public void ActivityExecutionCountsUseGlobalStateIndexes()
        {
            var factory = CreateFactory();
            new KsrSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();
            var chunkPlan = QueryPlan(connection, "SELECT COUNT(*) FROM current_slice_chunk_state WHERE state='Running';");
            var queuePlan = QueryPlan(connection, "SELECT COUNT(*) FROM work_queue WHERE state='Queued';");

            Assert.Contains("ix_current_slice_chunk_state_running_global", chunkPlan, StringComparison.Ordinal);
            Assert.Contains("ix_work_queue_queued_global", queuePlan, StringComparison.Ordinal);
        }

        [Fact]
        public void RepairBatchesHaveJobAssociationColumn()
        {
            var factory = CreateFactory();
            new KsrSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();

            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('repair_batches') WHERE name = 'job_id';"));
        }

        [Fact]
        public void ForeignKeyEnforcementIsEnabled()
        {
            var factory = CreateFactory();
            new KsrSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();
            Assert.Equal(1, QueryInt(connection, "PRAGMA foreign_keys;"));

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO repair_slices (repair_slice_id, repair_batch_id, job_id, slice_start_utc, slice_end_utc, status)
                VALUES ('repair-slice-1', 'missing-batch', 'missing-job', '2026-01-01T00:00:00Z', '2026-01-01T01:00:00Z', 'Pending');
                """;
            Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        }

        [Fact]
        public void SchemaEstablishesWalAndFactoryAppliesConnectionPragmas()
        {
            var factory = CreateFactory(busyTimeoutMilliseconds: 7_500);

            // WAL is a persistent database property established once by EnsureSchema, not re-applied on
            // every open. Before the schema is applied a fresh connection is in the default journal
            // mode, but the connection-scoped pragmas (busy_timeout, foreign_keys, synchronous) are
            // applied on every open.
            using (var beforeSchema = factory.OpenConnection())
            {
                Assert.Equal("delete", QueryString(beforeSchema, "PRAGMA journal_mode;"));
                Assert.Equal(7_500, QueryInt(beforeSchema, "PRAGMA busy_timeout;"));
                Assert.Equal(1, QueryInt(beforeSchema, "PRAGMA foreign_keys;"));
                Assert.Equal(1, QueryInt(beforeSchema, "PRAGMA synchronous;"));
            }

            new KsrSqliteSchema(factory).EnsureSchema();

            using var afterSchema = factory.OpenConnection();
            Assert.Equal("wal", QueryString(afterSchema, "PRAGMA journal_mode;"));
            Assert.Equal(7_500, QueryInt(afterSchema, "PRAGMA busy_timeout;"));
            Assert.Equal(1, QueryInt(afterSchema, "PRAGMA foreign_keys;"));
            Assert.Equal(1, QueryInt(afterSchema, "PRAGMA synchronous;"));
        }

        [Fact]
        public void SchemaCanBeAppliedThroughConnectionFactory()
        {
            var factory = CreateFactory();

            using (var connection = factory.OpenConnection())
            {
                new KsrSqliteSchema(factory).EnsureSchema(connection);
            }

            using var verification = factory.OpenConnection();
            Assert.Equal(1, QueryInt(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'job_definitions';"));
        }

        private KsrSqliteConnectionFactory CreateFactory(int busyTimeoutMilliseconds = 5_000)
        {
            Directory.CreateDirectory(testDirectory);
            var databasePath = Path.Combine(testDirectory, $"ksr-{Guid.NewGuid():N}.db");
            return new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(databasePath)
            {
                BusyTimeoutMilliseconds = busyTimeoutMilliseconds,
            });
        }

        private static int QueryInt(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            return Convert.ToInt32(QueryScalar(connection, sql, parameters));
        }

        private static string QueryString(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            return Convert.ToString(QueryScalar(connection, sql, parameters)) ?? string.Empty;
        }

        private static string QueryPlan(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            using var reader = command.ExecuteReader();
            var details = new List<string>();
            while (reader.Read())
            {
                details.Add(reader.GetString(3));
            }

            return string.Join(Environment.NewLine, details);
        }

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static object? QueryScalar(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return command.ExecuteScalar();
        }
    }
}
