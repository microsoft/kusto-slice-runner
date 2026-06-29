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
    public sealed record RetentionCleanupResult(string RetentionRunId, int LogsDeleted, int AttemptsDeleted, int ScheduledSlicesDeleted, int IngestionThrottlesDeleted, int QueueRowsDeleted)
    {
        public int TotalDeleted => LogsDeleted + AttemptsDeleted + ScheduledSlicesDeleted + IngestionThrottlesDeleted + QueueRowsDeleted;
    }
    public sealed record SliceThroughputSample(int SucceededCount, DateTimeOffset? FirstCompletedUtc, DateTimeOffset? LastCompletedUtc);
    public sealed record SliceAttemptRow(string AttemptId, string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, int Attempt, string Status, string? WorkerId, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, string? ErrorCode, string? ErrorMessage);
    public sealed record OperationalLogRow(string LogId, string? JobId, DateTimeOffset? SliceStartUtc, DateTimeOffset? SliceEndUtc, string Level, string Message, string? Category, string? Exception, DateTimeOffset RecordedAtUtc);
    public sealed record SliceStateEventRow(string EventId, string JobId, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, string EventType, string? State, int? Attempt, string? Reason, string? Actor, DateTimeOffset RecordedAtUtc);

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

        // Per-slice attempt history for one job, optionally narrowed to a single slice window, with the
        // most recent activity first. Backs the job-details "attempts" readout.
        public IReadOnlyList<SliceAttemptRow> GetSliceAttempts(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT attempt_id, job_id, slice_start_utc, slice_end_utc, attempt, status, worker_id, started_at_utc, completed_at_utc, error_code, error_message
                FROM slice_attempts
                WHERE job_id=$jobId
                  AND ($sliceStart IS NULL OR slice_start_utc=$sliceStart)
                  AND ($sliceEnd IS NULL OR slice_end_utc=$sliceEnd)
                ORDER BY COALESCE(completed_at_utc, started_at_utc, slice_start_utc) DESC, attempt DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$sliceStart", sliceStartUtc is null ? null : SqliteStorage.Utc(sliceStartUtc.Value));
            cmd.Add("$sliceEnd", sliceEndUtc is null ? null : SqliteStorage.Utc(sliceEndUtc.Value));
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var rows = new List<SliceAttemptRow>();
            while (r.Read())
            {
                rows.Add(new SliceAttemptRow(
                    r.GetString(0),
                    r.GetString(1),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetInt32(4),
                    r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    SqliteStorage.ReadNullableUtc(r, "started_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "completed_at_utc"),
                    r.IsDBNull(9) ? null : r.GetString(9),
                    r.IsDBNull(10) ? null : r.GetString(10)));
            }

            return rows;
        }

        // Operational log lines for one job, optionally narrowed to a single slice window, newest first.
        // Backs the job-details "logs" readout.
        public IReadOnlyList<OperationalLogRow> GetOperationalLogs(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT log_id, job_id, slice_start_utc, slice_end_utc, level, message, category, exception, recorded_at_utc
                FROM operational_logs
                WHERE job_id=$jobId
                  AND ($sliceStart IS NULL OR slice_start_utc=$sliceStart)
                  AND ($sliceEnd IS NULL OR slice_end_utc=$sliceEnd)
                ORDER BY recorded_at_utc DESC, log_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$sliceStart", sliceStartUtc is null ? null : SqliteStorage.Utc(sliceStartUtc.Value));
            cmd.Add("$sliceEnd", sliceEndUtc is null ? null : SqliteStorage.Utc(sliceEndUtc.Value));
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var rows = new List<OperationalLogRow>();
            while (r.Read())
            {
                rows.Add(new OperationalLogRow(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    SqliteStorage.ReadNullableUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadNullableUtc(r, "slice_end_utc"),
                    r.GetString(4),
                    r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            }

            return rows;
        }

        // Slice state-transition events for one job, optionally narrowed to a single slice window, newest
        // first. Backs the job-details "events" readout.
        public IReadOnlyList<SliceStateEventRow> GetSliceStateEvents(string jobId, DateTimeOffset? sliceStartUtc = null, DateTimeOffset? sliceEndUtc = null, int take = 50)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT event_id, job_id, slice_start_utc, slice_end_utc, event_type, state, attempt, reason, actor, recorded_at_utc
                FROM slice_state_events
                WHERE job_id=$jobId
                  AND ($sliceStart IS NULL OR slice_start_utc=$sliceStart)
                  AND ($sliceEnd IS NULL OR slice_end_utc=$sliceEnd)
                ORDER BY recorded_at_utc DESC, event_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$sliceStart", sliceStartUtc is null ? null : SqliteStorage.Utc(sliceStartUtc.Value));
            cmd.Add("$sliceEnd", sliceEndUtc is null ? null : SqliteStorage.Utc(sliceEndUtc.Value));
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var rows = new List<SliceStateEventRow>();
            while (r.Read())
            {
                rows.Add(new SliceStateEventRow(
                    r.GetString(0),
                    r.GetString(1),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetInt32(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            }

            return rows;
        }

        // Earliest available-at time of each job's queued work, keyed by job. Drives the dashboard's
        // next-eligible hint for jobs that already have queued slices.
        public IReadOnlyDictionary<string, DateTimeOffset> GetQueuedAvailabilityByJob()
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT job_id, MIN(available_at_utc) AS available_at_utc
                FROM work_queue
                WHERE state = 'Queued'
                GROUP BY job_id;
                """);
            using var r = cmd.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            while (r.Read())
            {
                results[r.GetString(0)] = SqliteStorage.ReadUtc(r, "available_at_utc");
            }

            return results;
        }

        // Latest materialized slice-end per job, keyed by job. Drives the dashboard's next-window
        // computation for jobs with no queued work.
        public IReadOnlyDictionary<string, DateTimeOffset> GetLatestSliceEndsByJob()
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT job_id, MAX(slice_end_utc) AS slice_end_utc
                FROM current_slice_state
                GROUP BY job_id;
                """);
            using var r = cmd.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            while (r.Read())
            {
                results[r.GetString(0)] = SqliteStorage.ReadUtc(r, "slice_end_utc");
            }

            return results;
        }

        public SliceThroughputSample GetRecentSucceededThroughput(string jobId, DateTimeOffset sinceUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT COUNT(*) AS succeeded_count, MIN(completed_at_utc) AS first_completed, MAX(completed_at_utc) AS last_completed
                FROM slice_attempts
                WHERE job_id = $j AND status = 'Succeeded' AND completed_at_utc IS NOT NULL AND completed_at_utc >= $since;
                """);
            cmd.Add("$j", jobId); cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            using var r = cmd.ExecuteReader(); r.Read();
            return new SliceThroughputSample(
                r.IsDBNull(0) ? 0 : r.GetInt32(0),
                SqliteStorage.ReadNullableUtc(r, "first_completed"),
                SqliteStorage.ReadNullableUtc(r, "last_completed"));
        }

        // Prunes non-authoritative operational telemetry older than the cutoffs. The authoritative
        // window-history (current_slice_state, slice_state_events) and every catalog/lifecycle/
        // audit/rerun/repair row are never touched, so scheduler idempotency, rerun eligibility,
        // dependency readiness, and the started-job field guard are unaffected. Only terminal
        // work_queue rows (Completed/DeadLettered) are pruned; Queued/Leased rows stay claimable.
        // Chart- and advisor-backing tables (slice_attempts, ingestion_throttle_observations) use
        // the older protected cutoff so the dashboard's selectable chart range never thins. Each
        // table drains in batches with per-batch commits to keep write locks short on the live
        // database; one retention_runs row summarizes the pass.
        public RetentionCleanupResult CleanupOldReadModels(DateTimeOffset cutoffUtc, DateTimeOffset chartProtectedCutoffUtc, int batchSize = 500)
        {
            if (batchSize < 1) batchSize = 1;
            var id = Guid.NewGuid().ToString("N"); using var c = connectionFactory.OpenConnection();
            var logs = Drain(c, "operational_logs", "recorded_at_utc < $cutoff", cutoffUtc, batchSize);
            var attempts = Drain(c, "slice_attempts", "COALESCE(completed_at_utc, started_at_utc) < $cutoff AND status <> 'Started'", chartProtectedCutoffUtc, batchSize);
            var scheduled = Drain(c, "scheduled_slices", "scheduled_at_utc < $cutoff", cutoffUtc, batchSize);
            var throttles = Drain(c, "ingestion_throttle_observations", "observed_at_utc < $cutoff", chartProtectedCutoffUtc, batchSize);
            var queueRows = Drain(c, "work_queue", "state IN ('Completed','DeadLettered') AND updated_at_utc < $cutoff", cutoffUtc, batchSize);
            var deleted = logs + attempts + scheduled + throttles + queueRows;
            using (var cmd = SqliteStorage.Command(c, null, "INSERT INTO retention_runs (retention_run_id,policy_name,status,cutoff_utc,rows_scanned,rows_deleted,started_at_utc,completed_at_utc,details_json) VALUES ($id,'read-model-retention','Completed',$cutoff,$scanned,$deleted,$now,$now,$details);"))
            { cmd.Add("$id", id); cmd.Add("$cutoff", SqliteStorage.Utc(cutoffUtc)); cmd.Add("$scanned", deleted); cmd.Add("$deleted", deleted); cmd.Add("$now", SqliteStorage.Utc(DateTimeOffset.UtcNow)); cmd.Add("$details", $"{{\"logsDeleted\":{logs},\"attemptsDeleted\":{attempts},\"scheduledSlicesDeleted\":{scheduled},\"ingestionThrottlesDeleted\":{throttles},\"queueRowsDeleted\":{queueRows}}}"); cmd.ExecuteNonQuery(); }
            using (var checkpoint = SqliteStorage.Command(c, null, "PRAGMA wal_checkpoint(PASSIVE);")) checkpoint.ExecuteNonQuery();
            return new RetentionCleanupResult(id, logs, attempts, scheduled, throttles, queueRows);
        }

        // Deletes eligible rows in batches, committing each batch so a long backlog never holds a
        // single long write lock on the live database. Stops once a batch clears fewer than the
        // batch size (the eligible set is drained); the iteration cap is a defensive bound.
        private static int Drain(SqliteConnection c, string table, string where, DateTimeOffset cutoff, int batchSize)
        {
            var total = 0;
            for (var iteration = 0; iteration < 1_000_000; iteration++)
            {
                int deleted;
                using (var tx = c.BeginTransaction())
                {
                    using var cmd = SqliteStorage.Command(c, tx, $"DELETE FROM {table} WHERE rowid IN (SELECT rowid FROM {table} WHERE {where} LIMIT $take);");
                    cmd.Add("$cutoff", SqliteStorage.Utc(cutoff)); cmd.Add("$take", batchSize);
                    deleted = cmd.ExecuteNonQuery();
                    tx.Commit();
                }

                total += deleted;
                if (deleted < batchSize) break;
            }

            return total;
        }
    }
}
