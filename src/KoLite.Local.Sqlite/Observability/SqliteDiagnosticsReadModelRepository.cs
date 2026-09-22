// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Observability
{
    public sealed record RunningSliceReadout(
        string JobId,
        string ActivityId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        bool LeaseExpired,
        string? LastErrorCode,
        string? LastErrorMessage,
        DateTimeOffset UpdatedAtUtc,
        // Wall-clock start of the in-flight attempt (slice_attempts row with status='Started' and no
        // completion yet). Null when no such attempt row exists (e.g. a stalled/older running slice).
        DateTimeOffset? StartedAtUtc,
        int? TotalChunks = null);

    public sealed record ActivityExecutionCounts(int Running, int Queued);

    public sealed record RunningChunkExecutionReadout(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int ChunkId,
        int TotalChunks,
        string State,
        int Attempt,
        string? WorkerId,
        DateTimeOffset? LeaseExpiresAtUtc,
        bool LeaseExpired,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? StartedAtUtc);

    public sealed record ActivityRunningSnapshot(
        IReadOnlyList<RunningSliceReadout> Slices,
        IReadOnlyList<RunningChunkExecutionReadout> ChunkExecutions);

    public sealed record SliceStateReadout(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string State,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        bool LeaseExpired,
        string? LastErrorCode,
        string? LastErrorMessage,
        DateTimeOffset UpdatedAtUtc);

    public sealed record ThroughputBucket(string? JobId, DateTimeOffset BucketStartUtc, int SucceededCount);

    public sealed record DiagnosticsLogReadout(
        string LogId,
        string? JobId,
        DateTimeOffset? SliceStartUtc,
        DateTimeOffset? SliceEndUtc,
        string Level,
        string Message,
        string? Category,
        string? Exception,
        DateTimeOffset RecordedAtUtc,
        int? ChunkId,
        int? TotalChunks);

    public sealed record AuditEventReadout(
        string AuditId,
        string? Actor,
        string Action,
        string SubjectType,
        string? SubjectId,
        string PayloadJson,
        DateTimeOffset RecordedAtUtc);

    public sealed record RerunBatchSummary(
        string RerunBatchId,
        string RootJobId,
        DateTimeOffset RootStartUtc,
        DateTimeOffset RootEndUtc,
        string? RequestedBy,
        string Reason,
        string Status,
        bool KustoCleanupAcknowledged,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? CompletedAtUtc);

    public sealed record RepairBatchSummary(
        string RepairBatchId,
        string? JobId,
        string? RequestedBy,
        string Reason,
        string Status,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? CompletedAtUtc);

    public sealed record FailureSummaryReadout(
        string RunId,
        string? JobId,
        string SummaryKind,
        string? FailureCode,
        int FailureCount,
        DateTimeOffset? FirstSeenUtc,
        DateTimeOffset? LastSeenUtc,
        DateTimeOffset CreatedAtUtc);

    // Read-only diagnostic queries that the dashboard read models do not already expose:
    // cross-job/in-flight slice leases, time-bucketed throughput, global logs, the system
    // audit trail, and rerun/repair batch listings. Every query is bounded by the caller's
    // take cap (and, where relevant, an explicit time window) so a diagnostic call never
    // materializes the whole local store. This type performs no writes.
    public sealed class SqliteDiagnosticsReadModelRepository
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;

        public SqliteDiagnosticsReadModelRepository(IKoLiteSqliteConnectionFactory connectionFactory)
        {
            this.connectionFactory = connectionFactory;
        }

        // Current Running slices (optionally for one job), oldest first so a slice whose lease has
        // expired but is still held floats to the top - the fingerprint of a stalled job.
        public IReadOnlyList<RunningSliceReadout> GetRunningSlices(string? jobId, DateTimeOffset nowUtc, int take)
        {
            return GetRunningSlices(jobId, nowUtc, cursorUpdatedAtUtc: null, cursorId: null, take);
        }

        public IReadOnlyList<RunningSliceReadout> GetRunningSlices(
            string? jobId,
            DateTimeOffset nowUtc,
            DateTimeOffset? cursorUpdatedAtUtc,
            string? cursorId,
            int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT css.job_id, jd.activity_id, css.slice_start_utc, css.slice_end_utc, css.attempt,
                       css.lease_owner, css.lease_expires_at_utc, css.last_error_code, css.last_error_message, css.updated_at_utc,
                       (SELECT MAX(sa.started_at_utc) FROM slice_attempts sa
                         WHERE sa.job_id = css.job_id
                           AND sa.slice_start_utc = css.slice_start_utc
                           AND sa.slice_end_utc = css.slice_end_utc
                           AND sa.status = 'Started'
                           AND sa.completed_at_utc IS NULL) AS started_running_at_utc,
                       json_extract(jd.schedule_json, '$.chunks') AS total_chunks
                FROM current_slice_state css
                JOIN job_definitions jd ON jd.job_id = css.job_id
                WHERE css.state = 'Running' AND ($jobId IS NULL OR css.job_id = $jobId)
                  AND (
                      $cursor_updated IS NULL
                      OR css.updated_at_utc > $cursor_updated
                      OR (
                          css.updated_at_utc = $cursor_updated
                          AND (css.job_id || '|' || css.slice_start_utc || '|' || css.slice_end_utc) > $cursor_id
                      )
                  )
                ORDER BY css.updated_at_utc ASC, css.job_id ASC, css.slice_start_utc ASC, css.slice_end_utc ASC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$cursor_updated", cursorUpdatedAtUtc is null ? null : SqliteStorage.Utc(cursorUpdatedAtUtc.Value));
            cmd.Add("$cursor_id", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<RunningSliceReadout>();
            while (r.Read())
            {
                var leaseExpires = SqliteStorage.ReadNullableUtc(r, "lease_expires_at_utc");
                results.Add(new RunningSliceReadout(
                    r.GetString(0),
                    r.GetString(1),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    leaseExpires,
                    leaseExpires is not null && leaseExpires.Value <= nowUtc.ToUniversalTime(),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    SqliteStorage.ReadUtc(r, "updated_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "started_running_at_utc"),
                    r.IsDBNull(r.GetOrdinal("total_chunks")) ? null : r.GetInt32(r.GetOrdinal("total_chunks"))));
            }

            return results;
        }

        public ActivityExecutionCounts GetActivityExecutionCounts()
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT
                    (
                        SELECT COUNT(*)
                        FROM current_slice_chunk_state child
                        WHERE child.state='Running'
                    )
                    +
                    (
                        SELECT COUNT(*)
                        FROM current_slice_state parent
                        JOIN job_definitions jd ON jd.job_id=parent.job_id
                        WHERE parent.state='Running'
                          AND json_extract(jd.schedule_json, '$.chunks') IS NULL
                    ) AS running_executions,
                    (
                        SELECT COUNT(*)
                        FROM work_queue
                        WHERE state='Queued'
                    ) AS queued_executions;
                """);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            return new ActivityExecutionCounts(reader.GetInt32(0), reader.GetInt32(1));
        }

        public ActivityRunningSnapshot GetRunningActivitySnapshot(DateTimeOffset nowUtc, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                WITH running_parents AS (
                    SELECT css.job_id,
                           jd.activity_id,
                           css.slice_start_utc,
                           css.slice_end_utc,
                           css.attempt,
                           css.lease_owner,
                           css.lease_expires_at_utc,
                           css.last_error_code,
                           css.last_error_message,
                           css.updated_at_utc,
                           (
                               SELECT MAX(sa.started_at_utc)
                               FROM slice_attempts sa
                               WHERE sa.job_id=css.job_id
                                 AND sa.slice_start_utc=css.slice_start_utc
                                 AND sa.slice_end_utc=css.slice_end_utc
                                 AND sa.status='Started'
                                 AND sa.completed_at_utc IS NULL
                           ) AS started_running_at_utc,
                           json_extract(jd.schedule_json, '$.chunks') AS total_chunks
                    FROM current_slice_state css
                    JOIN job_definitions jd ON jd.job_id=css.job_id
                    WHERE css.state='Running'
                    ORDER BY css.updated_at_utc ASC,css.slice_start_utc ASC,css.job_id ASC
                    LIMIT $take
                )
                SELECT parent.job_id,
                       parent.activity_id,
                       parent.slice_start_utc,
                       parent.slice_end_utc,
                       parent.attempt,
                       parent.lease_owner,
                       parent.lease_expires_at_utc,
                       parent.last_error_code,
                       parent.last_error_message,
                       parent.updated_at_utc,
                       parent.started_running_at_utc,
                       parent.total_chunks,
                       child.chunk_id AS child_chunk_id,
                       child.total_chunks AS child_total_chunks,
                       child.state AS child_state,
                       child.attempt AS child_attempt,
                       child.lease_owner AS child_lease_owner,
                       child.lease_expires_at_utc AS child_lease_expires_at_utc,
                       child.updated_at_utc AS child_updated_at_utc,
                       (
                           SELECT MAX(sa.started_at_utc)
                           FROM slice_attempts sa
                           WHERE sa.job_id=child.job_id
                             AND sa.slice_start_utc=child.slice_start_utc
                             AND sa.slice_end_utc=child.slice_end_utc
                             AND sa.chunk_id=child.chunk_id
                             AND sa.attempt=child.attempt
                             AND sa.status='Started'
                             AND sa.completed_at_utc IS NULL
                       ) AS child_started_running_at_utc
                FROM running_parents parent
                LEFT JOIN current_slice_chunk_state child
                  ON child.job_id=parent.job_id
                 AND child.slice_start_utc=parent.slice_start_utc
                 AND child.slice_end_utc=parent.slice_end_utc
                ORDER BY parent.updated_at_utc,parent.slice_start_utc,parent.job_id,child.chunk_id;
                """);
            cmd.Add("$take", Math.Max(1, take));
            using var reader = cmd.ExecuteReader();
            var slices = new List<RunningSliceReadout>();
            var chunks = new List<RunningChunkExecutionReadout>();
            var seenSlices = new HashSet<(string JobId, string Start, string End)>();
            var now = nowUtc.ToUniversalTime();
            while (reader.Read())
            {
                var key = (reader.GetString(0), reader.GetString(2), reader.GetString(3));
                if (seenSlices.Add(key))
                {
                    var leaseExpires = SqliteStorage.ReadNullableUtc(reader, "lease_expires_at_utc");
                    slices.Add(new RunningSliceReadout(
                        key.Item1,
                        reader.GetString(1),
                        SqliteStorage.ReadUtc(reader, "slice_start_utc"),
                        SqliteStorage.ReadUtc(reader, "slice_end_utc"),
                        reader.GetInt32(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        leaseExpires,
                        leaseExpires is not null && leaseExpires.Value <= now,
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetString(8),
                        SqliteStorage.ReadUtc(reader, "updated_at_utc"),
                        SqliteStorage.ReadNullableUtc(reader, "started_running_at_utc"),
                        reader.IsDBNull(11) ? null : reader.GetInt32(11)));
                }

                if (reader.IsDBNull(12))
                {
                    continue;
                }

                var childLeaseExpires = SqliteStorage.ReadNullableUtc(reader, "child_lease_expires_at_utc");
                chunks.Add(new RunningChunkExecutionReadout(
                    key.Item1,
                    SqliteStorage.ReadUtc(reader, "slice_start_utc"),
                    SqliteStorage.ReadUtc(reader, "slice_end_utc"),
                    reader.GetInt32(12),
                    reader.GetInt32(13),
                    reader.GetString(14),
                    reader.GetInt32(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    childLeaseExpires,
                    childLeaseExpires is not null && childLeaseExpires.Value <= now,
                    SqliteStorage.ReadUtc(reader, "child_updated_at_utc"),
                    SqliteStorage.ReadNullableUtc(reader, "child_started_running_at_utc")));
            }

            return new ActivityRunningSnapshot(slices, chunks);
        }

        // Typical (median) wall-clock duration of recent successful attempts, per job, for the given
        // job ids. Used by the Activity page to project a running slice's ETA (started + median). Only
        // Succeeded attempts with both timestamps and a positive elapsed count; the newest
        // perJobSampleCap per job are considered (bounded by an in-SQL ROW_NUMBER window). Jobs with no
        // usable sample are omitted from the result.
        public IReadOnlyDictionary<string, TimeSpan> GetTypicalSuccessfulDurationsByJob(
            IReadOnlyCollection<string> jobIds, DateTimeOffset sinceUtc, int perJobSampleCap)
        {
            var result = new Dictionary<string, TimeSpan>();
            if (jobIds.Count == 0 || perJobSampleCap <= 0)
            {
                return result;
            }

            var distinctJobIds = jobIds.Distinct().ToList();
            var placeholders = new string[distinctJobIds.Count];
            for (var i = 0; i < distinctJobIds.Count; i++)
            {
                placeholders[i] = "$j" + i.ToString(CultureInfo.InvariantCulture);
            }

            var inClause = string.Join(", ", placeholders);
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, $"""
                SELECT job_id, started_at_utc, completed_at_utc, effective_waves
                FROM (
                    SELECT sa.job_id, sa.started_at_utc, sa.completed_at_utc,
                           (
                               COALESCE(json_extract(jd.schedule_json, '$.chunks'), 1)
                               + MIN(
                                   COALESCE(json_extract(jd.schedule_json, '$.chunks'), 1),
                                   MAX(1, COALESCE(json_extract(jd.schedule_json, '$.maxParallelism'), 1)))
                               - 1
                           ) / MIN(
                               COALESCE(json_extract(jd.schedule_json, '$.chunks'), 1),
                               MAX(1, COALESCE(json_extract(jd.schedule_json, '$.maxParallelism'), 1))
                           ) AS effective_waves,
                           ROW_NUMBER() OVER (PARTITION BY sa.job_id ORDER BY sa.completed_at_utc DESC) AS rn
                    FROM slice_attempts sa
                    JOIN job_definitions jd ON jd.job_id = sa.job_id
                    WHERE sa.status = 'Succeeded'
                      AND sa.started_at_utc IS NOT NULL
                      AND sa.completed_at_utc IS NOT NULL
                      AND sa.completed_at_utc >= $since
                      AND sa.job_id IN ({inClause})
                )
                WHERE rn <= $cap;
                """);
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            cmd.Add("$cap", perJobSampleCap);
            for (var i = 0; i < distinctJobIds.Count; i++)
            {
                cmd.Add(placeholders[i], distinctJobIds[i]);
            }

            var samplesByJob = new Dictionary<string, List<double>>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var started = SqliteStorage.ReadUtc(r, "started_at_utc");
                    var completed = SqliteStorage.ReadUtc(r, "completed_at_utc");
                    var elapsed = (completed - started).TotalSeconds * r.GetInt32(3);
                    if (elapsed <= 0)
                    {
                        continue;
                    }

                    var jobId = r.GetString(0);
                    if (!samplesByJob.TryGetValue(jobId, out var samples))
                    {
                        samples = new List<double>();
                        samplesByJob[jobId] = samples;
                    }

                    samples.Add(elapsed);
                }
            }

            foreach (var (jobId, samples) in samplesByJob)
            {
                if (samples.Count == 0)
                {
                    continue;
                }

                samples.Sort();
                result[jobId] = MedianDuration(samples);
            }

            return result;
        }

        // Median wall-clock duration of recent fully successful logical windows. Chunked samples run
        // from the earliest attempt start through the final required chunk success, include automatic
        // retries, and exclude manually repaired windows whose operator delay would skew the ETA.
        public IReadOnlyDictionary<string, TimeSpan> GetTypicalCompletedSliceDurationsByJob(
            IReadOnlyCollection<string> jobIds,
            DateTimeOffset sinceUtc,
            int perJobSampleCap)
        {
            var result = new Dictionary<string, TimeSpan>();
            if (jobIds.Count == 0 || perJobSampleCap <= 0)
            {
                return result;
            }

            var distinctJobIds = jobIds.Distinct().ToList();
            var placeholders = new string[distinctJobIds.Count];
            for (var i = 0; i < distinctJobIds.Count; i++)
            {
                placeholders[i] = "$logical" + i.ToString(CultureInfo.InvariantCulture);
            }

            var inClause = string.Join(", ", placeholders);
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, $"""
                WITH eligible_windows AS (
                    SELECT css.job_id,
                           css.slice_start_utc,
                           css.slice_end_utc,
                           css.updated_at_utc AS latest_success_utc
                    FROM current_slice_state css
                    WHERE css.state='Completed'
                      AND css.updated_at_utc >= $since
                      AND css.job_id IN ({inClause})
                      AND NOT EXISTS (
                          SELECT 1
                          FROM repair_slices repaired
                          WHERE repaired.job_id=css.job_id
                            AND repaired.slice_start_utc=css.slice_start_utc
                            AND repaired.slice_end_utc=css.slice_end_utc
                            AND repaired.status IN ('Queued','Completed','Failed')
                      )
                ),
                recent_candidates AS (
                    SELECT job_id,slice_start_utc,slice_end_utc
                    FROM (
                        SELECT job_id,
                               slice_start_utc,
                               slice_end_utc,
                               ROW_NUMBER() OVER (
                                   PARTITION BY job_id
                                   ORDER BY latest_success_utc DESC,slice_start_utc DESC,slice_end_utc DESC
                               ) AS rn
                        FROM eligible_windows
                    )
                    WHERE rn <= $cap
                ),
                completed_windows AS (
                    SELECT sa.job_id,
                           sa.slice_start_utc,
                           sa.slice_end_utc,
                           MIN(sa.started_at_utc) AS first_started_at_utc,
                           MAX(CASE WHEN sa.status='Succeeded' THEN sa.completed_at_utc END) AS final_completed_at_utc,
                           COALESCE(json_extract(jd.schedule_json, '$.chunks'), 1) AS expected_executions,
                           COUNT(DISTINCT CASE WHEN sa.status='Succeeded' THEN COALESCE(sa.chunk_id, -1) END) AS succeeded_executions
                    FROM slice_attempts sa
                    JOIN recent_candidates candidate
                      ON candidate.job_id=sa.job_id
                     AND candidate.slice_start_utc=sa.slice_start_utc
                     AND candidate.slice_end_utc=sa.slice_end_utc
                    JOIN job_definitions jd ON jd.job_id=sa.job_id
                    WHERE sa.started_at_utc IS NOT NULL
                    GROUP BY sa.job_id,sa.slice_start_utc,sa.slice_end_utc
                    HAVING succeeded_executions >= expected_executions
                )
                SELECT job_id,first_started_at_utc,final_completed_at_utc
                FROM completed_windows
                WHERE final_completed_at_utc >= $since;
                """);
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            cmd.Add("$cap", perJobSampleCap);
            for (var i = 0; i < distinctJobIds.Count; i++)
            {
                cmd.Add(placeholders[i], distinctJobIds[i]);
            }

            var samplesByJob = new Dictionary<string, List<double>>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var started = SqliteStorage.ReadUtc(reader, "first_started_at_utc");
                    var completed = SqliteStorage.ReadUtc(reader, "final_completed_at_utc");
                    var elapsed = (completed - started).TotalSeconds;
                    if (elapsed <= 0)
                    {
                        continue;
                    }

                    var jobId = reader.GetString(0);
                    if (!samplesByJob.TryGetValue(jobId, out var samples))
                    {
                        samples = new List<double>();
                        samplesByJob[jobId] = samples;
                    }

                    samples.Add(elapsed);
                }
            }

            foreach (var (jobId, samples) in samplesByJob)
            {
                samples.Sort();
                result[jobId] = MedianDuration(samples);
            }

            return result;
        }

        // Median of an ascending-sorted, non-empty sample of seconds.
        private static TimeSpan MedianDuration(IReadOnlyList<double> sortedSeconds)
        {
            var count = sortedSeconds.Count;
            var mid = count / 2;
            var medianSeconds = count % 2 == 1
                ? sortedSeconds[mid]
                : (sortedSeconds[mid - 1] + sortedSeconds[mid]) / 2.0;
            return TimeSpan.FromSeconds(medianSeconds);
        }

        // Materialized slice states for one job with their lease fields, newest slice first, optionally
        // filtered by state and slice-start window.
        public IReadOnlyList<SliceStateReadout> GetSlices(string jobId, string? state, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, DateTimeOffset nowUtc, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT job_id, slice_start_utc, slice_end_utc, state, attempt,
                       lease_owner, lease_expires_at_utc, last_error_code, last_error_message, updated_at_utc
                FROM current_slice_state
                WHERE job_id = $jobId
                  AND ($state IS NULL OR state = $state)
                  AND ($from IS NULL OR slice_start_utc >= $from)
                  AND ($to IS NULL OR slice_start_utc < $to)
                ORDER BY slice_start_utc DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$state", state);
            cmd.Add("$from", fromUtc is null ? null : SqliteStorage.Utc(fromUtc.Value));
            cmd.Add("$to", toUtc is null ? null : SqliteStorage.Utc(toUtc.Value));
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<SliceStateReadout>();
            while (r.Read())
            {
                var leaseExpires = SqliteStorage.ReadNullableUtc(r, "lease_expires_at_utc");
                results.Add(new SliceStateReadout(
                    r.GetString(0),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetString(3),
                    r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    leaseExpires,
                    leaseExpires is not null && leaseExpires.Value <= nowUtc.ToUniversalTime(),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    SqliteStorage.ReadUtc(r, "updated_at_utc")));
            }

            return results;
        }

        public IReadOnlyList<SliceStateReadout> GetSlicesPage(
            string? jobId,
            string? state,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            DateTimeOffset nowUtc,
            DateTimeOffset? cursorUpdatedAtUtc,
            string? cursorId,
            int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT job_id, slice_start_utc, slice_end_utc, state, attempt,
                       lease_owner, lease_expires_at_utc, last_error_code, last_error_message, updated_at_utc
                FROM current_slice_state
                WHERE ($jobId IS NULL OR job_id = $jobId)
                  AND ($state IS NULL OR state = $state)
                  AND ($from IS NULL OR slice_start_utc >= $from)
                  AND ($to IS NULL OR slice_start_utc < $to)
                  AND (
                      $cursorUpdated IS NULL
                      OR updated_at_utc < $cursorUpdated
                      OR (
                          updated_at_utc = $cursorUpdated
                          AND (job_id || '|' || slice_start_utc || '|' || slice_end_utc) < $cursorId
                      )
                  )
                ORDER BY updated_at_utc DESC, job_id DESC, slice_start_utc DESC, slice_end_utc DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$state", state);
            cmd.Add("$from", fromUtc is null ? null : SqliteStorage.Utc(fromUtc.Value));
            cmd.Add("$to", toUtc is null ? null : SqliteStorage.Utc(toUtc.Value));
            cmd.Add("$cursorUpdated", cursorUpdatedAtUtc is null ? null : SqliteStorage.Utc(cursorUpdatedAtUtc.Value));
            cmd.Add("$cursorId", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<SliceStateReadout>();
            while (r.Read())
            {
                var leaseExpires = SqliteStorage.ReadNullableUtc(r, "lease_expires_at_utc");
                results.Add(new SliceStateReadout(
                    r.GetString(0),
                    SqliteStorage.ReadUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadUtc(r, "slice_end_utc"),
                    r.GetString(3),
                    r.GetInt32(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    leaseExpires,
                    leaseExpires is not null && leaseExpires.Value <= nowUtc.ToUniversalTime(),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetString(8),
                    SqliteStorage.ReadUtc(r, "updated_at_utc")));
            }

            return results;
        }

        // Succeeded-completion counts bucketed into fixed windows over [fromUtc, toUtc). Buckets are
        // aligned to the unix epoch so they are stable regardless of the query range. When groupByJob
        // is set each bucket is split per job, which is how "is the whole app stalled or just one job?"
        // is answered. When the bucket count exceeds take, the most recent buckets are kept (the recent
        // tail is never dropped) and the result is returned oldest-first.
        public IReadOnlyList<ThroughputBucket> GetThroughputSeries(string? jobId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int bucketSeconds, bool groupByJob, int take)
        {
            var safeBucketSeconds = Math.Max(1, bucketSeconds);
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, $"""
                SELECT bucket_epoch, bucket_job_id, succeeded_count
                FROM (
                    SELECT (CAST(strftime('%s', logical_completed_at) AS INTEGER) / $bucket) * $bucket AS bucket_epoch,
                           {(groupByJob ? "logical.job_id" : "NULL")} AS bucket_job_id,
                           COUNT(*) AS succeeded_count
                    FROM (
                        WITH candidates AS (
                            SELECT DISTINCT job_id, slice_start_utc, slice_end_utc
                            FROM slice_attempts
                            WHERE status = 'Succeeded'
                              AND completed_at_utc IS NOT NULL
                              AND completed_at_utc >= $from AND completed_at_utc < $to
                              AND ($jobId IS NULL OR job_id = $jobId)
                        )
                        SELECT sa.job_id, sa.slice_start_utc, sa.slice_end_utc, MAX(sa.completed_at_utc) AS logical_completed_at
                        FROM slice_attempts sa
                        JOIN candidates candidate
                          ON candidate.job_id = sa.job_id
                         AND candidate.slice_start_utc = sa.slice_start_utc
                         AND candidate.slice_end_utc = sa.slice_end_utc
                        JOIN job_definitions jd ON jd.job_id = sa.job_id
                        WHERE sa.status = 'Succeeded' AND sa.completed_at_utc IS NOT NULL
                          AND ($jobId IS NULL OR sa.job_id = $jobId)
                        GROUP BY sa.job_id, sa.slice_start_utc, sa.slice_end_utc
                        HAVING COUNT(DISTINCT COALESCE(sa.chunk_id, -1))
                            >= COALESCE(json_extract(jd.schedule_json, '$.chunks'), 1)
                    ) logical
                    WHERE logical_completed_at >= $from AND logical_completed_at < $to
                    GROUP BY {(groupByJob ? "bucket_epoch, logical.job_id" : "bucket_epoch")}
                    ORDER BY bucket_epoch DESC
                    LIMIT $take
                )
                ORDER BY bucket_epoch ASC, bucket_job_id ASC;
                """);
            cmd.Add("$bucket", safeBucketSeconds);
            cmd.Add("$from", SqliteStorage.Utc(fromUtc));
            cmd.Add("$to", SqliteStorage.Utc(toUtc));
            cmd.Add("$jobId", jobId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<ThroughputBucket>();
            while (r.Read())
            {
                var bucketStart = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0));
                results.Add(new ThroughputBucket(
                    r.IsDBNull(1) ? null : r.GetString(1),
                    bucketStart,
                    r.GetInt32(2)));
            }

            return results;
        }

        // Operational logs across all jobs (or one job), newest first, filtered by level/category/time.
        public IReadOnlyList<DiagnosticsLogReadout> GetLogs(string? jobId, string? level, string? category, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT log_id, job_id, slice_start_utc, slice_end_utc, level, message, category, exception, recorded_at_utc, chunk_id, total_chunks
                FROM operational_logs
                WHERE ($jobId IS NULL OR job_id = $jobId)
                  AND ($level IS NULL OR level = $level)
                  AND ($category IS NULL OR category = $category)
                  AND ($from IS NULL OR recorded_at_utc >= $from)
                  AND ($to IS NULL OR recorded_at_utc < $to)
                ORDER BY recorded_at_utc DESC, log_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$level", level);
            cmd.Add("$category", category);
            cmd.Add("$from", fromUtc is null ? null : SqliteStorage.Utc(fromUtc.Value));
            cmd.Add("$to", toUtc is null ? null : SqliteStorage.Utc(toUtc.Value));
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<DiagnosticsLogReadout>();
            while (r.Read())
            {
                results.Add(new DiagnosticsLogReadout(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    SqliteStorage.ReadNullableUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadNullableUtc(r, "slice_end_utc"),
                    r.GetString(4),
                    r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc"),
                    r.IsDBNull(9) ? null : r.GetInt32(9),
                    r.IsDBNull(10) ? null : r.GetInt32(10)));
            }

            return results;
        }

        public IReadOnlyList<DiagnosticsLogReadout> GetLogsPage(
            string? jobId,
            string? level,
            string? category,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            DateTimeOffset? cursorRecordedAtUtc,
            string? cursorId,
            int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT log_id, job_id, slice_start_utc, slice_end_utc, level, message, category, exception, recorded_at_utc, chunk_id, total_chunks
                FROM operational_logs
                WHERE ($jobId IS NULL OR job_id = $jobId)
                  AND ($level IS NULL OR level = $level)
                  AND ($category IS NULL OR category = $category)
                  AND ($from IS NULL OR recorded_at_utc >= $from)
                  AND ($to IS NULL OR recorded_at_utc < $to)
                  AND (
                      $cursorRecorded IS NULL
                      OR recorded_at_utc < $cursorRecorded
                      OR (recorded_at_utc = $cursorRecorded AND log_id < $cursorId)
                  )
                ORDER BY recorded_at_utc DESC, log_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$level", level);
            cmd.Add("$category", category);
            cmd.Add("$from", fromUtc is null ? null : SqliteStorage.Utc(fromUtc.Value));
            cmd.Add("$to", toUtc is null ? null : SqliteStorage.Utc(toUtc.Value));
            cmd.Add("$cursorRecorded", cursorRecordedAtUtc is null ? null : SqliteStorage.Utc(cursorRecordedAtUtc.Value));
            cmd.Add("$cursorId", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<DiagnosticsLogReadout>();
            while (r.Read())
            {
                results.Add(new DiagnosticsLogReadout(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    SqliteStorage.ReadNullableUtc(r, "slice_start_utc"),
                    SqliteStorage.ReadNullableUtc(r, "slice_end_utc"),
                    r.GetString(4),
                    r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(7) ? null : r.GetString(7),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc"),
                    r.IsDBNull(9) ? null : r.GetInt32(9),
                    r.IsDBNull(10) ? null : r.GetInt32(10)));
            }

            return results;
        }

        // The system audit trail (rerun planned/executed, lifecycle, hard delete, ...), newest first.
        public IReadOnlyList<AuditEventReadout> GetAuditEvents(string? subjectType, string? subjectId, string? action, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT audit_id, actor, action, subject_type, subject_id, payload_json, recorded_at_utc
                FROM system_audit
                WHERE ($subjectType IS NULL OR subject_type = $subjectType)
                  AND ($subjectId IS NULL OR subject_id = $subjectId)
                  AND ($action IS NULL OR action = $action)
                  AND ($from IS NULL OR recorded_at_utc >= $from)
                  AND ($to IS NULL OR recorded_at_utc < $to)
                ORDER BY recorded_at_utc DESC, audit_id DESC
                LIMIT $take;
                """);
            cmd.Add("$subjectType", subjectType);
            cmd.Add("$subjectId", subjectId);
            cmd.Add("$action", action);
            cmd.Add("$from", fromUtc is null ? null : SqliteStorage.Utc(fromUtc.Value));
            cmd.Add("$to", toUtc is null ? null : SqliteStorage.Utc(toUtc.Value));
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<AuditEventReadout>();
            while (r.Read())
            {
                results.Add(new AuditEventReadout(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetString(5),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            }

            return results;
        }

        public IReadOnlyList<AuditEventReadout> GetAuditEventsPage(
            string? subjectType,
            string? subjectId,
            string? action,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            DateTimeOffset? cursorRecordedAtUtc,
            string? cursorId,
            int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT audit_id, actor, action, subject_type, subject_id, payload_json, recorded_at_utc
                FROM system_audit
                WHERE ($subjectType IS NULL OR subject_type = $subjectType)
                  AND ($subjectId IS NULL OR subject_id = $subjectId)
                  AND ($action IS NULL OR action = $action)
                  AND ($from IS NULL OR recorded_at_utc >= $from)
                  AND ($to IS NULL OR recorded_at_utc < $to)
                  AND (
                      $cursorRecorded IS NULL
                      OR recorded_at_utc < $cursorRecorded
                      OR (recorded_at_utc = $cursorRecorded AND audit_id < $cursorId)
                  )
                ORDER BY recorded_at_utc DESC, audit_id DESC
                LIMIT $take;
                """);
            cmd.Add("$subjectType", subjectType);
            cmd.Add("$subjectId", subjectId);
            cmd.Add("$action", action);
            cmd.Add("$from", fromUtc is null ? null : SqliteStorage.Utc(fromUtc.Value));
            cmd.Add("$to", toUtc is null ? null : SqliteStorage.Utc(toUtc.Value));
            cmd.Add("$cursorRecorded", cursorRecordedAtUtc is null ? null : SqliteStorage.Utc(cursorRecordedAtUtc.Value));
            cmd.Add("$cursorId", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<AuditEventReadout>();
            while (r.Read())
            {
                results.Add(new AuditEventReadout(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetString(5),
                    SqliteStorage.ReadUtc(r, "recorded_at_utc")));
            }

            return results;
        }

        // Lightweight rerun-batch listing (summary columns only), newest request first. Full per-slice
        // detail stays behind SqliteRerunService.GetBatch.
        public IReadOnlyList<RerunBatchSummary> ListRerunBatches(string? rootJobId, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT rerun_batch_id, root_job_id, root_start_utc, root_end_utc, requested_by, reason, status,
                       kusto_cleanup_acknowledged, requested_at_utc, completed_at_utc
                FROM rerun_batches
                WHERE ($rootJobId IS NULL OR root_job_id = $rootJobId)
                ORDER BY requested_at_utc DESC, rerun_batch_id DESC
                LIMIT $take;
                """);
            cmd.Add("$rootJobId", rootJobId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<RerunBatchSummary>();
            while (r.Read())
            {
                results.Add(new RerunBatchSummary(
                    r.GetString(0),
                    r.GetString(1),
                    SqliteStorage.ReadUtc(r, "root_start_utc"),
                    SqliteStorage.ReadUtc(r, "root_end_utc"),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetString(5),
                    r.GetString(6),
                    r.GetInt32(7) == 1,
                    SqliteStorage.ReadUtc(r, "requested_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "completed_at_utc")));
            }

            return results;
        }

        public IReadOnlyList<RerunBatchSummary> ListRerunBatchesPage(
            string? rootJobId,
            DateTimeOffset? cursorRequestedAtUtc,
            string? cursorId,
            int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT rerun_batch_id, root_job_id, root_start_utc, root_end_utc, requested_by, reason, status,
                       kusto_cleanup_acknowledged, requested_at_utc, completed_at_utc
                FROM rerun_batches
                WHERE ($rootJobId IS NULL OR root_job_id = $rootJobId)
                  AND (
                      $cursorRequested IS NULL
                      OR requested_at_utc < $cursorRequested
                      OR (requested_at_utc = $cursorRequested AND rerun_batch_id < $cursorId)
                  )
                ORDER BY requested_at_utc DESC, rerun_batch_id DESC
                LIMIT $take;
                """);
            cmd.Add("$rootJobId", rootJobId);
            cmd.Add("$cursorRequested", cursorRequestedAtUtc is null ? null : SqliteStorage.Utc(cursorRequestedAtUtc.Value));
            cmd.Add("$cursorId", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<RerunBatchSummary>();
            while (r.Read())
            {
                results.Add(new RerunBatchSummary(
                    r.GetString(0),
                    r.GetString(1),
                    SqliteStorage.ReadUtc(r, "root_start_utc"),
                    SqliteStorage.ReadUtc(r, "root_end_utc"),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetString(5),
                    r.GetString(6),
                    r.GetInt32(7) == 1,
                    SqliteStorage.ReadUtc(r, "requested_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "completed_at_utc")));
            }

            return results;
        }

        // Lightweight repair-batch listing (summary columns only), newest request first. Full per-slice
        // detail stays behind SqliteRepairService.GetRepairSlices.
        public IReadOnlyList<RepairBatchSummary> ListRepairBatches(string? jobId, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT repair_batch_id, job_id, requested_by, reason, status, requested_at_utc, completed_at_utc
                FROM repair_batches
                WHERE ($jobId IS NULL OR job_id = $jobId)
                ORDER BY requested_at_utc DESC, repair_batch_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<RepairBatchSummary>();
            while (r.Read())
            {
                results.Add(new RepairBatchSummary(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    SqliteStorage.ReadUtc(r, "requested_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "completed_at_utc")));
            }

            return results;
        }

        public IReadOnlyList<RepairBatchSummary> ListRepairBatchesPage(
            string? jobId,
            DateTimeOffset? cursorRequestedAtUtc,
            string? cursorId,
            int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT repair_batch_id, job_id, requested_by, reason, status, requested_at_utc, completed_at_utc
                FROM repair_batches
                WHERE ($jobId IS NULL OR job_id = $jobId)
                  AND (
                      $cursorRequested IS NULL
                      OR requested_at_utc < $cursorRequested
                      OR (requested_at_utc = $cursorRequested AND repair_batch_id < $cursorId)
                  )
                ORDER BY requested_at_utc DESC, repair_batch_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$cursorRequested", cursorRequestedAtUtc is null ? null : SqliteStorage.Utc(cursorRequestedAtUtc.Value));
            cmd.Add("$cursorId", cursorId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<RepairBatchSummary>();
            while (r.Read())
            {
                results.Add(new RepairBatchSummary(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    SqliteStorage.ReadUtc(r, "requested_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "completed_at_utc")));
            }

            return results;
        }

        public RepairBatchSummary? GetRepairBatch(string repairBatchId)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT repair_batch_id, job_id, requested_by, reason, status, requested_at_utc, completed_at_utc
                FROM repair_batches
                WHERE repair_batch_id=$id
                LIMIT 1;
                """);
            cmd.Add("$id", repairBatchId);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new RepairBatchSummary(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    SqliteStorage.ReadUtc(r, "requested_at_utc"),
                    SqliteStorage.ReadNullableUtc(r, "completed_at_utc"))
                : null;
        }

        // Persisted failure-summary runs (the AI triage history), newest first. Read-only listing of
        // summary columns; this never invokes the summarizer.
        public IReadOnlyList<FailureSummaryReadout> ListFailureSummaries(string? jobId, int take)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT run_id, job_id, summary_kind, failure_code, failure_count, first_seen_utc, last_seen_utc, created_at_utc
                FROM failure_summary_runs
                WHERE ($jobId IS NULL OR job_id = $jobId)
                ORDER BY created_at_utc DESC, run_id DESC
                LIMIT $take;
                """);
            cmd.Add("$jobId", jobId);
            cmd.Add("$take", take);
            using var r = cmd.ExecuteReader();
            var results = new List<FailureSummaryReadout>();
            while (r.Read())
            {
                results.Add(new FailureSummaryReadout(
                    r.GetString(0),
                    r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.GetInt32(4),
                    SqliteStorage.ReadNullableUtc(r, "first_seen_utc"),
                    SqliteStorage.ReadNullableUtc(r, "last_seen_utc"),
                    SqliteStorage.ReadUtc(r, "created_at_utc")));
            }

            return results;
        }
    }
}
