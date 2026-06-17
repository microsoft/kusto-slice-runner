using System.Globalization;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Migrations
{
    // One-time, in-place re-key of the job natural key (activityId) to an opaque GUID identity.
    // Runs inside the migrator's per-migration transaction. Existing rows are migrated in place
    // (no delete/recreate). FK integrity is deferred to commit so parent/child re-keys are
    // consistent only at the end of the transaction.
    internal static class RekeyActivityIdToGuidMigration
    {
        // Every table that carries a job_id foreign key to job_definitions.
        private static readonly string[] FkJobTables =
        [
            "job_definition_events", "slice_state_events", "current_slice_state", "work_queue",
            "failure_summary_runs", "operational_logs", "scheduled_slices", "slice_attempts",
            "repair_batches", "repair_slices", "purge_runs", "job_lifecycle_events", "rerun_slices"
        ];

        public static void Apply(SqliteConnection connection, SqliteTransaction transaction)
        {
            // UNIQUE is always immediate, but FK checks can be deferred to commit. This lets us
            // rewrite parents and children in any order within the transaction.
            Exec(connection, transaction, "PRAGMA defer_foreign_keys = ON;");

            // 1. Add the mutable label column (nullable: SQLite cannot add NOT NULL without a
            //    default), backfill it from the current natural key, and enforce uniqueness.
            Exec(connection, transaction, "ALTER TABLE job_definitions ADD COLUMN activity_id TEXT;");
            Exec(connection, transaction, "UPDATE job_definitions SET activity_id = job_id WHERE activity_id IS NULL;");
            Exec(connection, transaction, "CREATE UNIQUE INDEX IF NOT EXISTS ux_job_definitions_activity_id ON job_definitions(activity_id);");

            // 2. Snapshot jobs and assign a new GUID to each.
            var jobs = new List<(string OldId, string ScheduleJson)>();
            using (var read = SqliteStorage.Command(connection, transaction, "SELECT job_id, schedule_json FROM job_definitions;"))
            using (var reader = read.ExecuteReader())
            {
                while (reader.Read())
                {
                    jobs.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (oldId, _) in jobs)
            {
                map[oldId] = Guid.NewGuid().ToString("N");
            }

            // 3. Per-job rewrite. Embedded functional strings are rewritten while the row's job_id
            //    still equals the old id (so we can target rows), then the job_id columns are moved.
            foreach (var (oldId, scheduleJson) in jobs)
            {
                var newId = map[oldId];
                var storageJson = BuildMigratedScheduleJson(scheduleJson, newId, map);

                ReplaceWhereJob(connection, transaction, "work_queue", "idempotency_key", oldId,
                    ("|" + oldId + "|", "|" + newId + "|"));
                ReplaceWhereJob(connection, transaction, "work_queue", "payload_json", oldId,
                    ("\"jobId\":\"" + oldId + "\"", "\"jobId\":\"" + newId + "\""),
                    ("\"sliceKey\":\"" + oldId + "|", "\"sliceKey\":\"" + newId + "|"));
                ReplaceWhereJob(connection, transaction, "repair_batches", "criteria_json", oldId,
                    ("\"JobId\":\"" + oldId + "\"", "\"JobId\":\"" + newId + "\""));

                using (var audit = SqliteStorage.Command(connection, transaction, "UPDATE system_audit SET subject_id=$new WHERE subject_id=$old AND subject_type='Job';"))
                {
                    audit.Add("$new", newId);
                    audit.Add("$old", oldId);
                    audit.ExecuteNonQuery();
                }

                using (var def = SqliteStorage.Command(connection, transaction, "UPDATE job_definitions SET job_id=$new, display_name=activity_id, schedule_json=$json WHERE job_id=$old;"))
                {
                    def.Add("$new", newId);
                    def.Add("$json", storageJson);
                    def.Add("$old", oldId);
                    def.ExecuteNonQuery();
                }

                foreach (var table in FkJobTables)
                {
                    using var fk = SqliteStorage.Command(connection, transaction, $"UPDATE {table} SET job_id=$new WHERE job_id=$old;");
                    fk.Add("$new", newId);
                    fk.Add("$old", oldId);
                    fk.ExecuteNonQuery();
                }

                using (var rerun = SqliteStorage.Command(connection, transaction, "UPDATE rerun_batches SET root_job_id=$new WHERE root_job_id=$old;"))
                {
                    rerun.Add("$new", newId);
                    rerun.Add("$old", oldId);
                    rerun.ExecuteNonQuery();
                }
            }

            // 4. Best-effort: rewrite slice-key-bearing display strings that reference upstream ids
            //    (these are non-functional logs that also refresh on the next run).
            foreach (var entry in map)
            {
                BestEffortReplace(connection, transaction, "slice_state_events", "reason", entry.Key + "|", entry.Value + "|");
                BestEffortReplace(connection, transaction, "operational_logs", "properties_json", "\"" + entry.Key + "|", "\"" + entry.Value + "|");
            }

            // 5. Integrity assert before the ledger row is written by the migrator.
            using (var assert = SqliteStorage.Command(connection, transaction, "SELECT COUNT(*) FROM job_definitions WHERE activity_id IS NULL OR activity_id = '';"))
            {
                if (Convert.ToInt64(assert.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException("Migration v6 left NULL/empty activity_id values.");
                }
            }
        }

        private static string BuildMigratedScheduleJson(string oldScheduleJson, string newId, IReadOnlyDictionary<string, string> map)
        {
            var parsed = ScheduleParser.Parse(oldScheduleJson);
            var definition = parsed.Definition
                ?? throw new InvalidOperationException("Stored schedule JSON failed to parse during migration: " + string.Join("; ", parsed.Errors.Select(e => e.Field + ": " + e.Message)));

            var deps = new List<StoredDependency>();
            foreach (var dependency in definition.DependsOn)
            {
                // Historical stored edges reference the upstream by activityId (== its old job_id).
                var upstreamId = dependency.Id
                    ?? (dependency.ActivityId is { } activityId && map.TryGetValue(activityId, out var resolved) ? resolved : null);
                deps.Add(upstreamId is not null
                    ? new StoredDependency(upstreamId, null)
                    : new StoredDependency(null, dependency.ActivityId));
            }

            var canonical = SqliteStorage.CanonicalJson(oldScheduleJson);
            return CatalogScheduleJson.WriteStorageJson(canonical, newId, deps);
        }

        private static void ReplaceWhereJob(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string jobId, params (string Old, string New)[] replacements)
        {
            var expression = column;
            for (var i = 0; i < replacements.Length; i++)
            {
                expression = $"REPLACE({expression}, $o{i}, $n{i})";
            }

            using var command = SqliteStorage.Command(connection, transaction, $"UPDATE {table} SET {column}={expression} WHERE job_id=$job;");
            for (var i = 0; i < replacements.Length; i++)
            {
                command.Add($"$o{i}", replacements[i].Old);
                command.Add($"$n{i}", replacements[i].New);
            }

            command.Add("$job", jobId);
            command.ExecuteNonQuery();
        }

        private static void BestEffortReplace(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string oldFragment, string newFragment)
        {
            using var command = SqliteStorage.Command(connection, transaction, $"UPDATE {table} SET {column}=REPLACE({column}, $old, $new) WHERE {column} LIKE '%' || $like || '%';");
            command.Add("$old", oldFragment);
            command.Add("$new", newFragment);
            command.Add("$like", oldFragment);
            command.ExecuteNonQuery();
        }

        private static void Exec(SqliteConnection connection, SqliteTransaction transaction, string sql)
        {
            using var command = SqliteStorage.Command(connection, transaction, sql);
            command.ExecuteNonQuery();
        }
    }
}
