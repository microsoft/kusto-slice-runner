using KoLite.Local.Sqlite.Connections;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Schema
{
    // Provisions the KO Lite SQLite database to the current schema. The schema is a single,
    // idempotent definition (every statement is CREATE ... IF NOT EXISTS), so EnsureSchema runs on
    // every startup and is a no-op once the database is already provisioned.
    public sealed class KoLiteSqliteSchema
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public KoLiteSqliteSchema(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        }

        public void EnsureSchema()
        {
            using var connection = connectionFactory.OpenConnection();
            EnsureSchema(connection);
        }

        public void EnsureSchema(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            // Establish WAL once, at startup, before any hosted service opens a pooled connection. WAL
            // is a persistent database-header property; setting it per connection would take a write
            // lock on every open (including read-only paths), so connection setup leaves it alone. WAL
            // must run outside a transaction, so it is set before the schema statements run.
            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
            ExecuteNonQuery(connection, SchemaSql);
            EnsureColumn(connection, "work_queue", "chunk_id", "INTEGER NULL");
            EnsureColumn(connection, "work_queue", "total_chunks", "INTEGER NULL");
            EnsureColumn(connection, "slice_attempts", "chunk_id", "INTEGER NULL");
            EnsureColumn(connection, "slice_attempts", "total_chunks", "INTEGER NULL");
            EnsureColumn(connection, "operational_logs", "chunk_id", "INTEGER NULL");
            EnsureColumn(connection, "operational_logs", "total_chunks", "INTEGER NULL");
            EnsureColumn(connection, "ingestion_throttle_observations", "chunk_id", "INTEGER NULL");
            EnsureColumn(connection, "ingestion_throttle_observations", "total_chunks", "INTEGER NULL");
            ExecuteNonQuery(connection, AdditiveIndexSql);
        }

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
        {
            using var check = connection.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
            check.Parameters.AddWithValue("$column", column);
            if (Convert.ToInt32(check.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                return;
            }

            ExecuteNonQuery(connection, $"ALTER TABLE {table} ADD COLUMN {column} {definition};");
        }

        private const string SchemaSql = """
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

            CREATE TABLE IF NOT EXISTS slice_chunk_state_events (
                event_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                chunk_id INTEGER NOT NULL,
                total_chunks INTEGER NOT NULL,
                state TEXT NOT NULL,
                reason TEXT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,
                payload_json TEXT NOT NULL DEFAULT '{}',
                actor TEXT NULL,
                recorded_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                FOREIGN KEY (job_id, slice_start_utc, slice_end_utc)
                    REFERENCES current_slice_state(job_id, slice_start_utc, slice_end_utc) ON DELETE CASCADE,
                CHECK (total_chunks >= 1 AND total_chunks <= 32),
                CHECK (chunk_id >= 0 AND chunk_id < total_chunks)
            );

            CREATE TABLE IF NOT EXISTS current_slice_chunk_state (
                job_id TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                chunk_id INTEGER NOT NULL,
                total_chunks INTEGER NOT NULL,
                state TEXT NOT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,
                lease_owner TEXT NULL,
                lease_expires_at_utc TEXT NULL,
                last_event_id TEXT NULL,
                last_error_code TEXT NULL,
                last_error_message TEXT NULL,
                updated_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                PRIMARY KEY (job_id, slice_start_utc, slice_end_utc, chunk_id),
                FOREIGN KEY (job_id, slice_start_utc, slice_end_utc)
                    REFERENCES current_slice_state(job_id, slice_start_utc, slice_end_utc) ON DELETE CASCADE,
                FOREIGN KEY (last_event_id) REFERENCES slice_chunk_state_events(event_id) ON DELETE SET NULL,
                CHECK (total_chunks >= 1 AND total_chunks <= 32),
                CHECK (chunk_id >= 0 AND chunk_id < total_chunks)
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
                chunk_id INTEGER NULL,
                total_chunks INTEGER NULL,
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
                chunk_id INTEGER NULL,
                total_chunks INTEGER NULL,
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
                chunk_id INTEGER NULL,
                total_chunks INTEGER NULL,
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
            CREATE INDEX IF NOT EXISTS ix_slice_chunk_state_events_slice_recorded ON slice_chunk_state_events(job_id, slice_start_utc, slice_end_utc, chunk_id, recorded_at_utc);
            CREATE INDEX IF NOT EXISTS ix_current_slice_chunk_state_state ON current_slice_chunk_state(job_id, state, updated_at_utc);
            CREATE INDEX IF NOT EXISTS ix_current_slice_chunk_state_last_event ON current_slice_chunk_state(last_event_id);
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

            CREATE TABLE IF NOT EXISTS ingestion_throttle_observations (
                observation_id TEXT NOT NULL PRIMARY KEY,
                job_id TEXT NOT NULL,
                cluster_uri TEXT NOT NULL,
                slice_start_utc TEXT NOT NULL,
                slice_end_utc TEXT NOT NULL,
                attempt INTEGER NOT NULL DEFAULT 0,
                reported_capacity INTEGER NULL,
                chunk_id INTEGER NULL,
                total_chunks INTEGER NULL,
                observed_at_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
                terminal INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_throttle_cluster_observed ON ingestion_throttle_observations(cluster_uri, observed_at_utc);
            CREATE INDEX IF NOT EXISTS ix_ingestion_throttle_job ON ingestion_throttle_observations(job_id);
            CREATE INDEX IF NOT EXISTS ix_ingestion_throttle_terminal ON ingestion_throttle_observations(terminal, observed_at_utc);

            CREATE INDEX IF NOT EXISTS ix_current_slice_state_last_event ON current_slice_state(last_event_id);
            CREATE INDEX IF NOT EXISTS ix_repair_slices_enqueued_queue_item ON repair_slices(enqueued_queue_item_id);
            """;

        private const string AdditiveIndexSql = """
            CREATE INDEX IF NOT EXISTS ix_work_queue_slice_chunk
                ON work_queue(job_id, slice_start_utc, slice_end_utc, chunk_id);
            CREATE INDEX IF NOT EXISTS ix_slice_attempts_slice_chunk
                ON slice_attempts(job_id, slice_start_utc, slice_end_utc, chunk_id, attempt);
            """;
    }
}
