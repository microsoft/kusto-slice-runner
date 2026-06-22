using KoLite.Local.Core.Schedules;

namespace KoLite.Local.Core.Scheduling
{
    // Classification of a job's catch-up situation. Only CatchingUp and NotKeepingUp are
    // worth surfacing in the UI; the other states intentionally render nothing.
    public enum CatchUpStatus
    {
        // Paused, disabled, or no eligible window yet: catch-up is not a meaningful concept.
        NotApplicable,

        // Backlog is at or below the floor (job is running at its normal cadence, or a single
        // slice just became eligible). Nothing useful to show.
        CaughtUp,

        // A real backlog exists and the rate keeps up, but the projected catch-up time is below
        // the "worth showing" floor.
        Negligible,

        // A real backlog exists but there is not enough recent throughput to estimate a rate.
        InsufficientData,

        // A real backlog exists and the recent processing rate is at or below real time, so at the
        // current rate the job will never catch up.
        NotKeepingUp,

        // A real backlog exists, the rate outpaces real time, and the projected catch-up time is
        // worth showing.
        CatchingUp
    }

    // Tunable thresholds that decide when the catch-up estimate is shown and how it is computed.
    public sealed record CatchUpOptions
    {
        // A backlog must exceed this many eligible-but-incomplete slices before anything is shown.
        // This excludes a job that just became eligible for its next slice or two.
        public int MinBacklogSlices { get; init; } = 2;

        // The projected catch-up time must be at least this long to be worth showing.
        public TimeSpan MinProjected { get; init; } = TimeSpan.FromMinutes(30);

        // Minimum number of recent successful completions required to estimate a rate.
        public int MinThroughputSamples { get; init; } = 3;

        // Minimum wall-clock span between the first and last recent completion required to estimate
        // a rate (guards against a rate computed from a tiny, noisy sample window).
        public TimeSpan MinThroughputSpan { get; init; } = TimeSpan.FromMinutes(10);

        // The furthest back the throughput sample ever looks. The effective sample window starts at
        // the later of (last definition change, now - this), so executions from before a schedule
        // change are excluded, but a long-stable job still uses this recent window.
        public TimeSpan MaxThroughputLookback { get; init; } = TimeSpan.FromHours(6);

        public static CatchUpOptions Default { get; } = new();
    }

    // A recent sample of successful slice completions used to estimate processing throughput.
    public sealed record CatchUpThroughputSample(
        int SucceededCount,
        DateTimeOffset? FirstCompletedUtc,
        DateTimeOffset? LastCompletedUtc);

    // The result of a catch-up estimation. Projection fields are null when they do not apply.
    public sealed record CatchUpProjection
    {
        public required CatchUpStatus Status { get; init; }

        // Eligible-but-incomplete slices and the equivalent data-time backlog.
        public int BacklogSlices { get; init; }
        public TimeSpan BacklogDataTime { get; init; }

        // The completed data-time frontier (latest completed slice end), and the eligibility target
        // the job is working toward (now - delay, clamped to endOn). The job converges to the
        // target; it never reaches literal "now" because of the configured delay.
        public DateTimeOffset? CompletedFrontierUtc { get; init; }
        public DateTimeOffset TargetFrontierUtc { get; init; }

        // Recent processing rate, when measurable.
        public double? SlicesPerHour { get; init; }

        // Data-time processed per unit wall-clock time (R). R > 1 means the job is gaining on the
        // target; R <= 1 means it is not keeping up.
        public double? RealTimeMultiple { get; init; }

        // Projected wall-clock time to reach the target frontier, and the resulting ETA.
        public TimeSpan? ProjectedCatchUp { get; init; }
        public DateTimeOffset? EtaUtc { get; init; }

        public bool ShouldDisplay => Status is CatchUpStatus.CatchingUp or CatchUpStatus.NotKeepingUp;
    }

    public static class CatchUpEstimator
    {
        // Resolves the start of the throughput sample window: the later of the last definition
        // change and (now - maxLookback). A recent schedule change shortens the window so the rate
        // reflects only executions under the current definition; a long-stable job falls back to
        // the recent maxLookback window.
        public static DateTimeOffset ResolveThroughputWindowStart(
            DateTimeOffset now,
            DateTimeOffset? lastDefinitionChangeUtc,
            TimeSpan maxLookback)
        {
            var floorUtc = now.ToUniversalTime() - maxLookback;
            if (lastDefinitionChangeUtc is { } changed && changed.ToUniversalTime() > floorUtc)
            {
                return changed.ToUniversalTime();
            }

            return floorUtc;
        }

        public static CatchUpProjection Estimate(
            DateTimeOffset now,
            JobDefinition definition,
            bool isEnabled,
            DateTimeOffset? completedFrontierUtc,
            int completedSliceCount,
            CatchUpThroughputSample throughput,
            CatchUpOptions? options = null)
        {
            var opts = options ?? CatchUpOptions.Default;
            var nowUtc = now.ToUniversalTime();
            var queryWindow = definition.QueryWindowSize;
            var startFromUtc = definition.StartFrom.ToUniversalTime();
            var targetFrontierUtc = nowUtc - definition.DelayFromUtcNow;

            // The job can never process past its end-on boundary (when set).
            var eligibleEndUtc = targetFrontierUtc;
            if (definition.EndOn is { } endOn)
            {
                var endOnUtc = endOn.ToUniversalTime();
                if (endOnUtc < eligibleEndUtc)
                {
                    eligibleEndUtc = endOnUtc;
                }
            }

            var notApplicable = new CatchUpProjection
            {
                Status = CatchUpStatus.NotApplicable,
                CompletedFrontierUtc = completedFrontierUtc,
                TargetFrontierUtc = targetFrontierUtc
            };

            if (!isEnabled || definition.IsPaused || queryWindow <= TimeSpan.Zero || eligibleEndUtc <= startFromUtc)
            {
                return notApplicable;
            }

            var eligibleTotal = (eligibleEndUtc - startFromUtc).Ticks / queryWindow.Ticks;
            var backlogSlices = (int)Math.Max(0, Math.Min(int.MaxValue, eligibleTotal - completedSliceCount));
            var backlogDataTime = TimeSpan.FromTicks(queryWindow.Ticks * backlogSlices);

            var baseProjection = new CatchUpProjection
            {
                Status = CatchUpStatus.CaughtUp,
                BacklogSlices = backlogSlices,
                BacklogDataTime = backlogDataTime,
                CompletedFrontierUtc = completedFrontierUtc,
                TargetFrontierUtc = targetFrontierUtc
            };

            if (backlogSlices <= opts.MinBacklogSlices)
            {
                return baseProjection;
            }

            if (throughput.SucceededCount < opts.MinThroughputSamples
                || throughput.FirstCompletedUtc is not { } firstCompleted
                || throughput.LastCompletedUtc is not { } lastCompleted)
            {
                return baseProjection with { Status = CatchUpStatus.InsufficientData };
            }

            var span = lastCompleted.ToUniversalTime() - firstCompleted.ToUniversalTime();
            if (span < opts.MinThroughputSpan)
            {
                return baseProjection with { Status = CatchUpStatus.InsufficientData };
            }

            // N timestamps span (N-1) intervals; this is the average completion rate over the sample.
            var completionsPerHour = (throughput.SucceededCount - 1) / span.TotalHours;
            var realTimeMultiple = completionsPerHour * queryWindow.TotalHours;

            var withRate = baseProjection with
            {
                SlicesPerHour = completionsPerHour,
                RealTimeMultiple = realTimeMultiple
            };

            if (realTimeMultiple <= 1.0)
            {
                return withRate with { Status = CatchUpStatus.NotKeepingUp };
            }

            var projectedHours = backlogDataTime.TotalHours / (realTimeMultiple - 1.0);
            var projected = TimeSpan.FromHours(projectedHours);

            if (projected < opts.MinProjected)
            {
                return withRate with { Status = CatchUpStatus.Negligible };
            }

            return withRate with
            {
                Status = CatchUpStatus.CatchingUp,
                ProjectedCatchUp = projected,
                EtaUtc = nowUtc + projected
            };
        }
    }
}
