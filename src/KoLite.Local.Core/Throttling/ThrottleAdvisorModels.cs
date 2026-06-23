namespace KoLite.Local.Core.Throttling
{
    // Knobs for the advisory throttle detector and its recommendations. All read-only/non-mutating;
    // the feature only surfaces suggestions, so Enabled defaults to true. LocalApp binds these from
    // KoLite:Throttling:* configuration.
    public sealed record ThrottleAdvisorOptions
    {
        // Master switch for surfacing advisories (detection recording is independent and always on).
        public bool Enabled { get; init; } = true;

        // Rolling window over which throttle observations are counted for the sustained trigger.
        public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(20);

        // A cluster must have at least this many distinct throttled slices in the window before any
        // advisory is shown (the "sustained" trigger that ignores single self-healing 429s).
        public int MinThrottledSlices { get; init; } = 3;

        // How far back successful slice durations are sampled to estimate the keep-up floor.
        public TimeSpan DurationLookback { get; init; } = TimeSpan.FromHours(6);

        // Minimum clean (successful) duration samples required before a floor is estimated.
        public int MinDurationSamples { get; init; } = 5;

        // Percentile (0..1) of successful slice durations used as the robust duration estimate.
        public double DurationPercentile { get; init; } = 0.75;

        // Safety margin above the bare keep-up parallelism (see ParallelismRecommendationOptions).
        public double KeepUpSafetyFactor { get; init; } = 1.5;

        // Observations older than this are pruned by read-model retention.
        public TimeSpan ObservationRetention { get; init; } = TimeSpan.FromDays(7);

        public static ThrottleAdvisorOptions Default { get; } = new();

        public ParallelismRecommendationOptions ToRecommendationOptions() =>
            new() { KeepUpSafetyFactor = KeepUpSafetyFactor };
    }

    // One throttled cluster's advisory: the sustained-throttle evidence plus the ranked per-job
    // parallelism recommendations the operator can choose to apply.
    public sealed record ClusterThrottleAdvisory(
        string ClusterUri,
        int ThrottledSliceCount,
        int ObservationCount,
        int? LatestReportedCapacity,
        DateTimeOffset FirstObservedUtc,
        DateTimeOffset LatestObservedUtc,
        TimeSpan Window,
        IReadOnlyList<ParallelismRecommendation> Recommendations);
}
