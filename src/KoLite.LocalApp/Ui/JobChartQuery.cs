using System.Globalization;
using System.Text.Json;
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

    public enum JobAttemptResultBucket
    {
        Ignore,
        Success,
        Retry,
        Error
    }

    public static class JobAttemptStatusTaxonomy
    {
        public static IReadOnlyList<string> ChartedStatuses { get; } =
        [
            "Succeeded",
            "FailedRetryable",
            "Failed",
            "DeadLettered",
            "LeaseLost"
        ];

        public static JobAttemptResultBucket BucketFor(string status) => status switch
        {
            "Succeeded" => JobAttemptResultBucket.Success,
            "FailedRetryable" => JobAttemptResultBucket.Retry,
            "Failed" or "DeadLettered" or "LeaseLost" => JobAttemptResultBucket.Error,
            _ => JobAttemptResultBucket.Ignore
        };
    }

    public sealed record JobAttemptResultPoint(DateTimeOffset BucketStartUtc, int SuccessCount, int RetryCount, int ErrorCount)
    {
        public int TotalCount => SuccessCount + RetryCount + ErrorCount;
        public string BucketLabel => BucketStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public sealed record JobAttemptResultChart(
        string Title,
        IReadOnlyList<JobAttemptResultPoint> Points,
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        TimeSpan BucketSize)
    {
        public bool HasData => Points.Any(p => p.TotalCount > 0);
    }

    public sealed record JobSuccessfulDurationPoint(DateTimeOffset BucketStartUtc, int Count, int MissingDurationCount, double? AverageDurationMilliseconds)
    {
        public string BucketLabel => BucketStartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        public string AverageDurationText => AverageDurationMilliseconds is { } duration
            ? TimeSpan.FromMilliseconds(duration).ToString("c", CultureInfo.InvariantCulture)
            : "n/a";
    }

    public sealed record JobSuccessfulDurationChart(
        string Title,
        IReadOnlyList<JobSuccessfulDurationPoint> Points,
        DateTimeOffset RangeStartUtc,
        DateTimeOffset RangeEndUtc,
        TimeSpan BucketSize,
        int MetricsDurationCount,
        int AttemptDurationFallbackCount,
        int RecoveredDurationCount,
        int MissingDurationCount)
    {
        public bool HasData => Points.Any(p => p.Count > 0);
        public int SampleCount => MetricsDurationCount + AttemptDurationFallbackCount + RecoveredDurationCount;
        public int TotalSuccessfulCount => SampleCount + MissingDurationCount;
    }

    public sealed record JobDetailsCharts(
        string JobId,
        TimeSpan SelectedRange,
        JobAttemptResultChart AttemptResults,
        JobSuccessfulDurationChart SuccessfulDurations);

    public sealed class JobChartQuery
    {
        private static readonly string[] DurationMillisecondsMetricKeys =
        [
            "queryDurationMs",
            "queryDurationMilliseconds",
            "kustoDurationMs",
            "kustoDurationMilliseconds",
            "executionDurationMs",
            "executionDurationMilliseconds",
            "durationMs",
            "durationMilliseconds"
        ];

        private static readonly string[] DurationTimeSpanMetricKeys =
        [
            "queryDuration",
            "kustoDuration",
            "executionDuration",
            "duration"
        ];

        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly IClock clock;

        public JobChartQuery(IKoLiteSqliteConnectionFactory connectionFactory, IClock clock)
        {
            this.connectionFactory = connectionFactory;
            this.clock = clock;
        }

        public DashboardCharts GetDashboardCharts(TimeSpan range, IEnumerable<string>? jobIds = null)
        {
            var bucketSize = BucketSizeFor(range);
            var until = AlignUp(clock.UtcNow, bucketSize);
            var since = AlignDown(clock.UtcNow.Subtract(range), bucketSize);
            var includedJobIds = jobIds?.ToHashSet(StringComparer.Ordinal);
            var chartJobIds = GetJobIds(includedJobIds);
            var buckets = EnumerateBuckets(since, until, bucketSize);

            var attemptCounts = InitializeCounts(chartJobIds, buckets.Count);
            foreach (var row in ReadAttemptOutcomes(since, until))
            {
                AddOutcome(attemptCounts, buckets, since, bucketSize, row.JobId, row.CompletedAtUtc, row.Succeeded);
            }

            var finalCounts = InitializeCounts(chartJobIds, buckets.Count);
            foreach (var row in ReadFinalOutcomes(since, until))
            {
                AddOutcome(finalCounts, buckets, since, bucketSize, row.JobId, row.CompletedAtUtc, row.Succeeded);
            }

            return new DashboardCharts(
                BuildChart("Success Rate By Function", chartJobIds, buckets, attemptCounts, since, until, bucketSize),
                BuildChart("Success Rate After Retries by function", chartJobIds, buckets, finalCounts, since, until, bucketSize));
        }

        public JobDetailsCharts GetJobDetailsCharts(string jobId, TimeSpan range)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            var bucketSize = BucketSizeFor(range);
            var until = AlignUp(clock.UtcNow, bucketSize);
            var since = AlignDown(clock.UtcNow.Subtract(range), bucketSize);
            var buckets = EnumerateBuckets(since, until, bucketSize);

            return new JobDetailsCharts(
                jobId,
                range,
                BuildJobAttemptResultChart(jobId, buckets, since, until, bucketSize),
                BuildJobSuccessfulDurationChart(jobId, buckets, since, until, bucketSize));
        }

        private IReadOnlyList<string> GetJobIds(IReadOnlySet<string>? includedJobIds = null)
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
                var jobId = reader.GetString(0);
                if (includedJobIds is null || includedJobIds.Contains(jobId))
                {
                    results.Add(jobId);
                }
            }

            return results;
        }

        private JobAttemptResultChart BuildJobAttemptResultChart(string jobId, IReadOnlyList<DateTimeOffset> buckets, DateTimeOffset since, DateTimeOffset until, TimeSpan bucketSize)
        {
            var counts = Enumerable.Range(0, buckets.Count).Select(_ => new JobAttemptResultCounts()).ToArray();
            foreach (var row in ReadJobAttemptResultCounts(jobId, since, until, bucketSize))
            {
                if (row.BucketIndex < 0 || row.BucketIndex >= counts.Length)
                {
                    continue;
                }

                switch (JobAttemptStatusTaxonomy.BucketFor(row.Status))
                {
                    case JobAttemptResultBucket.Success:
                        counts[row.BucketIndex].SuccessCount += row.Count;
                        break;
                    case JobAttemptResultBucket.Retry:
                        counts[row.BucketIndex].RetryCount += row.Count;
                        break;
                    case JobAttemptResultBucket.Error:
                        counts[row.BucketIndex].ErrorCount += row.Count;
                        break;
                }
            }

            return new JobAttemptResultChart(
                "Query Results by Time of Execution",
                buckets.Select((bucket, index) => new JobAttemptResultPoint(bucket, counts[index].SuccessCount, counts[index].RetryCount, counts[index].ErrorCount)).ToArray(),
                since,
                until,
                bucketSize);
        }

        private JobSuccessfulDurationChart BuildJobSuccessfulDurationChart(string jobId, IReadOnlyList<DateTimeOffset> buckets, DateTimeOffset since, DateTimeOffset until, TimeSpan bucketSize)
        {
            var durations = Enumerable.Range(0, buckets.Count).Select(_ => new DurationBucket()).ToArray();
            var metricsCount = 0;
            var fallbackCount = 0;
            var recoveredCount = 0;
            var missingCount = 0;
            foreach (var sample in ReadSuccessfulDurationSamples(jobId, since, until))
            {
                var index = (int)((sample.CompletedAtUtc.ToUniversalTime() - since).Ticks / bucketSize.Ticks);
                if (index < 0 || index >= durations.Length)
                {
                    continue;
                }

                var duration = TryReadDurationFromMetricsJson(sample.MetricsJson);
                var usedMetric = duration is not null;
                if (duration is null && sample.StartedAtUtc is { } startedAtUtc)
                {
                    duration = sample.CompletedAtUtc - startedAtUtc;
                }

                if (duration is null || duration.Value < TimeSpan.Zero)
                {
                    durations[index].MissingDurationCount++;
                    missingCount++;
                    continue;
                }

                durations[index].Count++;
                durations[index].TotalMilliseconds += duration.Value.TotalMilliseconds;
                if (usedMetric)
                {
                    metricsCount++;
                }
                else if (sample.RecoveredStartedAtUtc)
                {
                    recoveredCount++;
                }
                else
                {
                    fallbackCount++;
                }
            }

            return new JobSuccessfulDurationChart(
                "Successful Query Duration by Time of Execution",
                buckets.Select((bucket, index) => new JobSuccessfulDurationPoint(
                    bucket,
                    durations[index].Count,
                    durations[index].MissingDurationCount,
                    durations[index].Count == 0 ? null : durations[index].TotalMilliseconds / durations[index].Count)).ToArray(),
                since,
                until,
                bucketSize,
                metricsCount,
                fallbackCount,
                recoveredCount,
                missingCount);
        }

        private IReadOnlyList<JobAttemptResultCount> ReadJobAttemptResultCounts(string jobId, DateTimeOffset since, DateTimeOffset until, TimeSpan bucketSize)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            var statusParameters = string.Join(", ", JobAttemptStatusTaxonomy.ChartedStatuses.Select((_, index) => "$status" + index.ToString(CultureInfo.InvariantCulture)));
            command.CommandText = $"""
                SELECT CAST((unixepoch(sa.completed_at_utc) - unixepoch($since)) / $bucket_seconds AS INTEGER) bucket_index,
                       sa.status,
                       COUNT(*) attempt_count
                FROM slice_attempts sa
                WHERE sa.job_id = $job
                  AND sa.completed_at_utc IS NOT NULL
                  AND sa.completed_at_utc >= $since
                  AND sa.completed_at_utc < $until
                  AND sa.status IN ({statusParameters})
                GROUP BY bucket_index, sa.status
                ORDER BY bucket_index, sa.status;
                """;
            command.Add("$job", jobId);
            command.Add("$since", SqliteUi.FormatUtc(since));
            command.Add("$until", SqliteUi.FormatUtc(until));
            command.Add("$bucket_seconds", Math.Max(1L, (long)bucketSize.TotalSeconds));
            for (var i = 0; i < JobAttemptStatusTaxonomy.ChartedStatuses.Count; i++)
            {
                command.Add("$status" + i.ToString(CultureInfo.InvariantCulture), JobAttemptStatusTaxonomy.ChartedStatuses[i]);
            }

            using var reader = command.ExecuteReader();
            var results = new List<JobAttemptResultCount>();
            while (reader.Read())
            {
                results.Add(new JobAttemptResultCount(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    Convert.ToInt32(reader.GetInt64(2), CultureInfo.InvariantCulture)));
            }

            return results;
        }

        private IReadOnlyList<SuccessfulDurationSample> ReadSuccessfulDurationSamples(string jobId, DateTimeOffset since, DateTimeOffset until)
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sa.completed_at_utc,
                       sa.started_at_utc,
                       (
                           SELECT MAX(running.recorded_at_utc)
                           FROM slice_state_events running
                           WHERE running.job_id = sa.job_id
                             AND running.slice_start_utc = sa.slice_start_utc
                             AND running.slice_end_utc = sa.slice_end_utc
                             AND running.attempt = sa.attempt
                             AND running.state = 'Running'
                             AND running.recorded_at_utc <= sa.completed_at_utc
                       ) recovered_started_at_utc,
                       sa.metrics_json
                FROM slice_attempts sa
                WHERE sa.job_id = $job
                  AND sa.status = 'Succeeded'
                  AND sa.completed_at_utc IS NOT NULL
                  AND sa.completed_at_utc >= $since
                  AND sa.completed_at_utc < $until
                ORDER BY sa.completed_at_utc;
                """;
            command.Add("$job", jobId);
            command.Add("$since", SqliteUi.FormatUtc(since));
            command.Add("$until", SqliteUi.FormatUtc(until));
            using var reader = command.ExecuteReader();
            var results = new List<SuccessfulDurationSample>();
            while (reader.Read())
            {
                DateTimeOffset? startedAtUtc = null;
                if (!reader.IsDBNull(1))
                {
                    startedAtUtc = SqliteUi.ParseUtc(reader.GetString(1));
                }
                else if (!reader.IsDBNull(2))
                {
                    startedAtUtc = SqliteUi.ParseUtc(reader.GetString(2));
                }

                results.Add(new SuccessfulDurationSample(
                    SqliteUi.ParseUtc(reader.GetString(0)),
                    startedAtUtc,
                    reader.IsDBNull(1) && !reader.IsDBNull(2),
                    reader.GetString(3)));
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

        private static TimeSpan? TryReadDurationFromMetricsJson(string metricsJson)
        {
            if (string.IsNullOrWhiteSpace(metricsJson) || metricsJson.Trim() == "{}")
            {
                return null;
            }

            using var document = JsonDocument.Parse(metricsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in DurationMillisecondsMetricKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var value) && TryReadMilliseconds(value, out var milliseconds))
                {
                    return TimeSpan.FromMilliseconds(milliseconds);
                }
            }

            foreach (var key in DurationTimeSpanMetricKeys)
            {
                if (document.RootElement.TryGetProperty(key, out var value) && TryReadTimeSpan(value, out var duration))
                {
                    return duration;
                }
            }

            return null;
        }

        private static bool TryReadMilliseconds(JsonElement value, out double milliseconds)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out milliseconds))
            {
                return milliseconds >= 0;
            }

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out milliseconds))
            {
                return milliseconds >= 0;
            }

            milliseconds = 0;
            return false;
        }

        private static bool TryReadTimeSpan(JsonElement value, out TimeSpan duration)
        {
            if (value.ValueKind == JsonValueKind.String &&
                TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out duration) &&
                duration >= TimeSpan.Zero)
            {
                return true;
            }

            duration = TimeSpan.Zero;
            return false;
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

        private sealed record JobAttemptResultCount(int BucketIndex, string Status, int Count);

        private sealed record SuccessfulDurationSample(DateTimeOffset CompletedAtUtc, DateTimeOffset? StartedAtUtc, bool RecoveredStartedAtUtc, string MetricsJson);

        private sealed class BucketCounts
        {
            public int Numerator { get; set; }
            public int Denominator { get; set; }
        }

        private sealed class JobAttemptResultCounts
        {
            public int SuccessCount { get; set; }
            public int RetryCount { get; set; }
            public int ErrorCount { get; set; }
        }

        private sealed class DurationBucket
        {
            public int Count { get; set; }
            public int MissingDurationCount { get; set; }
            public double TotalMilliseconds { get; set; }
        }
    }
}
