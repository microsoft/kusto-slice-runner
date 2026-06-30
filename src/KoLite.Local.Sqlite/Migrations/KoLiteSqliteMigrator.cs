using System.Security.Cryptography;
using System.Text;
using KoLite.Local.Sqlite.Connections;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Migrations
{
    public sealed class KoLiteSqliteMigrator
    {
        private const int BaselineVersion = 1;
        private const string BaselineName = "baseline-guid-schema";

        // The schema is a single, idempotent baseline: a fresh database gets the full GUID-identity
        // schema directly (no historical migration chain to replay). A legacy pre-consolidation
        // ledger (the old 1..6 chain) is collapsed to this baseline once, in place, on first run.
        // Additive post-baseline migrations (version >= 2) are appended here and replay normally on
        // databases already provisioned at the baseline.
        private static readonly SqliteMigration[] Migrations =
        [
            new(BaselineVersion, BaselineName, BaselineSchemaSql),
            new(2, "ingestion-throttle-observations", IngestionThrottleObservationsSql),
            new(3, "ingestion-throttle-terminal", IngestionThrottleTerminalSql),
            new(4, "foreign-key-delete-indexes", ForeignKeyDeleteIndexesSql),
        ];

        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public KoLiteSqliteMigrator(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        public static int LatestVersion => Migrations[^1].Version;

        public void Migrate()
        {
            using var connection = connectionFactory.OpenConnection();
            Migrate(connection);
        }

        public void Migrate(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            // Establish WAL exactly once, at startup, before any hosted service opens a pooled
            // connection. WAL is a persistent database-header property, so per-connection ApplyPragmas
            // no longer sets it (doing so took a write lock on every open, including read-only paths).
            // WAL must run outside a transaction, so it is set here before any ledger work begins.
            ExecuteNonQuery(connection, null, "PRAGMA journal_mode = WAL;");

            EnsureLedger(connection);
            CollapseLegacyLedger(connection);

            foreach (var migration in Migrations)
            {
                if (HasApplied(connection, migration))
                {
                    continue;
                }

                ApplyMigration(connection, migration);
            }

            SetMetadata(connection, "schema_version", LatestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Applies a not-yet-recorded migration and stamps the ledger. Additive migrations are written
        // to be idempotent (CREATE ... IF NOT EXISTS). SQLite has no ADD COLUMN IF NOT EXISTS, so a
        // replay onto a schema that already has the column (e.g. a legacy-ledger collapse on a database
        // that is already at the latest schema) surfaces as a duplicate-column error; that is treated
        // as a no-op and the migration is still recorded so the ledger converges.
        private static void ApplyMigration(SqliteConnection connection, SqliteMigration migration)
        {
            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    ExecuteNonQuery(connection, transaction, migration.Sql);
                    InsertLedger(connection, transaction, migration);
                    transaction.Commit();
                    return;
                }
                catch (SqliteException ex) when (IsAlreadyPresentColumn(ex))
                {
                    transaction.Rollback();
                }
            }

            using var stamp = connection.BeginTransaction();
            InsertLedger(connection, stamp, migration);
            stamp.Commit();
        }

        private static bool IsAlreadyPresentColumn(SqliteException ex) =>
            ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase);

        // One-time, in-place collapse of a pre-consolidation ledger to the single baseline. Detected
        // precisely by the legacy version-1 migration name, so it never fires on a baseline database
        // or on any future post-baseline migration. The idempotent baseline that follows makes no
        // schema change to an already-provisioned database.
        private static void CollapseLegacyLedger(SqliteConnection connection)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT 1 FROM schema_migrations WHERE version = 1 AND name <> $baseline LIMIT 1;";
            check.Parameters.AddWithValue("$baseline", BaselineName);
            if (check.ExecuteScalar() is null)
            {
                return;
            }

            ExecuteNonQuery(connection, null, "DELETE FROM schema_migrations;");
        }

        private static void EnsureLedger(SqliteConnection connection)
        {
            ExecuteNonQuery(connection, null, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER NOT NULL PRIMARY KEY,
                    name TEXT NOT NULL,
                    checksum TEXT NOT NULL,
                    applied_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                );
                """);
        }

        private static bool HasApplied(SqliteConnection connection, SqliteMigration migration)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT checksum FROM schema_migrations WHERE version = $version LIMIT 1;";
            command.Parameters.AddWithValue("$version", migration.Version);
            var appliedChecksum = command.ExecuteScalar() as string;
            if (appliedChecksum is null)
            {
                return false;
            }

            var expectedChecksum = Sha256(migration.Sql);
            if (!StringComparer.Ordinal.Equals(appliedChecksum, expectedChecksum))
            {
                throw new InvalidOperationException($"SQLite migration {migration.Version} ({migration.Name}) checksum mismatch. Expected {expectedChecksum}, found {appliedChecksum}.");
            }

            return true;
        }

        private static void InsertLedger(SqliteConnection connection, SqliteTransaction transaction, SqliteMigration migration)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO schema_migrations (version, name, checksum)
                VALUES ($version, $name, $checksum);
                """;
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$checksum", Sha256(migration.Sql));
            command.ExecuteNonQuery();
        }

        private static void SetMetadata(SqliteConnection connection, string key, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_metadata (key, value, updated_at_utc)
                VALUES ($key, $value, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }

        private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static string Sha256(string text)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private sealed record SqliteMigration(int Version, string Name, string Sql);

        private const string BaselineSchemaSql = """
            CREATE TABLE IF NOT EXISTS app_metadata (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE IF NOT EXISTS app_settings (
                key TEXT NOT NULL PRIMARY KEY,
                value_json TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE IF NOT EXISTS job_definitions (
                job_id TEXT NOT NULL PRIMARY KEY,
                activity_id TEXT NOT NULL,
                display_name TEXT NOT NULL,
                description TEXT NULL,
                query_ref TEXT NULL,
                schedule_json TEXT NOT NULL,
                parameters_json TEXT NOT NULL DEFAULT '{}',
                is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
                catalog_version INTEGER NOT NULL DEFAULT 1,
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE IF NOT EXISTS job_definition_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                catalog_version INTEGER NOT NULL,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                recorded_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS slice_state_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                generation_id TEXT NULL,
                event_type TEXT NOT NULL,
                state TEXT NULL,
                reason TEXT NULL,
                attempt INTEGER NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                actor TEXT NULL,
                recorded_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS current_slice_state (
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                generation_id TEXT NULL,
                state TEXT NOT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,
                lease_owner TEXT NULL,
                lease_expires_at_utc TEXT NULL,
                last_event_id TEXT NULL,
                last_error_code TEXT NULL,
                last_error_message TEXT NULL,
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                PRIMARY KEY (job_id, slice_start_utc, slice_end_utc),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE,
                FOREIGN KEY (last_event_id) REFERENCES slice_state_events(event_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS work_queue (
                queue_item_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                queue_name TEXT NOT NULL DEFAULT 'default',
                priority INTEGER NOT NULL DEFAULT 0,
                state TEXT NOT NULL,
                available_at_utc TEXT NOT NULL,
                locked_by TEXT NULL,
                locked_until_utc TEXT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                max_attempts INTEGER NOT NULL DEFAULT 3,
                idempotency_key TEXT NOT NULL UNIQUE,
                payload_json TEXT NOT NULL DEFAULT '{}',
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE,
                FOREIGN KEY (job_id, slice_start_utc, slice_end_utc)
                    REFERENCES current_slice_state(job_id, slice_start_utc, slice_end_utc) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS failure_summary_runs (
                run_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NULL,
                slice_start_utc TEXT NULL,
                slice_end_utc TEXT NULL,
                summary_kind TEXT NOT NULL,
                failure_code TEXT NULL,
                failure_count INTEGER NOT NULL DEFAULT 0,
                first_seen_utc TEXT NULL,
                last_seen_utc TEXT NULL,
                summary_json TEXT NOT NULL DEFAULT '{}',
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS operational_logs (
                log_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NULL,
                slice_start_utc TEXT NULL,
                slice_end_utc TEXT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL,
                category TEXT NULL,
                exception TEXT NULL,
                properties_json TEXT NOT NULL DEFAULT '{}',
                recorded_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS scheduled_slices (
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                generation_id TEXT NULL,
                status TEXT NOT NULL,
                scheduled_at_utc TEXT NOT NULL,
                due_at_utc TEXT NOT NULL,
                PRIMARY KEY (job_id, slice_start_utc, slice_end_utc),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS slice_attempts (
                attempt_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                attempt INTEGER NOT NULL,
                status TEXT NOT NULL,
                worker_id TEXT NULL,
                started_at_utc TEXT NULL,
                completed_at_utc TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                metrics_json TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (job_id, slice_start_utc, slice_end_utc)
                    REFERENCES current_slice_state(job_id, slice_start_utc, slice_end_utc) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS repair_batches (
                repair_batch_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NULL,
                requested_by TEXT NULL,
                reason TEXT NOT NULL,
                status TEXT NOT NULL,
                criteria_json TEXT NOT NULL DEFAULT '{}',
                requested_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                completed_at_utc TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS repair_slices (
                repair_slice_id TEXT NOT NULL PRIMARY KEY,
                repair_batch_id TEXT NOT NULL,
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                status TEXT NOT NULL,
                enqueued_queue_item_id TEXT NULL,
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (repair_batch_id) REFERENCES repair_batches(repair_batch_id) ON DELETE CASCADE,
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE,
                FOREIGN KEY (enqueued_queue_item_id) REFERENCES work_queue(queue_item_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS retention_runs (
                retention_run_id TEXT NOT NULL PRIMARY KEY,
                policy_name TEXT NOT NULL,
                status TEXT NOT NULL,
                cutoff_utc TEXT NOT NULL,
                rows_scanned INTEGER NOT NULL DEFAULT 0,
                rows_deleted INTEGER NOT NULL DEFAULT 0,
                started_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                completed_at_utc TEXT NULL,
                details_json TEXT NOT NULL DEFAULT '{}'
            );

            CREATE TABLE IF NOT EXISTS purge_runs (
                purge_run_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NULL,
                reason TEXT NOT NULL,
                status TEXT NOT NULL,
                requested_by TEXT NULL,
                requested_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                completed_at_utc TEXT NULL,
                details_json TEXT NOT NULL DEFAULT '{}',
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS job_lifecycle_events (
                lifecycle_event_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                event_type TEXT NOT NULL,
                reason TEXT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                recorded_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS system_audit (
                audit_id TEXT NOT NULL PRIMARY KEY,
                actor TEXT NULL,
                action TEXT NOT NULL,
                subject_type TEXT NOT NULL,
                subject_id TEXT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                recorded_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE TABLE IF NOT EXISTS rerun_batches (
                rerun_batch_id TEXT NOT NULL PRIMARY KEY,
                root_job_id TEXT NOT NULL,
                root_start_utc TEXT NOT NULL,
                root_end_utc TEXT NOT NULL,
                requested_by TEXT NULL,
                reason TEXT NOT NULL,
                status TEXT NOT NULL,
                kusto_cleanup_acknowledged INTEGER NOT NULL DEFAULT 0 CHECK (kusto_cleanup_acknowledged IN (0, 1)),
                kusto_cleanup_commands TEXT NOT NULL DEFAULT '',
                summary_json TEXT NOT NULL DEFAULT '{}',
                requested_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                completed_at_utc TEXT NULL,
                FOREIGN KEY (root_job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS rerun_slices (
                rerun_slice_id TEXT NOT NULL PRIMARY KEY,
                rerun_batch_id TEXT NOT NULL,
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                role TEXT NOT NULL,
                previous_state TEXT NULL,
                previous_attempt INTEGER NULL,
                status TEXT NOT NULL,
                blocker_reason TEXT NULL,
                snapshot_json TEXT NOT NULL DEFAULT '{}',
                reset_at_utc TEXT NULL,
                created_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (rerun_batch_id) REFERENCES rerun_batches(rerun_batch_id) ON DELETE CASCADE,
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ux_job_definitions_activity_id ON job_definitions(activity_id);
            CREATE INDEX IF NOT EXISTS ix_job_definition_events_job_recorded ON job_definition_events(job_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_slice_state_events_slice_recorded ON slice_state_events(job_id, slice_start_utc, slice_end_utc, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_current_slice_state_state_due ON current_slice_state(state, updated_at_utc);
            CREATE INDEX IF NOT EXISTS ix_work_queue_ready ON work_queue(queue_name, state, available_at_utc, priority DESC);
            CREATE INDEX IF NOT EXISTS ix_work_queue_slice ON work_queue(job_id, slice_start_utc, slice_end_utc);
            CREATE INDEX IF NOT EXISTS ix_failure_summary_runs_job_updated ON failure_summary_runs(job_id, updated_at_utc);
            CREATE INDEX IF NOT EXISTS ix_operational_logs_job_recorded ON operational_logs(job_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_scheduled_slices_due ON scheduled_slices(status, due_at_utc);
            CREATE INDEX IF NOT EXISTS ix_slice_attempts_slice_attempt ON slice_attempts(job_id, slice_start_utc, slice_end_utc, attempt);
            CREATE INDEX IF NOT EXISTS ix_slice_attempts_job_completed ON slice_attempts(job_id, completed_at_utc);
            CREATE INDEX IF NOT EXISTS ix_repair_batches_job ON repair_batches(job_id);
            CREATE INDEX IF NOT EXISTS ix_repair_slices_batch_status ON repair_slices(repair_batch_id, status);
            CREATE INDEX IF NOT EXISTS ix_repair_slices_job_slice ON repair_slices(job_id, slice_start_utc, slice_end_utc);
            CREATE INDEX IF NOT EXISTS ix_retention_runs_policy_started ON retention_runs(policy_name, started_at_utc);
            CREATE INDEX IF NOT EXISTS ix_purge_runs_job_requested ON purge_runs(job_id, requested_at_utc);
            CREATE INDEX IF NOT EXISTS ix_job_lifecycle_events_job_recorded ON job_lifecycle_events(job_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_system_audit_subject_recorded ON system_audit(subject_type, subject_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_rerun_batches_root_requested ON rerun_batches(root_job_id, requested_at_utc);
            CREATE INDEX IF NOT EXISTS ix_rerun_slices_batch_status ON rerun_slices(rerun_batch_id, status);
            CREATE INDEX IF NOT EXISTS ix_rerun_slices_job_slice ON rerun_slices(job_id, slice_start_utc, slice_end_utc);
            """;

        // Version 2 (additive): records every Kusto ingestion-capacity throttle (429,
        // CapacityPolicy/Ingestion) a slice attempt hit. Drives the sustained-throttle detector and
        // the advisory maxParallelism recommendations. Append-only, time-pruned, and purged with the
        // owning job; no foreign key (matches the slice_attempts / current_slice_state pattern, with
        // explicit cleanup on hard-delete).
        private const string IngestionThrottleObservationsSql = """
            CREATE TABLE IF NOT EXISTS ingestion_throttle_observations (
                observation_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                cluster_uri TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,
                reported_capacity INTEGER NULL,
                observed_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_throttle_cluster_observed ON ingestion_throttle_observations(cluster_uri, observed_at_utc);
            CREATE INDEX IF NOT EXISTS ix_ingestion_throttle_job ON ingestion_throttle_observations(job_id);
            """;

        // Version 3 (additive): marks the throttle observation that corresponds to a slice's terminal
        // (dead-letter) attempt, i.e. the slice gave up after consecutive throttled attempts. This is
        // the worst throttling outcome (a data gap needing a rerun); the column lets the advisor
        // surface such slices and force the page/banner to show regardless of the rate gate.
        private const string IngestionThrottleTerminalSql = """
            ALTER TABLE ingestion_throttle_observations ADD COLUMN terminal INTEGER NOT NULL DEFAULT 0;

            CREATE INDEX IF NOT EXISTS ix_ingestion_throttle_terminal ON ingestion_throttle_observations(terminal, observed_at_utc);
            """;

        // Version 4 (additive): index the two foreign-key columns whose ON DELETE action is otherwise
        // unindexed, so a hard delete does not degrade to O(rows^2). The FK
        // current_slice_state.last_event_id -> slice_state_events(event_id) ON DELETE SET NULL forced a
        // full scan of current_slice_state for every slice_state_events row deleted during a purge
        // (e.g. 12k events x 99k states ~= 1.2 billion row scans), which hung the hard-delete request,
        // held the single WAL writer, and starved the worker (SQLITE_BUSY "database is locked"). The
        // repair_slices.enqueued_queue_item_id -> work_queue(queue_item_id) ON DELETE SET NULL FK has
        // the same latent problem on the work_queue delete. These indexes turn each FK enforcement
        // lookup into an index seek. Pure indexes (no data change); replays idempotently.
        private const string ForeignKeyDeleteIndexesSql = """
            CREATE INDEX IF NOT EXISTS ix_current_slice_state_last_event ON current_slice_state(last_event_id);
            CREATE INDEX IF NOT EXISTS ix_repair_slices_enqueued_queue_item ON repair_slices(enqueued_queue_item_id);
            """;
    }
}
