namespace KoLite.Local.Core.Throttling
{
    // Tunables for turning observed throughput into a safe parallelism recommendation.
    public sealed record ParallelismRecommendationOptions
    {
        // Margin applied above the bare keep-up parallelism so a right-sized job still has slack for
        // slice-duration variance. 1.0 = exactly keep up; 1.5 = keep up with 50% headroom.
        public double KeepUpSafetyFactor { get; init; } = 1.5;

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
        TimeSpan QueryWindowSize,
        int InFlightCount,
        TimeSpan? ObservedSliceDuration,
        int DurationSampleCount);

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

        // Minimum parallelism that still keeps up with real time, or null when it cannot be estimated.
        public int? KeepUpFloor { get; init; }

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

            var floor = ComputeKeepUpFloor(snapshot.QueryWindowSize, snapshot.ObservedSliceDuration, opts.KeepUpSafetyFactor);

            var partial = new ParallelismRecommendation
            {
                JobId = snapshot.JobId,
                ActivityId = snapshot.ActivityId,
                ClusterUri = snapshot.ClusterUri,
                Status = ParallelismRecommendationStatus.InsufficientData,
                CurrentMaxParallelism = snapshot.CurrentMaxParallelism,
                SlicesPerDay = slicesPerDay,
                ObservedSliceDuration = snapshot.ObservedSliceDuration,
                DurationSampleCount = snapshot.DurationSampleCount,
                InFlightCount = snapshot.InFlightCount
            };

            if (floor is not { } keepUpFloor)
            {
                return partial;
            }

            var headroom = snapshot.CurrentMaxParallelism - keepUpFloor;
            if (headroom > 0)
            {
                return partial with
                {
                    Status = ParallelismRecommendationStatus.Recommended,
                    KeepUpFloor = keepUpFloor,
                    RecommendedMaxParallelism = keepUpFloor,
                    Headroom = headroom
                };
            }

            return partial with
            {
                Status = ParallelismRecommendationStatus.AtOrBelowKeepUpFloor,
                KeepUpFloor = keepUpFloor,
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
