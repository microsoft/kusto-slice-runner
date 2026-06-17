using System.Security.Cryptography;
using System.Text;
using KoLite.Local.Sqlite.Connections;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Migrations
{
    public sealed class KoLiteSqliteMigrator
    {
        private const int InitialSchemaVersion = 1;

        private static readonly SqliteMigration[] Migrations =
        [
            new(InitialSchemaVersion, "initial-local-first-schema", InitialSchemaSql),
            new(2, "repair-batches-job-association", """
                ALTER TABLE repair_batches ADD COLUMN job_id TEXT NULL;

                UPDATE repair_batches
                SET job_id = (
                    SELECT repair_slices.job_id
                    FROM repair_slices
                    WHERE repair_slices.repair_batch_id = repair_batches.repair_batch_id
                    GROUP BY repair_slices.job_id
                    ORDER BY COUNT(*) DESC, repair_slices.job_id
                    LIMIT 1
                )
                WHERE job_id IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM repair_slices
                      WHERE repair_slices.repair_batch_id = repair_batches.repair_batch_id
                  );

                CREATE INDEX IF NOT EXISTS ix_repair_batches_job ON repair_batches(job_id);
                """),
            new(3, "rerun-batches", """
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

                CREATE INDEX IF NOT EXISTS ix_rerun_batches_root_requested ON rerun_batches(root_job_id, requested_at_utc);
                CREATE INDEX IF NOT EXISTS ix_rerun_slices_batch_status ON rerun_slices(rerun_batch_id, status);
                CREATE INDEX IF NOT EXISTS ix_rerun_slices_job_slice ON rerun_slices(job_id, slice_start_utc, slice_end_utc);
                """),
            new(4, "drop-activity-cursors", """
                DROP TABLE IF EXISTS activity_cursors;
                """),
            new(5, "slice-attempts-job-completed-index", """
                CREATE INDEX IF NOT EXISTS ix_slice_attempts_job_completed ON slice_attempts(job_id, completed_at_utc);
                """),
            new(6, "rekey-activity-id-to-guid", null, RekeyActivityIdToGuidMigration.Apply, "v6:rekey-activity-id-to-guid:1"),
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

        public void Migrate(SqliteConnection connection) => Migrate(connection, LatestVersion);

        // Applies migrations only up to and including throughVersion. Primarily a test/ops hook
        // for reconstructing an older schema state; production startup calls Migrate(connection).
        public void Migrate(SqliteConnection connection, int throughVersion)
        {
            ArgumentNullException.ThrowIfNull(connection);
            EnsureLedger(connection);

            foreach (var migration in Migrations)
            {
                if (migration.Version > throughVersion)
                {
                    break;
                }

                if (HasApplied(connection, migration))
                {
                    continue;
                }

                using var transaction = connection.BeginTransaction();
                if (!string.IsNullOrEmpty(migration.Sql))
                {
                    ExecuteNonQuery(connection, transaction, migration.Sql);
                }

                migration.Code?.Invoke(connection, transaction);
                InsertLedger(connection, transaction, migration);
                transaction.Commit();
            }

            var target = Math.Min(throughVersion, LatestVersion);
            SetMetadata(connection, "schema_version", target.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

            var expectedChecksum = Sha256(migration.ChecksumMaterial);
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
            command.Parameters.AddWithValue("$checksum", Sha256(migration.ChecksumMaterial));
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

        private sealed record SqliteMigration(int Version, string Name, string? Sql, Action<SqliteConnection, SqliteTransaction>? Code = null, string? CodeChecksum = null)
        {
            // Material the ledger checksum is computed from. SQL migrations hash their SQL text;
            // code migrations hash an explicit, reviewer-bumped checksum string so that changing
            // the delegate body without bumping the string is detected as drift.
            public string ChecksumMaterial => Sql ?? CodeChecksum
                ?? throw new InvalidOperationException($"Migration {Version} ({Name}) has neither SQL nor a code checksum.");
        }

        private const string InitialSchemaSql = """
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

            CREATE TABLE IF NOT EXISTS activity_cursors (
                cursor_name TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NULL,
                cursor_value TEXT NOT NULL,
                cursor_kind TEXT NOT NULL,
                payload_json TEXT NOT NULL DEFAULT '{}',
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id) REFERENCES job_definitions(job_id) ON DELETE CASCADE
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

            CREATE INDEX IF NOT EXISTS ix_job_definition_events_job_recorded ON job_definition_events(job_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_slice_state_events_slice_recorded ON slice_state_events(job_id, slice_start_utc, slice_end_utc, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_current_slice_state_state_due ON current_slice_state(state, updated_at_utc);
            CREATE INDEX IF NOT EXISTS ix_work_queue_ready ON work_queue(queue_name, state, available_at_utc, priority DESC);
            CREATE INDEX IF NOT EXISTS ix_work_queue_slice ON work_queue(job_id, slice_start_utc, slice_end_utc);
            CREATE INDEX IF NOT EXISTS ix_activity_cursors_job ON activity_cursors(job_id);
            CREATE INDEX IF NOT EXISTS ix_failure_summary_runs_job_updated ON failure_summary_runs(job_id, updated_at_utc);
            CREATE INDEX IF NOT EXISTS ix_operational_logs_job_recorded ON operational_logs(job_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_scheduled_slices_due ON scheduled_slices(status, due_at_utc);
            CREATE INDEX IF NOT EXISTS ix_slice_attempts_slice_attempt ON slice_attempts(job_id, slice_start_utc, slice_end_utc, attempt);
            CREATE INDEX IF NOT EXISTS ix_repair_slices_batch_status ON repair_slices(repair_batch_id, status);
            CREATE INDEX IF NOT EXISTS ix_repair_slices_job_slice ON repair_slices(job_id, slice_start_utc, slice_end_utc);
            CREATE INDEX IF NOT EXISTS ix_retention_runs_policy_started ON retention_runs(policy_name, started_at_utc);
            CREATE INDEX IF NOT EXISTS ix_purge_runs_job_requested ON purge_runs(job_id, requested_at_utc);
            CREATE INDEX IF NOT EXISTS ix_job_lifecycle_events_job_recorded ON job_lifecycle_events(job_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_system_audit_subject_recorded ON system_audit(subject_type, subject_id, recorded_at_utc);
            """;
    }
}
