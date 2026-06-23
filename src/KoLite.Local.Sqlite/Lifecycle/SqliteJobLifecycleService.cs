using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;

namespace KoLite.Local.Sqlite.Lifecycle
{
    public sealed record HardDeleteResult(string PurgeRunId, int DeletedJobs, int DeletedQueueRows, int DeletedStateRows, int DeletedRepairBatches);

    public sealed class SqliteJobLifecycleService
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly SqliteJobCatalogRepository catalog;

        public SqliteJobLifecycleService(IKoLiteSqliteConnectionFactory connectionFactory, SqliteJobCatalogRepository catalog)
        {
            this.connectionFactory = connectionFactory;
            this.catalog = catalog;
        }

        public JobCatalogRecord SoftDelete(string jobId, long expectedVersion, string actor, string reason)
        {
            var updated = catalog.SetEnabled(jobId, enabled: false, expectedVersion, actor, eventId: Guid.NewGuid().ToString("N"));
            RecordLifecycle(jobId, "SoftDeleted", actor, reason, new { expectedVersion });
            return updated;
        }

        public JobCatalogRecord Restore(string jobId, long expectedVersion, string actor, string reason)
        {
            var updated = catalog.SetEnabled(jobId, enabled: true, expectedVersion, actor, eventId: Guid.NewGuid().ToString("N"));
            RecordLifecycle(jobId, "Restored", actor, reason, new { expectedVersion });
            return updated;
        }

        public HardDeleteResult HardDelete(string jobId, string confirmation, string actor, string reason)
        {
            var expected = $"DELETE {jobId}";
            if (!StringComparer.Ordinal.Equals(confirmation, expected))
            {
                throw new InvalidOperationException($"Hard-delete confirmation must exactly match '{expected}'.");
            }

            var purgeRunId = Guid.NewGuid().ToString("N");
            using var c = connectionFactory.OpenConnection();
            using var tx = c.BeginTransaction();
            EnsureHardDeletePreconditions(c, tx, jobId);
            InsertPurgeRun(c, tx, purgeRunId, jobId, actor, reason, "Running");
            var repairBatchIds = ReadRepairBatchIds(c, tx, jobId);
            var queueRows = Delete(c, tx, "work_queue", "job_id=$job", jobId);
            var attemptRows = Delete(c, tx, "slice_attempts", "job_id=$job", jobId);
            var scheduledRows = Delete(c, tx, "scheduled_slices", "job_id=$job", jobId);
            var logRows = Delete(c, tx, "operational_logs", "job_id=$job", jobId);
            var stateEventRows = Delete(c, tx, "slice_state_events", "job_id=$job", jobId);
            var stateRows = Delete(c, tx, "current_slice_state", "job_id=$job", jobId);
            var throttleObservationRows = Delete(c, tx, "ingestion_throttle_observations", "job_id=$job", jobId);
            var repairSliceRows = Delete(c, tx, "repair_slices", "job_id=$job", jobId);
            var repairBatchRows = DeleteRepairBatches(c, tx, repairBatchIds);
            var summaryRows = Delete(c, tx, "failure_summary_runs", "job_id=$job", jobId);
            var lifecycleRows = Delete(c, tx, "job_lifecycle_events", "job_id=$job", jobId);
            var eventRows = Delete(c, tx, "job_definition_events", "job_id=$job", jobId);
            var jobRows = Delete(c, tx, "job_definitions", "job_id=$job", jobId);

            InsertAudit(c, tx, actor, "HardDeleted", "Job", jobId, new
            {
                purgeRunId,
                reason,
                queueRows,
                attemptRows,
                scheduledRows,
                logRows,
                stateRows,
                stateEventRows,
                throttleObservationRows,
                repairSliceRows,
                repairBatchRows,
                summaryRows,
                lifecycleRows,
                eventRows,
                jobRows
            });
            CompletePurgeRun(c, tx, purgeRunId, new { jobId, jobRows, queueRows, stateRows, repairBatchRows });
            tx.Commit();

            using var checkpoint = SqliteStorage.Command(c, null, "PRAGMA wal_checkpoint(PASSIVE);");
            checkpoint.ExecuteNonQuery();
            return new HardDeleteResult(purgeRunId, jobRows, queueRows, stateRows, repairBatchRows);
        }

        private static void EnsureHardDeletePreconditions(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string jobId)
        {
            using (var job = SqliteStorage.Command(c, tx, "SELECT is_enabled FROM job_definitions WHERE job_id=$job;"))
            {
                job.Add("$job", jobId);
                var enabled = job.ExecuteScalar();
                if (enabled is null)
                {
                    throw new InvalidOperationException($"Job '{jobId}' does not exist.");
                }

                if (Convert.ToInt32(enabled, System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException($"Hard-delete requires job '{jobId}' to be disabled first.");
                }
            }

            var activeWork = Count(c, tx, "work_queue", "job_id=$job AND state IN ('Queued','Leased')", jobId);
            if (activeWork > 0)
            {
                throw new InvalidOperationException($"Hard-delete cannot purge job '{jobId}' while {activeWork} active work item(s) are queued or leased.");
            }

            var runningSlices = Count(c, tx, "current_slice_state", "job_id=$job AND state='Running'", jobId);
            if (runningSlices > 0)
            {
                throw new InvalidOperationException($"Hard-delete cannot purge job '{jobId}' while {runningSlices} slice(s) are running.");
            }
        }

        private void RecordLifecycle(string jobId, string eventType, string actor, string reason, object payload)
        {
            using var c = connectionFactory.OpenConnection();
            using var tx = c.BeginTransaction();
            using (var cmd = SqliteStorage.Command(c, tx, """
                INSERT INTO job_lifecycle_events (lifecycle_event_id,job_id,event_type,reason,payload_json)
                VALUES ($id,$job,$type,$reason,$payload);
                """))
            {
                cmd.Add("$id", Guid.NewGuid().ToString("N"));
                cmd.Add("$job", jobId);
                cmd.Add("$type", eventType);
                cmd.Add("$reason", reason);
                cmd.Add("$payload", JsonSerializer.Serialize(payload));
                cmd.ExecuteNonQuery();
            }

            InsertAudit(c, tx, actor, eventType, "Job", jobId, new { reason });
            tx.Commit();
        }

        private static IReadOnlyList<string> ReadRepairBatchIds(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string jobId)
        {
            using var cmd = SqliteStorage.Command(c, tx, """
                SELECT repair_batch_id FROM repair_batches WHERE job_id=$job
                UNION
                SELECT DISTINCT repair_batch_id FROM repair_slices WHERE job_id=$job;
                """);
            cmd.Add("$job", jobId);
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }

        private static int DeleteRepairBatches(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, IReadOnlyList<string> ids)
        {
            var deleted = 0;
            foreach (var id in ids)
            {
                using var cmd = SqliteStorage.Command(c, tx, "DELETE FROM repair_batches WHERE repair_batch_id=$id;");
                cmd.Add("$id", id);
                deleted += cmd.ExecuteNonQuery();
            }
            return deleted;
        }

        private static int Delete(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string table, string predicate, string jobId)
        {
            using var cmd = SqliteStorage.Command(c, tx, $"DELETE FROM {table} WHERE {predicate};");
            cmd.Add("$job", jobId);
            return cmd.ExecuteNonQuery();
        }

        private static int Count(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string table, string predicate, string jobId)
        {
            using var cmd = SqliteStorage.Command(c, tx, $"SELECT COUNT(*) FROM {table} WHERE {predicate};");
            cmd.Add("$job", jobId);
            return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void InsertPurgeRun(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string id, string jobId, string actor, string reason, string status)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO purge_runs (purge_run_id,job_id,reason,status,requested_by,details_json) VALUES ($id,$job,$reason,$status,$by,'{}');");
            cmd.Add("$id", id); cmd.Add("$job", jobId); cmd.Add("$reason", reason); cmd.Add("$status", status); cmd.Add("$by", actor); cmd.ExecuteNonQuery();
        }

        private static void CompletePurgeRun(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string id, object details)
        {
            using var cmd = SqliteStorage.Command(c, tx, "UPDATE purge_runs SET status='Completed', completed_at_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'), details_json=$details WHERE purge_run_id=$id;");
            cmd.Add("$id", id); cmd.Add("$details", JsonSerializer.Serialize(details)); cmd.ExecuteNonQuery();
        }

        private static void InsertAudit(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string actor, string action, string subjectType, string subjectId, object payload)
        {
            using var cmd = SqliteStorage.Command(c, tx, "INSERT INTO system_audit (audit_id,actor,action,subject_type,subject_id,payload_json) VALUES ($id,$actor,$action,$type,$subject,$payload);");
            cmd.Add("$id", Guid.NewGuid().ToString("N")); cmd.Add("$actor", actor); cmd.Add("$action", action); cmd.Add("$type", subjectType); cmd.Add("$subject", subjectId); cmd.Add("$payload", JsonSerializer.Serialize(payload)); cmd.ExecuteNonQuery();
        }
    }
}
