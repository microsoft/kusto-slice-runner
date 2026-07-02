using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class KoLiteSqliteSchemaTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "sqlite-file-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(testDirectory))
            {
                return;
            }

            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(testDirectory, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }

        [Fact]
        public void EnsureSchemaProvisionsAFreshDatabaseWithoutALedger()
        {
            var factory = CreateFactory();

            new KoLiteSqliteSchema(factory).EnsureSchema();

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
            var schema = new KoLiteSqliteSchema(factory);

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
        public void RequiredTablesAndIndexesExist()
        {
            var factory = CreateFactory();
            new KoLiteSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();
            var expectedTables = new[]
            {
                "app_metadata",
                "app_settings",
                "job_definitions",
                "job_definition_events",
                "slice_state_events",
                "current_slice_state",
                "work_queue",
                "failure_summary_runs",
                "operational_logs",
                "scheduled_slices",
                "slice_attempts",
                "repair_batches",
                "repair_slices",
                "rerun_batches",
                "rerun_slices",
                "retention_runs",
                "purge_runs",
                "job_lifecycle_events",
                "system_audit",
                "ingestion_throttle_observations",
            };

            foreach (var table in expectedTables)
            {
                Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;", ("$name", table)));
            }

            var expectedIndexes = new[]
            {
                "ix_job_definition_events_job_recorded",
                "ix_slice_state_events_slice_recorded",
                "ix_current_slice_state_state_due",
                "ix_work_queue_ready",
                "ix_work_queue_slice",
                "ix_failure_summary_runs_job_updated",
                "ix_operational_logs_job_recorded",
                "ix_scheduled_slices_due",
                "ix_slice_attempts_job_completed",
                "ix_slice_attempts_slice_attempt",
                "ix_repair_slices_batch_status",
                "ix_repair_slices_job_slice",
                "ix_repair_batches_job",
                "ix_rerun_batches_root_requested",
                "ix_rerun_slices_batch_status",
                "ix_rerun_slices_job_slice",
                "ix_retention_runs_policy_started",
                "ix_purge_runs_job_requested",
                "ix_job_lifecycle_events_job_recorded",
                "ix_system_audit_subject_recorded",
                "ix_ingestion_throttle_cluster_observed",
                "ix_ingestion_throttle_job",
                "ix_ingestion_throttle_terminal",
                "ix_current_slice_state_last_event",
                "ix_repair_slices_enqueued_queue_item",
            };

            foreach (var index in expectedIndexes)
            {
                Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;", ("$name", index)));
            }

            // The GUID-identity schema ships activity_id (NOT NULL) and its unique index directly.
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('job_definitions') WHERE name = 'activity_id' AND \"notnull\" = 1;"));
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_job_definitions_activity_id';"));
        }

        [Fact]
        public void RepairBatchesHaveJobAssociationColumn()
        {
            var factory = CreateFactory();
            new KoLiteSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();

            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('repair_batches') WHERE name = 'job_id';"));
        }

        [Fact]
        public void IngestionThrottleObservationsHaveTerminalColumn()
        {
            var factory = CreateFactory();
            new KoLiteSqliteSchema(factory).EnsureSchema();

            using var connection = factory.OpenConnection();

            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('ingestion_throttle_observations') WHERE name = 'terminal';"));
        }

        [Fact]
        public void ForeignKeyEnforcementIsEnabled()
        {
            var factory = CreateFactory();
            new KoLiteSqliteSchema(factory).EnsureSchema();

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

            new KoLiteSqliteSchema(factory).EnsureSchema();

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
                new KoLiteSqliteSchema(factory).EnsureSchema(connection);
            }

            using var verification = factory.OpenConnection();
            Assert.Equal(1, QueryInt(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'job_definitions';"));
        }

        private KoLiteSqliteConnectionFactory CreateFactory(int busyTimeoutMilliseconds = 5_000)
        {
            Directory.CreateDirectory(testDirectory);
            var databasePath = Path.Combine(testDirectory, $"kolite-{Guid.NewGuid():N}.db");
            return new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath)
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
