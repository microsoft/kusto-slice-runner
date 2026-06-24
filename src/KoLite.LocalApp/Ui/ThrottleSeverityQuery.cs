using System.Globalization;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;

namespace KoLite.LocalApp.Ui
{
    // One time bucket of the throttle-severity chart: how many slice attempts completed in the bucket
    // and how many of those were ingestion-throttle hits. Percent is null for an empty bucket so the
    // line shows a gap rather than a misleading 0%.
    public sealed record ThrottleSeverityPoint(DateTimeOffset BucketStartUtc, int ThrottledAttempts, int TotalAttempts)
    {
        public double? Percent => TotalAttempts <= 0
            ? null
            : Math.Round((double)ThrottledAttempts * 100d / TotalAttempts, 1);

        public string PercentText => Percent is { } p
            ? p.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "n/a";

        public string BucketLabel => BucketStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    // The "how bad is it" view: a bucketed time series of the throttled-attempt rate plus a headline
    // figure over the most recent window ("in the last X minutes, Y% of attempts were throttled").
    public sealed record ThrottleSeverityChart(
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        TimeSpan BucketSize,
        IReadOnlyList<ThrottleSeverityPoint> Points,
        TimeSpan HeadlineWindow,
        int HeadlineThrottledAttempts,
        int HeadlineTotalAttempts)
    {
        public bool HasData => Points.Any(p => p.TotalAttempts > 0);

        public bool HasThrottling => HeadlineThrottledAttempts > 0 || Points.Any(p => p.ThrottledAttempts > 0);

        public double? HeadlinePercent => HeadlineTotalAttempts <= 0
            ? null
            : Math.Round((double)HeadlineThrottledAttempts * 100d / HeadlineTotalAttempts, 1);

        public string HeadlinePercentText => HeadlinePercent is { } p
            ? p.ToString("0.0", CultureInfo.InvariantCulture) + "%"
            : "n/a";
    }

    // Reads the throttled-attempt rate over time from the local store. The numerator is ingestion
    // throttle observations; the denominator is all completed slice attempts. Bucketing is done in
    // memory (matching the other chart readers, which parse timestamps in C# rather than in SQL).
    public sealed class ThrottleSeverityQuery
    {
        private static readonly TimeSpan DefaultBucketSize = TimeSpan.FromMinutes(5);

        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly IClock clock;
        private readonly BucketedTimeSeries timeSeries;

        public ThrottleSeverityQuery(IKoLiteSqliteConnectionFactory connectionFactory, IClock clock)
        {
            this.connectionFactory = connectionFactory;
            this.clock = clock;
            this.timeSeries = new BucketedTimeSeries(connectionFactory);
        }

        public ThrottleSeverityChart GetSeverity(TimeSpan range, TimeSpan headlineWindow, TimeSpan? bucketSize = null)
        {
            var now = clock.UtcNow;
            var window = timeSeries.CreateWindow(now, range, bucketSize ?? DefaultBucketSize);

            var throttled = new int[window.Count];
            var total = new int[window.Count];

            foreach (var observedAt in ReadObservationTimes(window))
            {
                var index = window.IndexOf(observedAt);
                if (index >= 0)
                {
                    throttled[index]++;
                }
            }

            foreach (var completedAt in ReadAttemptTimes(window))
            {
                var index = window.IndexOf(completedAt);
                if (index >= 0)
                {
                    total[index]++;
                }
            }

            var points = new List<ThrottleSeverityPoint>(window.Count);
            for (var i = 0; i < window.Count; i++)
            {
                points.Add(new ThrottleSeverityPoint(window.Buckets[i], throttled[i], total[i]));
            }

            var headlineSince = now - headlineWindow;
            return new ThrottleSeverityChart(
                window.Since,
                window.Until,
                window.BucketSize,
                points,
                headlineWindow,
                CountSince("ingestion_throttle_observations", "observed_at_utc", headlineSince),
                CountSince("slice_attempts", "completed_at_utc", headlineSince));
        }

        private IReadOnlyList<DateTimeOffset> ReadObservationTimes(BucketWindow window)
        {
            const string commandText = """
                SELECT observed_at_utc
                FROM ingestion_throttle_observations
                WHERE observed_at_utc >= $since AND observed_at_utc < $until;
                """;
            return timeSeries.ReadWindow(commandText, window, bindParameters: null, reader => SqliteStorage.ParseUtc(reader.GetString(0)));
        }

        private IReadOnlyList<DateTimeOffset> ReadAttemptTimes(BucketWindow window)
        {
            const string commandText = """
                SELECT completed_at_utc
                FROM slice_attempts
                WHERE completed_at_utc IS NOT NULL
                  AND completed_at_utc >= $since AND completed_at_utc < $until
                  AND status IN ('Succeeded','Failed','FailedRetryable','DeadLettered','LeaseLost');
                """;
            return timeSeries.ReadWindow(commandText, window, bindParameters: null, reader => SqliteStorage.ParseUtc(reader.GetString(0)));
        }

        // Precise count for the headline figure over the trailing headline window (independent of the
        // chart's bucket alignment). Attempts use the same charted-status filter as the buckets.
        private int CountSince(string table, string timeColumn, DateTimeOffset sinceUtc)
        {
            var statusFilter = StringComparer.Ordinal.Equals(table, "slice_attempts")
                ? " AND status IN ('Succeeded','Failed','FailedRetryable','DeadLettered','LeaseLost')"
                : string.Empty;
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, $"SELECT COUNT(*) FROM {table} WHERE {timeColumn} IS NOT NULL AND {timeColumn} >= $since{statusFilter};");
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }
}
