using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Observability
{
    public sealed record JobStatusSummary(string JobId, bool IsEnabled, int MissingCount, int QueuedCount, int RunningCount, int CompletedCount, int FailedCount, int DeadLetteredCount, int DependencyBlockedCount, DateTimeOffset? LastUpdatedAtUtc);
    public sealed record RecentFailure(string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string Status, int Attempt, string? Reason, DateTimeOffset UpdatedAtUtc);
    public sealed record RecentSliceEvent(string EventId, string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string EventType, int? Attempt, string? Reason, string? Actor, DateTimeOffset RecordedAtUtc);
    public sealed record QueueStatusSummary(string QueueName, int QueuedCount, int LeasedCount, int CompletedCount, int DeadLetteredCount, int ExpiredLeaseCount);
    public sealed record SliceStatusReadout(string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string Status, int Attempt, int? SuccessfulAttempt, string? LatestAttemptStatus, DateTimeOffset? LastAttemptUpdatedAtUtc, DateTimeOffset UpdatedAtUtc);
    public sealed record RetentionCleanupResult(string RetentionRunId, int LogsDeleted, int AttemptsDeleted, int ScheduledSlicesDeleted);

    public sealed class SqliteOperationalReadModelRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqliteOperationalReadModelRepository(IKoLiteSqliteConnectionFactory connectionFactory) => this.connectionFactory = connectionFactory;

        public void RecordLog(string level, string message, string? category = null, string? jobId = null, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, string propertiesJson = "{}", string? exception = null)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "INSERT INTO operational_logs (log_id,job_id,slice_start_utc,slice_end_utc,level,message,category,exception,properties_json,recorded_at_utc) VALUES ($id,$j,$s,$e,$l,$m,$cat,$ex,$p,$n);");
            cmd.Add("$id", Guid.NewGuid().ToString("N")); cmd.Add("$j", jobId); cmd.Add("$s", sliceStartUtc is null ? null : SqliteStorage.Utc(sliceStartUtc.Value)); cmd.Add("$e", sliceEndUtc is null ? null : SqliteStorage.Utc(sliceEndUtc.Value)); cmd.Add("$l", level); cmd.Add("$m", message); cmd.Add("$cat", category); cmd.Add("$ex", exception); cmd.Add("$p", propertiesJson); cmd.Add("$n", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.ExecuteNonQuery();
        }

        public void RecordScheduledSlice(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string status, DateTimeOffset scheduledAtUtc, DateTimeOffset dueAtUtc, string? generationId = null)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "INSERT INTO scheduled_slices (job_id,slice_start_utc,slice_end_utc,generation_id,status,scheduled_at_utc,due_at_utc) VALUES ($j,$s,$e,$g,$st,$sa,$d) ON CONFLICT(job_id,slice_start_utc,slice_end_utc) DO UPDATE SET generation_id=excluded.generation_id,status=excluded.status,scheduled_at_utc=excluded.scheduled_at_utc,due_at_utc=excluded.due_at_utc;");
            cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(sliceStartUtc)); cmd.Add("$e", SqliteStorage.Utc(sliceEndUtc)); cmd.Add("$g", generationId); cmd.Add("$st", status); cmd.Add("$sa", SqliteStorage.Utc(scheduledAtUtc)); cmd.Add("$d", SqliteStorage.Utc(dueAtUtc)); cmd.ExecuteNonQuery();
        }

        public void RecordAttempt(string attemptId, string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, int attempt, string status, string? workerId, DateTimeOffset? startedAtUtc, DateTimeOffset? completedAtUtc, string? errorCode = null, string? errorMessage = null, string metricsJson = "{}")
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "INSERT INTO slice_attempts (attempt_id,job_id,slice_start_utc,slice_end_utc,attempt,status,worker_id,started_at_utc,completed_at_utc,error_code,error_message,metrics_json) VALUES ($id,$j,$s,$e,$a,$st,$w,$start,$done,$ec,$em,$m) ON CONFLICT(attempt_id) DO UPDATE SET status=excluded.status,worker_id=excluded.worker_id,started_at_utc=COALESCE(excluded.started_at_utc, slice_attempts.started_at_utc),completed_at_utc=excluded.completed_at_utc,error_code=excluded.error_code,error_message=excluded.error_message,metrics_json=excluded.metrics_json;");
            cmd.Add("$id", attemptId); cmd.Add("$j", jobId); cmd.Add("$s", SqliteStorage.Utc(sliceStartUtc)); cmd.Add("$e", SqliteStorage.Utc(sliceEndUtc)); cmd.Add("$a", attempt); cmd.Add("$st", status); cmd.Add("$w", workerId); cmd.Add("$start", startedAtUtc is null ? null : SqliteStorage.Utc(startedAtUtc.Value)); cmd.Add("$done", completedAtUtc is null ? null : SqliteStorage.Utc(completedAtUtc.Value)); cmd.Add("$ec", errorCode); cmd.Add("$em", errorMessage); cmd.Add("$m", metricsJson); cmd.ExecuteNonQuery();
        }

        public IReadOnlyList<JobStatusSummary> GetJobStatusSummaries()
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT jd.job_id, jd.is_enabled,
                       SUM(CASE WHEN css.state IS NULL THEN 1 ELSE 0 END) missing_count,
                       SUM(CASE WHEN css.state='Queued' THEN 1 ELSE 0 END) queued_count,
                       SUM(CASE WHEN css.state='Running' THEN 1 ELSE 0 END) running_count,
                       SUM(CASE WHEN css.state='Completed' THEN 1 ELSE 0 END) completed_count,
                       SUM(CASE WHEN css.state='Failed' THEN 1 ELSE 0 END) failed_count,
                       SUM(CASE WHEN css.state='DeadLettered' THEN 1 ELSE 0 END) deadletter_count,
                       SUM(CASE WHEN css.state='DependencyBlocked' THEN 1 ELSE 0 END) blocked_count,
                       MAX(css.updated_at_utc) last_updated
                FROM job_definitions jd
                LEFT JOIN current_slice_state css ON css.job_id = jd.job_id
                GROUP BY jd.job_id, jd.is_enabled
                ORDER BY jd.job_id;
                """);
            using var r = cmd.ExecuteReader(); var results = new List<JobStatusSummary>();
            while (r.Read()) results.Add(new JobStatusSummary(r.GetString(0), r.GetInt32(1) == 1, r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.IsDBNull(9) ? null : DateTimeOffset.Parse(r.GetString(9), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal)));
            return results;
        }

        public IReadOnlyList<RecentFailure> GetRecentFailures(int take = 20)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT job_id,slice_start_utc,slice_end_utc,state,attempt,last_error_message,updated_at_utc FROM current_slice_state WHERE state IN ('Failed','DeadLettered') ORDER BY updated_at_utc DESC LIMIT $take;");
            cmd.Add("$take", take); using var r = cmd.ExecuteReader(); var results = new List<RecentFailure>();
            while (r.Read()) results.Add(new RecentFailure(r.GetString(0), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), SqliteStorage.ReadUtc(r, "updated_at_utc")));
            return results;
        }

        public IReadOnlyList<RecentSliceEvent> GetRecentSliceEvents(int take = 50)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT event_id,job_id,slice_start_utc,slice_end_utc,event_type,attempt,reason,actor,recorded_at_utc FROM slice_state_events ORDER BY recorded_at_utc DESC, event_id DESC LIMIT $take;");
            cmd.Add("$take", take); using var r = cmd.ExecuteReader(); var results = new List<RecentSliceEvent>();
            while (r.Read()) results.Add(new RecentSliceEvent(r.GetString(0), r.GetString(1), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            return results;
        }

        public QueueStatusSummary GetQueueStatus(string queueName, DateTimeOffset nowUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT
                  SUM(CASE WHEN state='Queued' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN state='Leased' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN state='Completed' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN state='DeadLettered' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN state='Leased' AND locked_until_utc <= $now THEN 1 ELSE 0 END)
                FROM work_queue WHERE queue_name=$q;
                """);
            cmd.Add("$q", queueName); cmd.Add("$now", SqliteStorage.Utc(nowUtc)); using var r = cmd.ExecuteReader(); r.Read();
            return new QueueStatusSummary(queueName, r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt32(3), r.IsDBNull(4) ? 0 : r.GetInt32(4));
        }

        public IReadOnlyList<SliceStatusReadout> GetSliceStatus(string jobId)
        {
            using var c = connectionFactory.OpenConnection(); using var cmd = SqliteStorage.Command(c, null, """
                SELECT css.job_id, css.slice_start_utc, css.slice_end_utc, css.state, css.attempt,
                       (SELECT sa.attempt FROM slice_attempts sa WHERE sa.job_id=css.job_id AND sa.slice_start_utc=css.slice_start_utc AND sa.slice_end_utc=css.slice_end_utc AND sa.status='Succeeded' ORDER BY sa.attempt DESC, COALESCE(sa.completed_at_utc, sa.started_at_utc) DESC LIMIT 1) successful_attempt,
                       (SELECT sa.status FROM slice_attempts sa WHERE sa.job_id=css.job_id AND sa.slice_start_utc=css.slice_start_utc AND sa.slice_end_utc=css.slice_end_utc ORDER BY sa.attempt DESC, COALESCE(sa.completed_at_utc, sa.started_at_utc) DESC LIMIT 1) latest_attempt_status,
                       (SELECT COALESCE(sa.completed_at_utc, sa.started_at_utc) FROM slice_attempts sa WHERE sa.job_id=css.job_id AND sa.slice_start_utc=css.slice_start_utc AND sa.slice_end_utc=css.slice_end_utc ORDER BY sa.attempt DESC, COALESCE(sa.completed_at_utc, sa.started_at_utc) DESC LIMIT 1) latest_attempt_time,
                       css.updated_at_utc
                FROM current_slice_state css WHERE css.job_id=$j ORDER BY css.slice_start_utc;
                """); cmd.Add("$j", jobId); using var r = cmd.ExecuteReader(); var results = new List<SliceStatusReadout>();
            while (r.Read()) results.Add(new SliceStatusReadout(r.GetString(0), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"), r.GetString(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal), SqliteStorage.ReadUtc(r, "updated_at_utc")));
            return results;
        }

        public RetentionCleanupResult CleanupOldReadModels(DateTimeOffset cutoffUtc, int batchSize = 500)
        {
            var id = Guid.NewGuid().ToString("N"); using var c = connectionFactory.OpenConnection(); using var tx = c.BeginTransaction();
            Delete(c, tx, "operational_logs", "recorded_at_utc < $cutoff", cutoffUtc, batchSize, out var logs);
            Delete(c, tx, "slice_attempts", "COALESCE(completed_at_utc, started_at_utc) < $cutoff AND status <> 'Started'", cutoffUtc, batchSize, out var attempts);
            Delete(c, tx, "scheduled_slices", "scheduled_at_utc < $cutoff", cutoffUtc, batchSize, out var scheduled);
            using (var cmd = SqliteStorage.Command(c, tx, "INSERT INTO retention_runs (retention_run_id,policy_name,status,cutoff_utc,rows_scanned,rows_deleted,started_at_utc,completed_at_utc,details_json) VALUES ($id,'read-model-retention','Completed',$cutoff,$scanned,$deleted,$now,$now,$details);"))
            { cmd.Add("$id", id); cmd.Add("$cutoff", SqliteStorage.Utc(cutoffUtc)); cmd.Add("$scanned", logs + attempts + scheduled); cmd.Add("$deleted", logs + attempts + scheduled); cmd.Add("$now", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.Add("$details", $"{{\"logsDeleted\":{logs},\"attemptsDeleted\":{attempts},\"scheduledSlicesDeleted\":{scheduled}}}"); cmd.ExecuteNonQuery(); }
            tx.Commit();
            using (var checkpoint = SqliteStorage.Command(c, null, "PRAGMA wal_checkpoint(PASSIVE);")) checkpoint.ExecuteNonQuery();
            return new RetentionCleanupResult(id, logs, attempts, scheduled);
        }

        private static void Delete(SqliteConnection c, SqliteTransaction tx, string table, string where, DateTimeOffset cutoff, int batchSize, out int rows)
        {
            using var cmd = SqliteStorage.Command(c, tx, $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {where} LIMIT $take);");
            cmd.Add("$cutoff", SqliteStorage.Utc(cutoff)); cmd.Add("$take", batchSize); rows = cmd.ExecuteNonQuery();
        }
    }
}
