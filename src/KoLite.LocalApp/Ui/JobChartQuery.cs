using System.Globalization;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Connections;

namespace KoLite.LocalApp.Ui
{
    public sealed record SuccessRatePoint(DateTimeOffset BucketStartUtc, int Numerator, int Denominator)
    {
        public double Percent => Denominator <= 0 ? 0 : Math.Round((double)Numerator * 100 / Denominator, 1);
        public string PercentText => Denominator <= 0 ? "n/a" : $"{Percent:0.0}%";
        public string BucketLabel => BucketStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public sealed record SuccessRateSeries(string Name, IReadOnlyList<SuccessRatePoint> Points)
    {
        public SuccessRatePoint? LatestPoint => Points.LastOrDefault(p => p.Denominator > 0);
    }

    public sealed record SuccessRateChart(
        string Title,
        IReadOnlyList<SuccessRateSeries> Series,
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        TimeSpan BucketSize)
    {
        public bool HasData => Series.Any(s => s.Points.Any(p => p.Denominator > 0));
    }

    public sealed record DashboardCharts(SuccessRateChart FirstAttemptSuccess, SuccessRateChart SuccessAfterRetries);

    public sealed class JobChartQuery
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly IClock clock;

        public JobChartQuery(IKoLiteSqliteConnectionFactory connectionFactory, IClock clock)
        {
            this.connectionFactory = connectionFactory;
            this.clock = clock;
        }

        public DashboardCharts GetDashboardCharts(TimeSpan range)
        {
            var bucketSize = BucketSizeFor(range);
            var until = AlignUp(clock.UtcNow, bucketSize);
            var since = AlignDown(clock.UtcNow.Subtract(range), bucketSize);
            var jobIds = GetJobIds();
            var buckets = EnumerateBuckets(since, until, bucketSize);

            var attemptCounts = InitializeCounts(jobIds, buckets.Count);
            foreach (var row in ReadAttemptOutcomes(since, until))
            {
                AddOutcome(attemptCounts, buckets, since, bucketSize, row.JobId, row.CompletedAtUtc, row.Succeeded);
            }

            var finalCounts = InitializeCounts(jobIds, buckets.Count);
            foreach (var row in ReadFinalOutcomes(since, until))
            {
                AddOutcome(finalCounts, buckets, since, bucketSize, row.JobId, row.CompletedAtUtc, row.Succeeded);
            }

            return new DashboardCharts(
                BuildChart("Success Rate By Function", jobIds, buckets, attemptCounts, since, until, bucketSize),
                BuildChart("Success Rate After Retries by function", jobIds, buckets, finalCounts, since, until, bucketSize));
        }

        private IReadOnlyList<string> GetJobIds()
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_id
                FROM job_definitions
                ORDER BY job_id;
                """;
            using var reader = command.ExecuteReader();
            var results = new List<string>();
            while (reader.Read())
            {
                results.Add(reader.GetString(0));
            }

            return results;
        }

        private IReadOnlyList<AttemptOutcome> ReadAttemptOutcomes(DateTimeOffset since, DateTimeOffset until)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sa.job_id, sa.completed_at_utc, sa.status
                FROM slice_attempts sa
                INNER JOIN job_definitions jd ON jd.job_id = sa.job_id
                WHERE sa.completed_at_utc IS NOT NULL
                  AND sa.completed_at_utc >= $since
                  AND sa.completed_at_utc < $until
                  AND sa.status IN ('Succeeded','Failed','FailedRetryable','DeadLettered','LeaseLost')
                ORDER BY sa.job_id, sa.completed_at_utc, sa.attempt;
                """;
            command.Add("$since", SqliteUi.FormatUtc(since));
            command.Add("$until", SqliteUi.FormatUtc(until));
            using var reader = command.ExecuteReader();
            var results = new List<AttemptOutcome>();
            while (reader.Read())
            {
                var status = reader.GetString(2);
                results.Add(new AttemptOutcome(
                    reader.GetString(0),
                    SqliteUi.ParseUtc(reader.GetString(1)),
                    StringComparer.Ordinal.Equals(status, "Succeeded")));
            }

            return results;
        }

        private IReadOnlyList<AttemptOutcome> ReadFinalOutcomes(DateTimeOffset since, DateTimeOffset until)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT css.job_id, css.updated_at_utc, css.state
                FROM current_slice_state css
                INNER JOIN job_definitions jd ON jd.job_id = css.job_id
                WHERE css.updated_at_utc >= $since
                  AND css.updated_at_utc < $until
                  AND css.state IN ('Completed','Failed','DeadLettered')
                  AND NOT EXISTS (
                      SELECT 1
                      FROM work_queue w
                      WHERE w.job_id = css.job_id
                        AND w.slice_start_utc = css.slice_start_utc
                        AND w.slice_end_utc = css.slice_end_utc
                        AND w.state IN ('Queued','Leased')
                  )
                ORDER BY css.job_id, css.updated_at_utc;
                """;
            command.Add("$since", SqliteUi.FormatUtc(since));
            command.Add("$until", SqliteUi.FormatUtc(until));
            using var reader = command.ExecuteReader();
            var results = new List<AttemptOutcome>();
            while (reader.Read())
            {
                var status = reader.GetString(2);
                results.Add(new AttemptOutcome(
                    reader.GetString(0),
                    SqliteUi.ParseUtc(reader.GetString(1)),
                    StringComparer.Ordinal.Equals(status, "Completed")));
            }

            return results;
        }

        private static Dictionary<string, BucketCounts[]> InitializeCounts(IReadOnlyList<string> jobIds, int bucketCount) =>
            jobIds.ToDictionary(jobId => jobId, _ => Enumerable.Range(0, bucketCount).Select(_ => new BucketCounts()).ToArray(), StringComparer.Ordinal);

        private static void AddOutcome(
            Dictionary<string, BucketCounts[]> counts,
            IReadOnlyList<DateTimeOffset> buckets,
            DateTimeOffset since,
            TimeSpan bucketSize,
            string jobId,
            DateTimeOffset completedAtUtc,
            bool succeeded)
        {
            if (!counts.TryGetValue(jobId, out var jobCounts))
            {
                return;
            }

            var index = (int)((completedAtUtc.ToUniversalTime() - since).Ticks / bucketSize.Ticks);
            if (index < 0 || index >= buckets.Count)
            {
                return;
            }

            jobCounts[index].Denominator++;
            if (succeeded)
            {
                jobCounts[index].Numerator++;
            }
        }

        private static SuccessRateChart BuildChart(
            string title,
            IReadOnlyList<string> jobIds,
            IReadOnlyList<DateTimeOffset> buckets,
            IReadOnlyDictionary<string, BucketCounts[]> counts,
            DateTimeOffset since,
            DateTimeOffset until,
            TimeSpan bucketSize)
        {
            var series = jobIds
                .Select(jobId => new SuccessRateSeries(
                    jobId,
                    buckets.Select((bucket, index) => new SuccessRatePoint(bucket, counts[jobId][index].Numerator, counts[jobId][index].Denominator)).ToArray()))
                .ToArray();
            return new SuccessRateChart(title, series, since, until, bucketSize);
        }

        private static IReadOnlyList<DateTimeOffset> EnumerateBuckets(DateTimeOffset since, DateTimeOffset until, TimeSpan bucketSize)
        {
            var buckets = new List<DateTimeOffset>();
            for (var bucket = since; bucket < until; bucket = bucket.Add(bucketSize))
            {
                buckets.Add(bucket);
            }

            return buckets;
        }

        private static TimeSpan BucketSizeFor(TimeSpan range)
        {
            if (range <= TimeSpan.FromHours(1)) return TimeSpan.FromMinutes(1);
            if (range <= TimeSpan.FromDays(1)) return TimeSpan.FromHours(1);
            if (range <= TimeSpan.FromDays(7)) return TimeSpan.FromHours(6);
            return TimeSpan.FromDays(1);
        }

        private static DateTimeOffset AlignDown(DateTimeOffset value, TimeSpan bucketSize)
        {
            var utc = value.ToUniversalTime();
            return new DateTimeOffset(utc.Ticks - utc.Ticks % bucketSize.Ticks, TimeSpan.Zero);
        }

        private static DateTimeOffset AlignUp(DateTimeOffset value, TimeSpan bucketSize)
        {
            var down = AlignDown(value, bucketSize);
            return down == value.ToUniversalTime() ? down : down.Add(bucketSize);
        }

        private sealed record AttemptOutcome(string JobId, DateTimeOffset CompletedAtUtc, bool Succeeded);

        private sealed class BucketCounts
        {
            public int Numerator { get; set; }
            public int Denominator { get; set; }
        }
    }
}
