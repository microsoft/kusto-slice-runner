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

        // A cluster must have at least this many distinct throttled slices in the window before the
        // rate-based advisory is shown (a volume floor so a high throttled-attempt percentage computed
        // from a tiny denominator does not trip the trigger).
        public int MinThrottledSlices { get; init; } = 3;

        // Appearance gate: the throttled-attempt rate (throttled attempts / total attempts) over the
        // window must reach this percentage before a cluster is surfaced, provided the volume floors
        // below are also met. Expressed as a percentage (5 = 5%).
        public double RateThresholdPercent { get; init; } = 5.0;

        // Minimum total slice attempts on a cluster within the window before the rate gate can trip,
        // so the percentage is not computed from a noisy handful of attempts.
        public int MinAttemptsForRate { get; init; } = 20;

        // Hysteresis: once surfaced, a cluster stays surfaced until it has gone this long with no new
        // ingestion-throttle observation (a continuous "clean" gap), so the page/banner does not flap.
        public TimeSpan CleanPeriod { get; init; } = TimeSpan.FromMinutes(15);

        // How far back to look for recent slices that terminally failed (dead-lettered) on consecutive
        // throttled attempts. Such a slice forces the advisory to show regardless of the rate gate.
        public TimeSpan TerminalFailureLookback { get; init; } = TimeSpan.FromMinutes(60);

        // Target wall-clock time within which a backfilling job should clear its eligible backlog,
        // used to size the catch-up floor so a deliberate backfill is not trimmed to a crawl.
        public TimeSpan CatchUpTargetDuration { get; init; } = TimeSpan.FromHours(24);

        // How far back successful slice durations are sampled to estimate the keep-up floor.
        public TimeSpan DurationLookback { get; init; } = TimeSpan.FromHours(6);

        // Minimum clean (successful) duration samples required before a floor is estimated.
        public int MinDurationSamples { get; init; } = 5;

        // Percentile (0..1) of successful slice durations used as the robust duration estimate.
        public double DurationPercentile { get; init; } = 0.75;

        // Safety margin above the bare keep-up parallelism (see ParallelismRecommendationOptions).
        public double KeepUpSafetyFactor { get; init; } = 1.5;

        public static ThrottleAdvisorOptions Default { get; } = new();

        public ParallelismRecommendationOptions ToRecommendationOptions() =>
            new() { KeepUpSafetyFactor = KeepUpSafetyFactor, CatchUpTargetDuration = CatchUpTargetDuration };
    }

    // A slice that terminally failed (dead-lettered) on consecutive ingestion-throttle attempts and is
    // still unresolved. This is the worst throttling outcome (a data gap needing a rerun), so it is
    // surfaced prominently and forces the advisory to show even when the rate gate is not met.
    public sealed record ThrottleTerminalFailureSlice(
        string JobId,
        string ActivityId,
        string ClusterUri,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int ThrottledAttempts,
        DateTimeOffset LastObservedUtc,
        string CurrentState);

    // One throttled cluster's advisory: the sustained-throttle evidence, the recent throttled-attempt
    // rate (how bad it is), any slices that were lost to throttling, plus the ranked per-job
    // parallelism recommendations the operator can choose to apply.
    public sealed record ClusterThrottleAdvisory(
        string ClusterUri,
        int ThrottledSliceCount,
        int ObservationCount,
        int? LatestReportedCapacity,
        DateTimeOffset FirstObservedUtc,
        DateTimeOffset LatestObservedUtc,
        TimeSpan Window,
        int ThrottledAttemptCount,
        int TotalAttemptCount,
        IReadOnlyList<ThrottleTerminalFailureSlice> TerminalFailures,
        IReadOnlyList<ParallelismRecommendation> Recommendations)
    {
        // Throttled-attempt rate over the window as a percentage; 0 when there were no attempts.
        public double ThrottledAttemptPercent => TotalAttemptCount <= 0
            ? 0d
            : Math.Round((double)ThrottledAttemptCount * 100d / TotalAttemptCount, 1);
    }
}
