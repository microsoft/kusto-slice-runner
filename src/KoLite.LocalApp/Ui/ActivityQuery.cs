using System.Globalization;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp.Ui
{
    // A pair of "slices processed" counts split by terminal outcome. Succeeded is a completed slice;
    // Failed groups the terminal-failure states (Failed + DeadLettered). Transient retry attempts
    // (FailedRetryable, LeaseLost) are not counted here - they are not a final outcome.
    public sealed record ProcessedTotals(int Succeeded, int Failed)
    {
        public static readonly ProcessedTotals Empty = new(0, 0);

        public int Total => Succeeded + Failed;
    }

    // One time bucket of the throughput chart: how many slice attempts completed succeeded vs.
    // failed/dead-lettered in the bucket.
    public sealed record SlicesProcessedPoint(DateTimeOffset BucketStartUtc, int SucceededCount, int FailedCount)
    {
        public int TotalCount => SucceededCount + FailedCount;

        public string BucketLabel => BucketStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public sealed record SlicesProcessedChart(
        IReadOnlyList<SlicesProcessedPoint> Points,
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        TimeSpan BucketSize)
    {
        public bool HasData => Points.Any(p => p.TotalCount > 0);
    }

    // Point-in-time snapshot of in-flight work, sourced from current_slice_state (never pruned by
    // retention). RunningSlices is a capped detail list for the "which ones?" table.
    public sealed record RunningNowSummary(
        int RunningCount,
        int QueuedCount,
        IReadOnlyList<RunningSliceReadout> RunningSlices);

    public sealed record ActivityPageData(
        RunningNowSummary RunningNow,
        ProcessedTotals AllTime,
        ProcessedTotals LastDay,
        ProcessedTotals Last7Days,
        ProcessedTotals Last30Days,
        SlicesProcessedChart Chart,
        TimeSpan SelectedRange);

    // Backs the Activity page. Two stores are read, chosen for correctness under retention:
    //  - current_slice_state (never pruned) -> running/queued now and the all-time totals by current
    //    outcome (Completed = succeeded; Failed + DeadLettered = failed). This is the retention-proof
    //    "history of the app" snapshot.
    //  - slice_attempts (retention-protected for >= 30 days) -> the 1d/7d/30d totals and the
    //    over-time chart, counted by attempt completion time (matching the existing throughput and
    //    throttle-severity charts).
    public sealed class ActivityQuery
    {
        // Cap on the running-now detail list; the headline count is exact regardless.
        private const int RunningSlicesTake = 100;

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
            var allTime = new ProcessedTotals(
                summaries.Sum(s => s.CompletedCount),
                summaries.Sum(s => s.FailedCount + s.DeadLetteredCount));

            var runningSlices = diagnostics.GetRunningSlices(jobId: null, now, RunningSlicesTake);
            var runningNow = new RunningNowSummary(runningCount, queuedCount, runningSlices);

            var (lastDay, last7Days, last30Days) = ReadWindowedTotals(now);
            var chart = BuildChart(now, chartRange);

            return new ActivityPageData(runningNow, allTime, lastDay, last7Days, last30Days, chart, chartRange);
        }

        // Succeeded vs. failed/dead-lettered attempt completions for the trailing 1d/7d/30d windows in
        // a single pass. The outer WHERE bounds the scan to the widest (30d) window.
        private (ProcessedTotals LastDay, ProcessedTotals Last7Days, ProcessedTotals Last30Days) ReadWindowedTotals(DateTimeOffset now)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT
                  SUM(CASE WHEN status='Succeeded' AND completed_at_utc >= $d1 THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status IN ('Failed','DeadLettered') AND completed_at_utc >= $d1 THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status='Succeeded' AND completed_at_utc >= $d7 THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status IN ('Failed','DeadLettered') AND completed_at_utc >= $d7 THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status='Succeeded' THEN 1 ELSE 0 END),
                  SUM(CASE WHEN status IN ('Failed','DeadLettered') THEN 1 ELSE 0 END)
                FROM slice_attempts
                WHERE completed_at_utc IS NOT NULL
                  AND completed_at_utc >= $d30
                  AND status IN ('Succeeded','Failed','DeadLettered');
                """);
            cmd.Add("$d1", SqliteStorage.Utc(now - TimeSpan.FromDays(1)));
            cmd.Add("$d7", SqliteStorage.Utc(now - TimeSpan.FromDays(7)));
            cmd.Add("$d30", SqliteStorage.Utc(now - TimeSpan.FromDays(30)));
            using var r = cmd.ExecuteReader();
            r.Read();
            int Count(int ordinal) => r.IsDBNull(ordinal) ? 0 : r.GetInt32(ordinal);
            return (
                new ProcessedTotals(Count(0), Count(1)),
                new ProcessedTotals(Count(2), Count(3)),
                new ProcessedTotals(Count(4), Count(5)));
        }

        // Bucketed throughput over [now - range, now). Aggregation is done in SQL (GROUP BY epoch
        // bucket) and slotted onto the pre-aligned bucket window so empty buckets render as gaps.
        private SlicesProcessedChart BuildChart(DateTimeOffset now, TimeSpan range)
        {
            var window = timeSeries.CreateWindow(now, range);
            var succeeded = new int[window.Count];
            var failed = new int[window.Count];

            const string commandText = """
                SELECT (CAST(strftime('%s', completed_at_utc) AS INTEGER) / $bucket) * $bucket AS bucket_epoch,
                       SUM(CASE WHEN status='Succeeded' THEN 1 ELSE 0 END) AS succeeded_count,
                       SUM(CASE WHEN status IN ('Failed','DeadLettered') THEN 1 ELSE 0 END) AS failed_count
                FROM slice_attempts
                WHERE completed_at_utc IS NOT NULL
                  AND completed_at_utc >= $since AND completed_at_utc < $until
                  AND status IN ('Succeeded','Failed','DeadLettered')
                GROUP BY bucket_epoch;
                """;

            var rows = timeSeries.ReadWindow(
                commandText,
                window,
                bind => bind.Add("$bucket", window.BucketSeconds),
                reader => (
                    Epoch: reader.GetInt64(0),
                    Succeeded: reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    Failed: reader.IsDBNull(2) ? 0 : reader.GetInt32(2)));

            foreach (var row in rows)
            {
                var index = window.IndexOf(DateTimeOffset.FromUnixTimeSeconds(row.Epoch));
                if (index >= 0)
                {
                    succeeded[index] += row.Succeeded;
                    failed[index] += row.Failed;
                }
            }

            var points = new List<SlicesProcessedPoint>(window.Count);
            for (var i = 0; i < window.Count; i++)
            {
                points.Add(new SlicesProcessedPoint(window.Buckets[i], succeeded[i], failed[i]));
            }

            return new SlicesProcessedChart(points, window.Since, window.Until, window.BucketSize);
        }
    }
}
