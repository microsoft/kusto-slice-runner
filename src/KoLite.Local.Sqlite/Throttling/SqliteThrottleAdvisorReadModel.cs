using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Throttling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Observability;

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
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly IClock clock;
        private readonly ThrottleAdvisorOptions options;

        public SqliteThrottleAdvisorReadModel(
            IKoLiteSqliteConnectionFactory connectionFactory,
            SqliteJobCatalogRepository catalog,
            SqliteIngestionThrottleRepository throttleStore,
            SqliteOperationalReadModelRepository readModels,
            IClock clock,
            ThrottleAdvisorOptions options)
        {
            this.connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.throttleStore = throttleStore ?? throw new ArgumentNullException(nameof(throttleStore));
            this.readModels = readModels ?? throw new ArgumentNullException(nameof(readModels));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        // One advisory per cluster currently displayed: either sustained throttling crosses the
        // rate gate while still active (within the clean period), or a slice was recently lost to
        // throttling. Empty when the feature is disabled or nothing crosses the display threshold.
        public IReadOnlyList<ClusterThrottleAdvisory> BuildAdvisories()
        {
            if (!options.Enabled)
            {
                return Array.Empty<ClusterThrottleAdvisory>();
            }

            var now = clock.UtcNow;
            var displayed = EvaluateDisplayedClusters(now);
            if (displayed.Count == 0)
            {
                return Array.Empty<ClusterThrottleAdvisory>();
            }

            var windowStart = now - options.Window;
            var enabledJobs = catalog.List(enabledOnly: true);
            var activityById = catalog.List(enabledOnly: false)
                .ToDictionary(job => job.JobId, job => job.ActivityId, StringComparer.Ordinal);
            var inFlight = CountInFlightByJob(now);
            var durationSince = now - options.DurationLookback;
            var recommendationOptions = options.ToRecommendationOptions();

            var advisories = new List<ClusterThrottleAdvisory>();
            foreach (var cluster in displayed)
            {
                var throttledJobIds = throttleStore.ListThrottledJobIds(cluster.ClusterUri, windowStart);
                var terminalJobIds = cluster.TerminalFailures.Select(t => t.JobId).ToHashSet(StringComparer.Ordinal);
                var snapshots = enabledJobs
                    .Where(job => StringComparer.Ordinal.Equals(job.Definition.Target.ClusterUri, cluster.ClusterUri))
                    .Where(job => inFlight.GetValueOrDefault(job.JobId) > 0 || throttledJobIds.Contains(job.JobId) || terminalJobIds.Contains(job.JobId))
                    .Select(job => ToSnapshot(job, cluster.ClusterUri, inFlight.GetValueOrDefault(job.JobId), durationSince, now))
                    .ToList();

                var recommendations = ParallelismRecommendationEngine.Recommend(snapshots, recommendationOptions);
                var terminalFailures = cluster.TerminalFailures
                    .Select(t => new ThrottleTerminalFailureSlice(
                        t.JobId,
                        activityById.GetValueOrDefault(t.JobId, t.JobId),
                        t.ClusterUri,
                        t.SliceStartUtc,
                        t.SliceEndUtc,
                        t.ThrottledAttempts,
                        t.LastObservedUtc,
                        t.CurrentState))
                    .ToList();

                advisories.Add(new ClusterThrottleAdvisory(
                    cluster.ClusterUri,
                    cluster.DistinctThrottledSlices,
                    cluster.ThrottledAttemptCount,
                    cluster.LatestReportedCapacity,
                    cluster.FirstObservedUtc ?? now,
                    cluster.LatestObservedUtc ?? now,
                    options.Window,
                    cluster.ThrottledAttemptCount,
                    cluster.TotalAttemptCount,
                    terminalFailures,
                    recommendations));
            }

            return advisories;
        }

        // Cheap display check for the dashboard banner: the cluster URIs currently displayed, without
        // the per-job recommendation/duration work BuildAdvisories does.
        public IReadOnlyList<string> ListSustainedClusterUris()
        {
            if (!options.Enabled)
            {
                return Array.Empty<string>();
            }

            return EvaluateDisplayedClusters(clock.UtcNow)
                .Select(cluster => cluster.ClusterUri)
                .ToList();
        }

        // Evidence for one displayed cluster: window throttle counts, the throttled-attempt rate
        // denominator, the latest/first observation times, and any slices lost to throttling.
        private sealed record ClusterDisplayState(
            string ClusterUri,
            int ThrottledAttemptCount,
            int TotalAttemptCount,
            int DistinctThrottledSlices,
            int? LatestReportedCapacity,
            DateTimeOffset? FirstObservedUtc,
            DateTimeOffset? LatestObservedUtc,
            IReadOnlyList<TerminalThrottleSlice> TerminalFailures);

        // Decides which clusters to surface. A cluster shows when, over the trailing window, the
        // throttled-attempt rate reaches the threshold with enough distinct slices and total attempts,
        // and it is still active (a throttle within the clean period) — or when a slice has recently
        // been lost to throttling, which forces display regardless of the rate gate.
        private List<ClusterDisplayState> EvaluateDisplayedClusters(DateTimeOffset now)
        {
            var windowStart = now - options.Window;
            var summaries = throttleStore.SummarizeWindow(windowStart)
                .ToDictionary(summary => summary.ClusterUri, StringComparer.Ordinal);
            var terminalByCluster = throttleStore.ListUnresolvedTerminalFailures(now - options.TerminalFailureLookback)
                .GroupBy(slice => slice.ClusterUri, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<TerminalThrottleSlice>)group.ToList(), StringComparer.Ordinal);

            var attemptsByJob = CountAttemptsByJob(windowStart);
            var clusterByJob = catalog.List(enabledOnly: false)
                .ToDictionary(job => job.JobId, job => job.Definition.Target.ClusterUri, StringComparer.Ordinal);
            var totalByCluster = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (jobId, attempts) in attemptsByJob)
            {
                if (clusterByJob.TryGetValue(jobId, out var cluster))
                {
                    totalByCluster[cluster] = totalByCluster.GetValueOrDefault(cluster) + attempts;
                }
            }

            var clusters = new HashSet<string>(summaries.Keys, StringComparer.Ordinal);
            clusters.UnionWith(terminalByCluster.Keys);

            var displayed = new List<ClusterDisplayState>();
            foreach (var clusterUri in clusters)
            {
                var summary = summaries.GetValueOrDefault(clusterUri);
                var terminalFailures = terminalByCluster.GetValueOrDefault(clusterUri) ?? Array.Empty<TerminalThrottleSlice>();

                var throttledAttempts = summary?.ObservationCount ?? 0;
                var distinctSlices = summary?.ThrottledSliceCount ?? 0;
                var totalAttempts = totalByCluster.GetValueOrDefault(clusterUri);
                var latestObserved = summary?.LatestObservedUtc
                    ?? (terminalFailures.Count > 0 ? terminalFailures.Max(t => t.LastObservedUtc) : (DateTimeOffset?)null);
                var firstObserved = summary?.FirstObservedUtc
                    ?? (terminalFailures.Count > 0 ? terminalFailures.Min(t => t.LastObservedUtc) : (DateTimeOffset?)null);

                var rate = totalAttempts > 0 ? (double)throttledAttempts * 100d / totalAttempts : 0d;
                var activeWithinCleanPeriod = latestObserved is { } latest && now - latest <= options.CleanPeriod;
                var rateGateMet = distinctSlices >= options.MinThrottledSlices
                    && totalAttempts >= options.MinAttemptsForRate
                    && rate >= options.RateThresholdPercent;

                if (!((rateGateMet && activeWithinCleanPeriod) || terminalFailures.Count > 0))
                {
                    continue;
                }

                displayed.Add(new ClusterDisplayState(
                    clusterUri,
                    throttledAttempts,
                    totalAttempts,
                    distinctSlices,
                    summary?.LatestReportedCapacity,
                    firstObserved,
                    latestObserved,
                    terminalFailures));
            }

            return displayed
                .OrderByDescending(c => c.TerminalFailures.Count > 0)
                .ThenByDescending(c => c.ThrottledAttemptCount)
                .ThenBy(c => c.ClusterUri, StringComparer.Ordinal)
                .ToList();
        }

        // The keep-up floor for a single job, used as the server-side guardrail when applying a
        // recommendation. Null when the job is unknown or has too few clean duration samples.
        public int? EstimateKeepUpFloor(string jobId)
        {
            var record = catalog.Get(jobId);
            if (record is null)
            {
                return null;
            }

            var (duration, _) = EstimateSliceDuration(jobId, clock.UtcNow - options.DurationLookback);
            return ParallelismRecommendationEngine.ComputeKeepUpFloor(record.Definition.QueryWindowSize, duration, options.KeepUpSafetyFactor);
        }

        private JobThrottleSnapshot ToSnapshot(JobCatalogRecord job, string clusterUri, int inFlightCount, DateTimeOffset durationSinceUtc, DateTimeOffset nowUtc)
        {
            var definition = job.Definition;
            var (duration, sampleCount) = EstimateSliceDuration(job.JobId, durationSinceUtc);
            var (backlogSlices, backlogDataTime, isBackfilling) = EstimateBacklog(job, nowUtc);
            return new JobThrottleSnapshot(
                job.JobId,
                job.ActivityId,
                clusterUri,
                definition.MaxParallelism,
                job.CatalogVersion,
                definition.QueryWindowSize,
                inFlightCount,
                duration,
                sampleCount,
                backlogSlices,
                backlogDataTime,
                isBackfilling);
        }

        // Eligible backlog for a job via the shared catch-up estimator. A job is "backfilling" when it
        // has a real eligible backlog it can work off (above the estimator's minimum), so the advisor
        // sizes its recommendation to clear that backlog instead of trimming it to the keep-up floor.
        private (int BacklogSlices, TimeSpan BacklogDataTime, bool IsBackfilling) EstimateBacklog(JobCatalogRecord job, DateTimeOffset nowUtc)
        {
            var statuses = readModels.GetSliceStatus(job.JobId);
            var completed = statuses.Where(s => string.Equals(s.Status, "Completed", StringComparison.Ordinal)).ToList();
            DateTimeOffset? completedFrontier = completed.Count == 0 ? null : completed.Max(s => s.SliceEndUtc);
            var dependencyBlocked = statuses.Count(s => string.Equals(s.Status, "DependencyBlocked", StringComparison.Ordinal));

            var catchUpOptions = CatchUpOptions.Default;
            var throughput = readModels.GetRecentSucceededThroughput(job.JobId, nowUtc - catchUpOptions.MaxThroughputLookback);
            var sample = new CatchUpThroughputSample(throughput.SucceededCount, throughput.FirstCompletedUtc, throughput.LastCompletedUtc);

            var projection = CatchUpEstimator.Estimate(nowUtc, job.Definition, job.IsEnabled, completedFrontier, completed.Count, sample, dependencyBlocked, lastDefinitionChangeUtc: null, catchUpOptions);
            var isBackfilling = projection.BacklogSlices > catchUpOptions.MinBacklogSlices && projection.BacklogDataTime > TimeSpan.Zero;
            return (projection.BacklogSlices, projection.BacklogDataTime, isBackfilling);
        }

        // Total slice attempts per job since sinceUtc (the throttled-attempt rate denominator). Keyed
        // by job id; the caller maps jobs to clusters via the catalog.
        private Dictionary<string, int> CountAttemptsByJob(DateTimeOffset sinceUtc)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT job_id, COUNT(*) AS attempts FROM slice_attempts WHERE completed_at_utc IS NOT NULL AND completed_at_utc >= $since GROUP BY job_id;");
            cmd.Add("$since", SqliteStorage.Utc(sinceUtc));
            using var r = cmd.ExecuteReader();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            while (r.Read())
            {
                counts[r.GetString(0)] = r.GetInt32(1);
            }

            return counts;
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
