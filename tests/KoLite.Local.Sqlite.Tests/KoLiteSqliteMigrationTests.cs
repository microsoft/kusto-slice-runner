using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class KoLiteSqliteMigrationTests : IDisposable
    {
        private const string BaselineSchemaChecksum = "07743713f19cd9c306aa330d12a44f9f30be486ce794e25134d17370c225661c";
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
        public void NewDatabaseMigratesFromEmptyToLatestVersion()
        {
            var factory = CreateFactory();
            var migrator = new KoLiteSqliteMigrator(factory);

            migrator.Migrate();

            using var connection = factory.OpenConnection();
            Assert.Equal(KoLiteSqliteMigrator.LatestVersion, QueryInt(connection, "SELECT MAX(version) FROM schema_migrations;"));
            Assert.Equal(KoLiteSqliteMigrator.LatestVersion.ToString(), QueryString(connection, "SELECT value FROM app_metadata WHERE key = 'schema_version';"));
        }

        [Fact]
        public void RerunningMigrationsIsIdempotentAndPreservesLedgerRows()
        {
            var factory = CreateFactory();
            var migrator = new KoLiteSqliteMigrator(factory);

            migrator.Migrate();
            using var firstConnection = factory.OpenConnection();
            var firstLedgerCount = QueryInt(firstConnection, "SELECT COUNT(*) FROM schema_migrations;");
            var firstAppliedAt = QueryString(firstConnection, "SELECT applied_at_utc FROM schema_migrations WHERE version = 1;");
            firstConnection.Dispose();

            migrator.Migrate();

            using var secondConnection = factory.OpenConnection();
            Assert.Equal(firstLedgerCount, QueryInt(secondConnection, "SELECT COUNT(*) FROM schema_migrations;"));
            Assert.Equal(firstAppliedAt, QueryString(secondConnection, "SELECT applied_at_utc FROM schema_migrations WHERE version = 1;"));
        }

        [Fact]
        public void BaselineSchemaChecksumIsStable()
        {
            var factory = CreateFactory();
            new KoLiteSqliteMigrator(factory).Migrate();

            using var connection = factory.OpenConnection();

            Assert.Equal(BaselineSchemaChecksum, QueryString(connection, "SELECT checksum FROM schema_migrations WHERE version = 1;"));
        }

        [Fact]
        public void LegacyMigrationLedgerIsCollapsedToBaselineInPlace()
        {
            var factory = CreateFactory();
            var migrator = new KoLiteSqliteMigrator(factory);
            migrator.Migrate();

            using (var connection = factory.OpenConnection())
            {
                ExecuteNonQuery(connection, "INSERT INTO job_definitions (job_id, activity_id, display_name, schedule_json) VALUES ('guid1','my.job','my.job','{}');");
                ExecuteNonQuery(connection, "DELETE FROM schema_migrations;");
                ExecuteNonQuery(connection, """
                    INSERT INTO schema_migrations (version, name, checksum) VALUES
                        (1,'initial-local-first-schema','old1'),
                        (2,'repair-batches-job-association','old2'),
                        (3,'rerun-batches','old3'),
                        (4,'drop-activity-cursors','old4'),
                        (5,'slice-attempts-job-completed-index','old5'),
                        (6,'rekey-activity-id-to-guid','old6');
                    """);
            }

            migrator.Migrate();

            using (var connection = factory.OpenConnection())
            {
                Assert.Equal(KoLiteSqliteMigrator.LatestVersion, QueryInt(connection, "SELECT COUNT(*) FROM schema_migrations;"));
                Assert.Equal("baseline-guid-schema", QueryString(connection, "SELECT name FROM schema_migrations WHERE version = 1;"));
                Assert.Equal(BaselineSchemaChecksum, QueryString(connection, "SELECT checksum FROM schema_migrations WHERE version = 1;"));
                Assert.Equal(KoLiteSqliteMigrator.LatestVersion.ToString(), QueryString(connection, "SELECT value FROM app_metadata WHERE key = 'schema_version';"));
                Assert.Equal("my.job", QueryString(connection, "SELECT activity_id FROM job_definitions WHERE job_id = 'guid1';"));
            }

            // A subsequent run does not reconcile again or duplicate the ledger.
            migrator.Migrate();
            using (var connection = factory.OpenConnection())
            {
                Assert.Equal(KoLiteSqliteMigrator.LatestVersion, QueryInt(connection, "SELECT COUNT(*) FROM schema_migrations;"));
            }
        }

        [Fact]
        public void RerunningMigrationsFailsWhenAppliedChecksumDrifts()
        {
            var factory = CreateFactory();
            var migrator = new KoLiteSqliteMigrator(factory);

            migrator.Migrate();
            using (var connection = factory.OpenConnection())
            {
                ExecuteNonQuery(connection, "UPDATE schema_migrations SET checksum = 'tampered-checksum' WHERE version = 1;");
            }

            var exception = Assert.Throws<InvalidOperationException>(() => migrator.Migrate());
            Assert.Contains("SQLite migration 1 (baseline-guid-schema) checksum mismatch", exception.Message);
            Assert.Contains("tampered-checksum", exception.Message);
        }

        [Fact]
        public void RequiredTablesAndIndexesExist()
        {
            var factory = CreateFactory();
            new KoLiteSqliteMigrator(factory).Migrate();

            using var connection = factory.OpenConnection();
            var expectedTables = new[]
            {
                "schema_migrations",
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
            };

            foreach (var index in expectedIndexes)
            {
                Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;", ("$name", index)));
            }

            Assert.Equal(0, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'activity_cursors';"));
            Assert.Equal(0, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_activity_cursors_job';"));

            // The GUID-identity baseline ships activity_id (NOT NULL) and its unique index directly.
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('job_definitions') WHERE name = 'activity_id' AND \"notnull\" = 1;"));
            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_job_definitions_activity_id';"));
        }

        [Fact]
        public void RepairBatchesHaveJobAssociationColumn()
        {
            var factory = CreateFactory();
            new KoLiteSqliteMigrator(factory).Migrate();

            using var connection = factory.OpenConnection();

            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('repair_batches') WHERE name = 'job_id';"));
        }

        [Fact]
        public void IngestionThrottleObservationsHaveTerminalColumn()
        {
            var factory = CreateFactory();
            new KoLiteSqliteMigrator(factory).Migrate();

            using var connection = factory.OpenConnection();

            Assert.Equal(1, QueryInt(connection, "SELECT COUNT(*) FROM pragma_table_info('ingestion_throttle_observations') WHERE name = 'terminal';"));
        }

        [Fact]
        public void ForeignKeyEnforcementIsEnabled()
        {
            var factory = CreateFactory();
            new KoLiteSqliteMigrator(factory).Migrate();

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
        public void FactoryAppliesWalModeAndBusyTimeout()
        {
            var factory = CreateFactory(busyTimeoutMilliseconds: 7_500);

            using var connection = factory.OpenConnection();

            Assert.Equal("wal", QueryString(connection, "PRAGMA journal_mode;"));
            Assert.Equal(7_500, QueryInt(connection, "PRAGMA busy_timeout;"));
            Assert.Equal(1, QueryInt(connection, "PRAGMA foreign_keys;"));
            Assert.Equal(1, QueryInt(connection, "PRAGMA synchronous;"));
        }

        [Fact]
        public void MigrationCanBeOpenedThroughConnectionFactory()
        {
            var factory = CreateFactory();

            using (var connection = factory.OpenConnection())
            {
                new KoLiteSqliteMigrator(factory).Migrate(connection);
            }

            using var verification = factory.OpenConnection();
            Assert.Equal(KoLiteSqliteMigrator.LatestVersion, QueryInt(verification, "SELECT MAX(version) FROM schema_migrations;"));
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
