using KoLite.Local.Core.Throttling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;

namespace KoLite.Local.Sqlite.Throttling
{
    // Read model behind the advisory throttle panel. Composes the throttle store (sustained-trigger
    // evidence), the catalog (job definitions), live in-flight queue state, and recent successful
    // slice durations into ranked, per-cluster parallelism recommendations. Read-only: it never
    // mutates a job; applying a recommendation is a separate, explicit operator action.
    public sealed class SqliteThrottleAdvisorReadModel
    {
        private const string DefaultQueueName = "default";
        private const int DurationSampleCap = 1000;

        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteIngestionThrottleRepository throttleStore;
        private readonly IClock clock;
        private readonly ThrottleAdvisorOptions options;

        public SqliteThrottleAdvisorReadModel(
            IKoLiteSqliteConnectionFactory connectionFactory,
            SqliteJobCatalogRepository catalog,
            SqliteIngestionThrottleRepository throttleStore,
            IClock clock,
            ThrottleAdvisorOptions options)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.throttleStore = throttleStore ?? throw new ArgumentNullException(nameof(throttleStore));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        // One advisory per cluster currently under sustained ingestion throttling. Empty when the
        // feature is disabled or no cluster crosses the sustained-throttle threshold.
        public IReadOnlyList<ClusterThrottleAdvisory> BuildAdvisories()
        {
            if (!options.Enabled)
            {
                return Array.Empty<ClusterThrottleAdvisory>();
            }

            var now = clock.UtcNow;
            var windowStart = now - options.Window;
            var sustained = throttleStore.SummarizeWindow(windowStart)
                .Where(summary => summary.ThrottledSliceCount >= options.MinThrottledSlices)
                .ToList();
            if (sustained.Count == 0)
            {
                return Array.Empty<ClusterThrottleAdvisory>();
            }

            var enabledJobs = catalog.List(enabledOnly: true);
            var inFlight = CountInFlightByJob(now);
            var durationSince = now - options.DurationLookback;
            var recommendationOptions = options.ToRecommendationOptions();

            var advisories = new List<ClusterThrottleAdvisory>();
            foreach (var cluster in sustained)
            {
                var throttledJobIds = throttleStore.ListThrottledJobIds(cluster.ClusterUri, windowStart);
                var snapshots = enabledJobs
                    .Where(job => StringComparer.Ordinal.Equals(job.Definition.Target.ClusterUri, cluster.ClusterUri))
                    .Where(job => inFlight.GetValueOrDefault(job.JobId) > 0 || throttledJobIds.Contains(job.JobId))
                    .Select(job => ToSnapshot(job, cluster.ClusterUri, inFlight.GetValueOrDefault(job.JobId), durationSince))
                    .ToList();

                var recommendations = ParallelismRecommendationEngine.Recommend(snapshots, recommendationOptions);
                advisories.Add(new ClusterThrottleAdvisory(
                    cluster.ClusterUri,
                    cluster.ThrottledSliceCount,
                    cluster.ObservationCount,
                    cluster.LatestReportedCapacity,
                    cluster.FirstObservedUtc,
                    cluster.LatestObservedUtc,
                    options.Window,
                    recommendations));
            }

            return advisories;
        }

        private JobThrottleSnapshot ToSnapshot(JobCatalogRecord job, string clusterUri, int inFlightCount, DateTimeOffset durationSinceUtc)
        {
            var definition = job.Definition;
            var (duration, sampleCount) = EstimateSliceDuration(job.JobId, durationSinceUtc);
            return new JobThrottleSnapshot(
                job.JobId,
                job.ActivityId,
                clusterUri,
                definition.MaxParallelism,
                definition.QueryWindowSize,
                inFlightCount,
                duration,
                sampleCount);
        }

        // Counts unexpired leased work per job on the default queue: a job's live in-flight slices.
        private Dictionary<string, int> CountInFlightByJob(DateTimeOffset nowUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT job_id, COUNT(*) AS inflight FROM work_queue WHERE queue_name = $q AND state = 'Leased' AND locked_until_utc > $now GROUP BY job_id;");
            cmd.Add("$q", DefaultQueueName);
            cmd.Add("$now", SqliteStorage.Utc(nowUtc));
            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            while (r.Read())
            {
                counts[r.GetString(0)] = r.GetInt32(1);
            }

            return counts;
        }

        // Robust recent slice wall-clock duration from successful attempts only. Throttled attempts
        // are excluded (status <> 'Succeeded'), so retry backoff never inflates the keep-up floor.
        // Returns (null, count) when fewer than the configured minimum samples are available.
        private (TimeSpan? Duration, int SampleCount) EstimateSliceDuration(string jobId, DateTimeOffset sinceUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                SELECT started_at_utc, completed_at_utc
                FROM slice_attempts
                WHERE job_id = $j
                  AND status = 'Succeeded'
                  AND started_at_utc IS NOT NULL
                  AND completed_at_utc IS NOT NULL
                  AND completed_at_utc >= $since
                ORDER BY completed_at_utc DESC
                LIMIT $cap;
                """);
            cmd.Add("$j", jobId);
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            cmd.Add("$cap", DurationSampleCap);

            var seconds = new List<double>();
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var started = SqliteStorage.ReadUtc(r, "started_at_utc");
                    var completed = SqliteStorage.ReadUtc(r, "completed_at_utc");
                    var elapsed = (completed - started).TotalSeconds;
                    if (elapsed > 0)
                    {
                        seconds.Add(elapsed);
                    }
                }
            }

            if (seconds.Count < options.MinDurationSamples)
            {
                return (null, seconds.Count);
            }

            seconds.Sort();
            return (Percentile(seconds, options.DurationPercentile), seconds.Count);
        }

        // Nearest-rank percentile over an ascending-sorted sample.
        private static TimeSpan Percentile(IReadOnlyList<double> sortedSeconds, double percentile)
        {
            var rank = (int)Math.Ceiling(Math.Clamp(percentile, 0d, 1d) * sortedSeconds.Count);
            var index = Math.Clamp(rank - 1, 0, sortedSeconds.Count - 1);
            return TimeSpan.FromSeconds(sortedSeconds[index]);
        }
    }
}
