namespace KoLite.Local.Core.Throttling
{
    // Tunables for turning observed throughput into a safe parallelism recommendation.
    public sealed record ParallelismRecommendationOptions
    {
        // Margin applied above the bare keep-up parallelism so a right-sized job still has slack for
        // slice-duration variance. 1.0 = exactly keep up; 1.5 = keep up with 50% headroom.
        public double KeepUpSafetyFactor { get; init; } = 1.5;

        // Target wall-clock time within which a backfilling job should clear its eligible backlog.
        // Used to size the catch-up floor for jobs that are behind real time, so they are not trimmed
        // all the way down to the keep-up floor (which would make a deliberate backfill crawl).
        public TimeSpan CatchUpTargetDuration { get; init; } = TimeSpan.FromHours(24);

        public static ParallelismRecommendationOptions Default { get; } = new();
    }

    // Per-job inputs for a recommendation, gathered for the active jobs on a throttled cluster.
    // ObservedSliceDuration is a robust recent statistic (e.g. p75) of successful slice wall-clock
    // time; it is null when there are too few clean samples to estimate a floor. It MUST be derived
    // from successful attempts only, because throttled attempts are inflated by retry backoff and
    // would bias the floor upward.
    public sealed record JobThrottleSnapshot(
        string JobId,
        string ActivityId,
        string ClusterUri,
        int CurrentMaxParallelism,
        long CatalogVersion,
        TimeSpan QueryWindowSize,
        int InFlightCount,
        TimeSpan? ObservedSliceDuration,
        int DurationSampleCount,
        int BacklogSlices = 0,
        TimeSpan BacklogDataTime = default,
        bool IsBackfilling = false);

    public enum ParallelismRecommendationStatus
    {
        // Over-provisioned: current parallelism exceeds the keep-up floor, so it can be trimmed.
        Recommended,

        // Already at or below the keep-up floor: trimming would starve the job, so do not reduce.
        AtOrBelowKeepUpFloor,

        // Not enough clean throughput samples to estimate the keep-up floor yet.
        InsufficientData
    }

    public sealed record ParallelismRecommendation
    {
        public required string JobId { get; init; }
        public required string ActivityId { get; init; }
        public required string ClusterUri { get; init; }
        public required ParallelismRecommendationStatus Status { get; init; }
        public required int CurrentMaxParallelism { get; init; }

        // Catalog version captured when the snapshot was taken, used as the optimistic-concurrency
        // expectedVersion when the operator applies the recommendation.
        public long CatalogVersion { get; init; }

        // Minimum parallelism that still keeps up with real time, or null when it cannot be estimated.
        public int? KeepUpFloor { get; init; }

        // True when the job has a real eligible backlog (it is behind real time and working to catch
        // up). For these jobs the recommendation targets the catch-up floor, never the lower keep-up
        // floor, so a deliberate backfill is not throttled down to a crawl.
        public bool IsBackfilling { get; init; }

        // The job's eligible backlog expressed as data-time (queryWindow * backlogSlices). Zero for a
        // job that is keeping up with real time.
        public TimeSpan BacklogDataTime { get; init; }

        // Minimum parallelism that still clears the backlog within the configured target, for a
        // backfilling job. Null for a non-backfilling job (or when it cannot be estimated). When set,
        // it is at least the keep-up floor and is used as the recommended target.
        public int? CatchUpFloor { get; init; }

        // For a backfilling job, the rough projected wall-clock time to clear the backlog at the
        // current parallelism and at the recommended parallelism, holding single-slice execution time
        // constant. Null when the job is not backfilling or the rate would never catch up.
        public TimeSpan? CurrentCatchUpEta { get; init; }
        public TimeSpan? ProjectedCatchUpEta { get; init; }

        // Suggested new maxParallelism (the keep-up floor) for a Recommended job; null otherwise.
        public int? RecommendedMaxParallelism { get; init; }

        // CurrentMaxParallelism - KeepUpFloor when the floor is known (the ranking key; can be <= 0).
        public int? Headroom { get; init; }

        public double SlicesPerDay { get; init; }
        public TimeSpan? ObservedSliceDuration { get; init; }
        public int DurationSampleCount { get; init; }
        public int InFlightCount { get; init; }
    }

    public static class ParallelismRecommendationEngine
    {
        private const double SecondsPerDay = 86_400d;

        // minParallelism = max(1, ceil((D / W) * safetyFactor)). A slice covers W of data-time and
        // takes D wall-clock; at parallelism P the job processes ~ P * W / D of data-time per unit
        // wall-clock, so keeping up (rate >= 1) needs P >= D / W. Returns null when inputs are unusable.
        public static int? ComputeKeepUpFloor(TimeSpan queryWindowSize, TimeSpan? observedSliceDuration, double safetyFactor)
        {
            if (observedSliceDuration is not { } duration
                || duration <= TimeSpan.Zero
                || queryWindowSize <= TimeSpan.Zero
                || safetyFactor <= 0)
            {
                return null;
            }

            var ratio = duration.TotalSeconds / queryWindowSize.TotalSeconds * safetyFactor;
            return Math.Max(1, (int)Math.Ceiling(ratio));
        }

        // Minimum parallelism that clears a backlog of B data-time within the target wall-clock window
        // T, holding single-slice execution time constant. A slice covers W of data-time in D
        // wall-clock, so at parallelism P the job advances data-time at rate R = P*W/D; clearing B
        // within T needs the net gain (R-1) >= B/T, i.e. P >= (D/W)*(1 + B/T). The same safety margin
        // as the keep-up floor is applied. Returns null when inputs are unusable or there is no
        // backlog (in which case the keep-up floor governs).
        public static int? ComputeCatchUpFloor(TimeSpan queryWindowSize, TimeSpan? observedSliceDuration, TimeSpan backlogDataTime, TimeSpan catchUpTarget, double safetyFactor)
        {
            if (observedSliceDuration is not { } duration
                || duration <= TimeSpan.Zero
                || queryWindowSize <= TimeSpan.Zero
                || catchUpTarget <= TimeSpan.Zero
                || backlogDataTime <= TimeSpan.Zero
                || safetyFactor <= 0)
            {
                return null;
            }

            var ratio = duration.TotalSeconds / queryWindowSize.TotalSeconds
                * (1d + backlogDataTime.TotalSeconds / catchUpTarget.TotalSeconds)
                * safetyFactor;
            return Math.Max(1, (int)Math.Ceiling(ratio));
        }

        // Rough projected wall-clock time to clear a backlog of B data-time at parallelism P, holding
        // single-slice execution time constant: the data-time advance rate is R = P*W/D, so the net
        // gain over real time is (R-1) and the time to clear B is B/(R-1). Returns null when R <= 1
        // (at this parallelism the job never catches up) or inputs are unusable.
        public static TimeSpan? ComputeCatchUpEta(TimeSpan queryWindowSize, TimeSpan? observedSliceDuration, TimeSpan backlogDataTime, int parallelism)
        {
            if (observedSliceDuration is not { } duration
                || duration <= TimeSpan.Zero
                || queryWindowSize <= TimeSpan.Zero
                || backlogDataTime <= TimeSpan.Zero
                || parallelism <= 0)
            {
                return null;
            }

            var realTimeMultiple = parallelism * queryWindowSize.TotalSeconds / duration.TotalSeconds;
            if (realTimeMultiple <= 1d)
            {
                return null;
            }

            // Guard against an effectively-infinite ETA (rate only marginally above real time with a
            // very large backlog), which would otherwise overflow TimeSpan. Null renders as the same
            // "won't catch up" text used when the rate cannot keep up at all.
            var seconds = backlogDataTime.TotalSeconds / (realTimeMultiple - 1d);
            if (double.IsNaN(seconds) || seconds >= TimeSpan.MaxValue.TotalSeconds)
            {
                return null;
            }

            return TimeSpan.FromSeconds(seconds);
        }

        // Builds one recommendation per snapshot and ranks them: actionable reductions first, ordered
        // by headroom descending (most over-provisioned first), then the jobs that should not be
        // trimmed, then the jobs lacking data. Stable and deterministic.
        public static IReadOnlyList<ParallelismRecommendation> Recommend(
            IEnumerable<JobThrottleSnapshot> snapshots,
            ParallelismRecommendationOptions? options = null)
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            var opts = options ?? ParallelismRecommendationOptions.Default;

            var recommendations = snapshots.Select(snapshot => Evaluate(snapshot, opts)).ToList();
            return recommendations
                .OrderBy(r => StatusRank(r.Status))
                .ThenByDescending(r => r.Headroom ?? int.MinValue)
                .ThenBy(r => r.ActivityId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static ParallelismRecommendation Evaluate(JobThrottleSnapshot snapshot, ParallelismRecommendationOptions opts)
        {
            var slicesPerDay = snapshot.QueryWindowSize > TimeSpan.Zero
                ? SecondsPerDay / snapshot.QueryWindowSize.TotalSeconds
                : 0d;

            var keepUpFloor = ComputeKeepUpFloor(snapshot.QueryWindowSize, snapshot.ObservedSliceDuration, opts.KeepUpSafetyFactor);

            var partial = new ParallelismRecommendation
            {
                JobId = snapshot.JobId,
                ActivityId = snapshot.ActivityId,
                ClusterUri = snapshot.ClusterUri,
                Status = ParallelismRecommendationStatus.InsufficientData,
                CurrentMaxParallelism = snapshot.CurrentMaxParallelism,
                CatalogVersion = snapshot.CatalogVersion,
                SlicesPerDay = slicesPerDay,
                ObservedSliceDuration = snapshot.ObservedSliceDuration,
                DurationSampleCount = snapshot.DurationSampleCount,
                InFlightCount = snapshot.InFlightCount,
                IsBackfilling = snapshot.IsBackfilling,
                BacklogDataTime = snapshot.BacklogDataTime
            };

            if (keepUpFloor is not { } floor)
            {
                return partial;
            }

            // A backfilling job is sized to clear its backlog within the target window, never trimmed
            // below that catch-up floor (which is itself at least the keep-up floor). A job that is
            // keeping up uses the keep-up floor directly.
            var catchUpFloor = snapshot.IsBackfilling
                ? ComputeCatchUpFloor(snapshot.QueryWindowSize, snapshot.ObservedSliceDuration, snapshot.BacklogDataTime, opts.CatchUpTargetDuration, opts.KeepUpSafetyFactor)
                : null;
            var effectiveFloor = catchUpFloor is { } cf ? Math.Max(floor, cf) : floor;

            var currentEta = snapshot.IsBackfilling
                ? ComputeCatchUpEta(snapshot.QueryWindowSize, snapshot.ObservedSliceDuration, snapshot.BacklogDataTime, snapshot.CurrentMaxParallelism)
                : null;

            var withFloors = partial with
            {
                KeepUpFloor = floor,
                CatchUpFloor = catchUpFloor,
                CurrentCatchUpEta = currentEta
            };

            var headroom = snapshot.CurrentMaxParallelism - effectiveFloor;
            if (headroom > 0)
            {
                var projectedEta = snapshot.IsBackfilling
                    ? ComputeCatchUpEta(snapshot.QueryWindowSize, snapshot.ObservedSliceDuration, snapshot.BacklogDataTime, effectiveFloor)
                    : null;

                return withFloors with
                {
                    Status = ParallelismRecommendationStatus.Recommended,
                    RecommendedMaxParallelism = effectiveFloor,
                    ProjectedCatchUpEta = projectedEta,
                    Headroom = headroom
                };
            }

            return withFloors with
            {
                Status = ParallelismRecommendationStatus.AtOrBelowKeepUpFloor,
                Headroom = headroom
            };
        }

        private static int StatusRank(ParallelismRecommendationStatus status) => status switch
        {
            ParallelismRecommendationStatus.Recommended => 0,
            ParallelismRecommendationStatus.AtOrBelowKeepUpFloor => 1,
            _ => 2
        };
    }
}
