// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp.Ui
{
    // Processed execution-unit counts split by terminal outcome. One chunk is one execution unit;
    // an unchunked slice is also one execution unit. Retries do not add another processed item.
    public sealed record ProcessedTotals(int Succeeded, int Failed)
    {
        public static readonly ProcessedTotals Empty = new(0, 0);

        public int Total => Succeeded + Failed;
    }

    // One time bucket of the throughput chart: how many execution units reached their latest
    // succeeded vs. failed/dead-lettered outcome in the bucket.
    public sealed record ExecutionsProcessedPoint(DateTimeOffset BucketStartUtc, int SucceededCount, int FailedCount)
    {
        public int TotalCount => SucceededCount + FailedCount;

        public string BucketLabel => AppFormatting.Iso(BucketStartUtc);
    }

    public sealed record ExecutionsProcessedChart(
        IReadOnlyList<ExecutionsProcessedPoint> Points,
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        TimeSpan BucketSize)
    {
        public bool HasData => Points.Any(p => p.TotalCount > 0);
    }

    // One logical running-slice row for the Activity table. Chunked rows include child progress and
    // every active execution; StartedAtUtc is the earliest active execution start and EtaUtc uses the
    // median recent whole-window duration. EtaUtc is null when the job has no usable history.
    public sealed record RunningSliceView(
        RunningSliceReadout Slice,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? EtaUtc,
        int? TotalChunks,
        int CompletedChunks,
        int RunningChunks,
        int QueuedChunks,
        int FailedChunks,
        int DeadLetteredChunks,
        int MissingChunks,
        IReadOnlyList<RunningExecutionView> RunningExecutions);

    public sealed record RunningExecutionView(
        int? ChunkId,
        int? TotalChunks,
        int Attempt,
        string? WorkerId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? LeaseExpiresAtUtc,
        bool LeaseExpired);

    // Point-in-time snapshot of in-flight work. Logical counts come from parent slice state, execution
    // counts come from child/queue state, and RunningSlices is a capped logical-row detail list.
    public sealed record RunningNowSummary(
        int RunningCount,
        int QueuedCount,
        int RunningExecutionCount,
        int QueuedExecutionCount,
        IReadOnlyList<RunningSliceView> RunningSlices);

    public sealed record ActivityPageData(
        RunningNowSummary RunningNow,
        ProcessedTotals AllTime,
        ProcessedTotals LastDay,
        ProcessedTotals Last7Days,
        ProcessedTotals Last30Days,
        ExecutionsProcessedChart Chart,
        TimeSpan SelectedRange,
        DateTimeOffset GeneratedAtUtc);

    // Backs the Activity page. Durable state and retained attempts are read for different purposes:
    //  - current_slice_state/current_slice_chunk_state (never pruned) -> running/queued now and
    //    retention-proof all-time execution totals. Chunked parents are excluded from the totals.
    //  - slice_attempts (retention-protected for >= 30 days) -> the 1d/7d/30d totals and the
    //    over-time chart, grouped into one latest terminal outcome per execution unit.
    public sealed class ActivityQuery
    {
        internal const string ProcessedActivityCommandText = """
            WITH ranked_executions AS MATERIALIZED (
              SELECT job_id,
                     slice_start_utc,
                     slice_end_utc,
                     COALESCE(chunk_id, -1) AS execution_id,
                     status,
                     completed_at_utc,
                     ROW_NUMBER() OVER (
                       PARTITION BY job_id,
                                    slice_start_utc,
                                    slice_end_utc,
                                    COALESCE(chunk_id, -1)
                       ORDER BY completed_at_utc DESC,
                                attempt DESC,
                                attempt_id DESC
                     ) AS outcome_rank
              FROM slice_attempts
              WHERE completed_at_utc IS NOT NULL
                AND completed_at_utc >= $historySince
                AND completed_at_utc < $asOf
                AND status IN ('Succeeded','Failed','DeadLettered')
            ),
            latest_executions AS MATERIALIZED (
              SELECT status, completed_at_utc
              FROM ranked_executions
              WHERE outcome_rank = 1
            )
            SELECT
              0 AS row_kind,
              NULL AS bucket_epoch,
              SUM(CASE WHEN status='Succeeded' AND completed_at_utc >= $d1 THEN 1 ELSE 0 END) AS succeeded_count,
              SUM(CASE WHEN status IN ('Failed','DeadLettered') AND completed_at_utc >= $d1 THEN 1 ELSE 0 END) AS failed_count,
              SUM(CASE WHEN status='Succeeded' AND completed_at_utc >= $d7 THEN 1 ELSE 0 END) AS last_7_days_succeeded,
              SUM(CASE WHEN status IN ('Failed','DeadLettered') AND completed_at_utc >= $d7 THEN 1 ELSE 0 END) AS last_7_days_failed,
              SUM(CASE WHEN status='Succeeded' AND completed_at_utc >= $d30 THEN 1 ELSE 0 END) AS last_30_days_succeeded,
              SUM(CASE WHEN status IN ('Failed','DeadLettered') AND completed_at_utc >= $d30 THEN 1 ELSE 0 END) AS last_30_days_failed
            FROM latest_executions
            UNION ALL
            SELECT
              1 AS row_kind,
              (CAST(strftime('%s', completed_at_utc) AS INTEGER) / $bucket) * $bucket AS bucket_epoch,
              SUM(CASE WHEN status='Succeeded' THEN 1 ELSE 0 END) AS succeeded_count,
              SUM(CASE WHEN status IN ('Failed','DeadLettered') THEN 1 ELSE 0 END) AS failed_count,
              NULL AS last_7_days_succeeded,
              NULL AS last_7_days_failed,
              NULL AS last_30_days_succeeded,
              NULL AS last_30_days_failed
            FROM latest_executions
            WHERE completed_at_utc >= $chartSince
            GROUP BY bucket_epoch
            ORDER BY row_kind, bucket_epoch;
            """;

        // Cap on the running-now detail list; the headline count is exact regardless.
        private const int RunningSlicesTake = 100;

        // ETA for a running slice is projected from the median of that job's recent successful
        // whole-window durations, sampled over this trailing window and capped per job.
        private static readonly TimeSpan DurationHistoryLookback = TimeSpan.FromDays(30);
        private const int DurationSampleCap = 50;

        private sealed record RecentProcessedActivity(
            ProcessedTotals LastDay,
            ProcessedTotals Last7Days,
            ProcessedTotals Last30Days,
            ExecutionsProcessedChart Chart);

        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly IClock clock;
        private readonly SqliteOperationalReadModelRepository operationalReadModels;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;
        private readonly BucketedTimeSeries timeSeries;

        public ActivityQuery(
            IKoLiteSqliteConnectionFactory connectionFactory,
            IClock clock,
            SqliteOperationalReadModelRepository operationalReadModels,
            SqliteDiagnosticsReadModelRepository diagnostics)
        {
            this.connectionFactory = connectionFactory;
            this.clock = clock;
            this.operationalReadModels = operationalReadModels;
            this.diagnostics = diagnostics;
            this.timeSeries = new BucketedTimeSeries(connectionFactory);
        }

        public ActivityPageData GetActivity(TimeSpan chartRange)
        {
            var now = clock.UtcNow;

            var summaries = operationalReadModels.GetJobStatusSummaries();
            var runningCount = summaries.Sum(s => s.RunningCount);
            var queuedCount = summaries.Sum(s => s.QueuedCount);
            var allTime = ReadAllTimeTotals();

            var runningSnapshot = diagnostics.GetRunningActivitySnapshot(now, RunningSlicesTake);
            var executionCounts = diagnostics.GetActivityExecutionCounts();
            var runningViews = BuildRunningViews(runningSnapshot.Slices, runningSnapshot.ChunkExecutions, now);
            var runningNow = new RunningNowSummary(
                runningCount,
                queuedCount,
                executionCounts.Running,
                executionCounts.Queued,
                runningViews);

            var recent = ReadProcessedActivity(now, chartRange);

            return new ActivityPageData(
                runningNow,
                allTime,
                recent.LastDay,
                recent.Last7Days,
                recent.Last30Days,
                recent.Chart,
                chartRange,
                now);
        }

        // Resolves each logical running slice's active executions, progress, earliest active start,
        // and ETA from recent successful whole-window durations. Typical durations and child state
        // are fetched in grouped reads rather than per slice.
        private IReadOnlyList<RunningSliceView> BuildRunningViews(
            IReadOnlyList<RunningSliceReadout> slices,
            IReadOnlyList<RunningChunkExecutionReadout> chunks,
            DateTimeOffset now)
        {
            if (slices.Count == 0)
            {
                return Array.Empty<RunningSliceView>();
            }

            var jobIds = slices.Select(s => s.JobId).Distinct().ToList();
            var typicalDurations = diagnostics.GetTypicalCompletedSliceDurationsByJob(jobIds, now - DurationHistoryLookback, DurationSampleCap);
            var chunksBySlice = chunks
                .GroupBy(chunk => (chunk.JobId, chunk.SliceStartUtc, chunk.SliceEndUtc))
                .ToDictionary(group => group.Key, group => group.OrderBy(chunk => chunk.ChunkId).ToArray());

            var views = new List<RunningSliceView>(slices.Count);
            foreach (var slice in slices)
            {
                chunksBySlice.TryGetValue((slice.JobId, slice.SliceStartUtc, slice.SliceEndUtc), out var sliceChunks);
                sliceChunks ??= [];
                var runningChunkRows = sliceChunks
                    .Where(chunk => string.Equals(chunk.State, "Running", StringComparison.Ordinal))
                    .ToArray();
                IReadOnlyList<RunningExecutionView> runningExecutions = slice.TotalChunks is null
                    ?
                    [
                        new RunningExecutionView(
                            null,
                            null,
                            slice.Attempt,
                            slice.LeaseOwner,
                            slice.StartedAtUtc ?? slice.UpdatedAtUtc,
                            slice.LeaseExpiresAtUtc,
                            slice.LeaseExpired)
                    ]
                    : runningChunkRows.Select(chunk => new RunningExecutionView(
                        chunk.ChunkId,
                        chunk.TotalChunks,
                        chunk.Attempt,
                        chunk.WorkerId,
                        chunk.StartedAtUtc ?? chunk.UpdatedAtUtc,
                        chunk.LeaseExpiresAtUtc,
                        chunk.LeaseExpired)).ToArray();
                var startedAt = runningExecutions.Count > 0
                    ? runningExecutions.Min(execution => execution.StartedAtUtc)
                    : slice.StartedAtUtc ?? slice.UpdatedAtUtc;
                DateTimeOffset? eta = typicalDurations.TryGetValue(slice.JobId, out var median)
                    ? startedAt + median
                    : null;
                views.Add(new RunningSliceView(
                    slice,
                    startedAt,
                    eta,
                    slice.TotalChunks,
                    sliceChunks.Count(chunk => string.Equals(chunk.State, "Completed", StringComparison.Ordinal)),
                    runningChunkRows.Length,
                    sliceChunks.Count(chunk => string.Equals(chunk.State, "Queued", StringComparison.Ordinal)),
                    sliceChunks.Count(chunk => string.Equals(chunk.State, "Failed", StringComparison.Ordinal)),
                    sliceChunks.Count(chunk => string.Equals(chunk.State, "DeadLettered", StringComparison.Ordinal)),
                    sliceChunks.Count(chunk => string.Equals(chunk.State, "Missing", StringComparison.Ordinal)),
                    runningExecutions));
            }

            return views;
        }

        // Latest terminal execution outcomes across the never-pruned state tables. Current chunk
        // rows define chunked execution identity; parent rows are counted only when they have no
        // children. A queued/running retry or repair falls back to its prior terminal state event.
        private ProcessedTotals ReadAllTimeTotals()
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                WITH current_executions AS (
                  SELECT CASE
                           WHEN child.state IN ('Completed','Failed','DeadLettered') THEN child.state
                           ELSE (
                             SELECT history.state
                             FROM slice_chunk_state_events history
                             WHERE history.job_id = child.job_id
                               AND history.slice_start_utc = child.slice_start_utc
                               AND history.slice_end_utc = child.slice_end_utc
                               AND history.chunk_id = child.chunk_id
                               AND history.state IN ('Completed','Failed','DeadLettered')
                             ORDER BY history.recorded_at_utc DESC,
                                      history.attempt DESC,
                                      history.rowid DESC
                             LIMIT 1
                           )
                         END AS terminal_state
                  FROM current_slice_chunk_state child
                  UNION ALL
                  SELECT CASE
                           WHEN parent.state IN ('Completed','Failed','DeadLettered') THEN parent.state
                           ELSE (
                             SELECT history.state
                             FROM slice_state_events history
                             WHERE history.job_id = parent.job_id
                               AND history.slice_start_utc = parent.slice_start_utc
                               AND history.slice_end_utc = parent.slice_end_utc
                               AND history.state IN ('Completed','Failed','DeadLettered')
                             ORDER BY history.recorded_at_utc DESC,
                                      history.attempt DESC,
                                      history.rowid DESC
                             LIMIT 1
                           )
                         END AS terminal_state
                  FROM current_slice_state parent
                  WHERE NOT EXISTS (
                    SELECT 1
                    FROM current_slice_chunk_state child
                    WHERE child.job_id = parent.job_id
                      AND child.slice_start_utc = parent.slice_start_utc
                      AND child.slice_end_utc = parent.slice_end_utc
                  )
                )
                SELECT
                  SUM(CASE WHEN terminal_state='Completed' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN terminal_state IN ('Failed','DeadLettered') THEN 1 ELSE 0 END)
                FROM current_executions;
                """);
            using var r = cmd.ExecuteReader();
            r.Read();
            int Count(int ordinal) => r.IsDBNull(ordinal) ? 0 : r.GetInt32(ordinal);
            return new ProcessedTotals(Count(0), Count(1));
        }

        // Reads recent totals and the selected chart in one bounded ranking pass. Once an execution
        // has a terminal outcome inside the reporting window, older terminal outcomes cannot be its
        // latest outcome, so there is no need to join every candidate back to its full history.
        private RecentProcessedActivity ReadProcessedActivity(DateTimeOffset now, TimeSpan chartRange)
        {
            var window = timeSeries.CreateWindow(now, chartRange);
            var last30DaysStart = now - TimeSpan.FromDays(30);
            var historyStart = window.Since < last30DaysStart ? window.Since : last30DaysStart;
            var succeeded = new int[window.Count];
            var failed = new int[window.Count];
            var lastDay = ProcessedTotals.Empty;
            var last7Days = ProcessedTotals.Empty;
            var last30Days = ProcessedTotals.Empty;

            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, ProcessedActivityCommandText);
            cmd.Add("$historySince", SqliteStorage.Utc(historyStart));
            cmd.Add("$asOf", SqliteStorage.Utc(now));
            cmd.Add("$d1", SqliteStorage.Utc(now - TimeSpan.FromDays(1)));
            cmd.Add("$d7", SqliteStorage.Utc(now - TimeSpan.FromDays(7)));
            cmd.Add("$d30", SqliteStorage.Utc(last30DaysStart));
            cmd.Add("$chartSince", SqliteStorage.Utc(window.Since));
            cmd.Add("$bucket", window.BucketSeconds);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int Count(int ordinal) => reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
                if (reader.GetInt32(0) == 0)
                {
                    lastDay = new ProcessedTotals(Count(2), Count(3));
                    last7Days = new ProcessedTotals(Count(4), Count(5));
                    last30Days = new ProcessedTotals(Count(6), Count(7));
                    continue;
                }

                var bucketStart = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1));
                var index = window.IndexOf(bucketStart);
                if (index >= 0)
                {
                    succeeded[index] += Count(2);
                    failed[index] += Count(3);
                }
            }

            var points = new List<ExecutionsProcessedPoint>(window.Count);
            for (var i = 0; i < window.Count; i++)
            {
                points.Add(new ExecutionsProcessedPoint(window.Buckets[i], succeeded[i], failed[i]));
            }

            return new RecentProcessedActivity(
                lastDay,
                last7Days,
                last30Days,
                new ExecutionsProcessedChart(points, window.Since, window.Until, window.BucketSize));
        }
    }
}
