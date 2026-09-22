// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Schedules;

namespace KoLite.Local.Core.Scheduling
{
    // Classification of a job's catch-up situation. CatchingUp, NotKeepingUp, and InsufficientData
    // are surfaced in the UI (see ShouldDisplay); the other states intentionally render nothing.
    public enum CatchUpStatus
    {
        // Paused, disabled, or no eligible window yet: catch-up is not a meaningful concept.
        NotApplicable,

        // Backlog is at or below the floor (job is running at its normal cadence, or a single
        // slice just became eligible). Nothing useful to show.
        CaughtUp,

        // A real backlog exists but there is not enough recent throughput to estimate a rate.
        InsufficientData,

        // A real backlog exists and the recent processing rate is at or below real time, so at the
        // current rate the job will never catch up.
        NotKeepingUp,

        // A real backlog exists and the rate outpaces real time, so the job is projected to catch
        // up. Shown continuously while a real, actionable backlog remains.
        CatchingUp
    }

    // Tunable thresholds that decide when the catch-up estimate is shown and how it is computed.
    public sealed record CatchUpOptions
    {
        // A backlog must exceed this many eligible-but-incomplete slices before anything is shown.
        // This excludes a job that just became eligible for its next slice or two.
        public int MinBacklogSlices { get; init; } = 2;

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

        // Eligible-but-incomplete slices and the equivalent data-time backlog. Two kinds of eligible
        // slices are excluded because the job will not work them off on its own: slices currently
        // blocked on an upstream dependency (reported separately in BacklogBlockedSlices), and
        // terminal dead-lettered slices (treated as done -- they never run again without an operator
        // rerun/repair, so counting them would falsely report the job as "catching up").
        public int BacklogSlices { get; init; }
        public TimeSpan BacklogDataTime { get; init; }

        // Eligible-but-incomplete slices currently blocked on an upstream dependency, excluded from
        // the backlog above. Surfaced so the UI can explain why blocked work is not counted.
        public int BacklogBlockedSlices { get; init; }

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

        // Throughput-sampling provenance, populated for every estimate. These let the UI explain an
        // InsufficientData state: when sampling started, how many recent successful completions have
        // been collected so far, how many (over what minimum span) are required before a rate can be
        // projected, and whether the sample window was shortened by a recent definition change (the
        // common reason the data is briefly insufficient right after an edit).
        public DateTimeOffset? ThroughputWindowStartUtc { get; init; }
        public bool ThroughputWindowBoundedByDefinitionChange { get; init; }
        public int ObservedThroughputSamples { get; init; }
        public int RequiredThroughputSamples { get; init; }
        public TimeSpan RequiredThroughputSpan { get; init; }

        // Wall-clock span between the first and last sampled completion (TimeSpan.Zero when fewer
        // than two completions exist). The rate estimate is gated on this reaching
        // RequiredThroughputSpan, so the collecting-data UI surfaces it as its own "time collected"
        // requirement alongside the completion-count requirement.
        public TimeSpan ObservedThroughputSpan { get; init; }

        // Per-requirement gating state for the collecting-data checklist: whether each gate is met,
        // and how much more completion-span is still required before an estimate can be projected.
        public bool ThroughputSamplesSatisfied => ObservedThroughputSamples >= RequiredThroughputSamples;

        public bool ThroughputSpanSatisfied => ObservedThroughputSpan >= RequiredThroughputSpan;

        public TimeSpan ThroughputSpanRemaining
        {
            get
            {
                var remaining = RequiredThroughputSpan - ObservedThroughputSpan;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public bool ShouldDisplay => Status is CatchUpStatus.CatchingUp or CatchUpStatus.NotKeepingUp or CatchUpStatus.InsufficientData;
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
            int dependencyBlockedSliceCount = 0,
            int deadLetteredSliceCount = 0,
            DateTimeOffset? lastDefinitionChangeUtc = null,
            CatchUpOptions? options = null)
        {
            var opts = options ?? CatchUpOptions.Default;
            var nowUtc = now.ToUniversalTime();
            var queryWindow = definition.QueryWindowSize;
            var startFromUtc = definition.StartFrom.ToUniversalTime();
            var targetFrontierUtc = nowUtc - definition.DelayFromUtcNow;

            // Provenance of the throughput sample, surfaced on every projection so the UI can explain
            // an InsufficientData state (how much data is collected vs required, and whether a recent
            // definition change shortened the sample window).
            var windowStartUtc = ResolveThroughputWindowStart(nowUtc, lastDefinitionChangeUtc, opts.MaxThroughputLookback);
            var boundedByChange = lastDefinitionChangeUtc is { } changeUtc
                && changeUtc.ToUniversalTime() > nowUtc - opts.MaxThroughputLookback;

            // Span between the first and last sampled completion. The rate estimate is gated on this
            // reaching opts.MinThroughputSpan; surfaced on every projection so the collecting-data UI
            // can show the "time collected" requirement and how much longer until it clears.
            var observedSpan = throughput.FirstCompletedUtc is { } firstSeenUtc
                && throughput.LastCompletedUtc is { } lastSeenUtc
                && lastSeenUtc.ToUniversalTime() > firstSeenUtc.ToUniversalTime()
                    ? lastSeenUtc.ToUniversalTime() - firstSeenUtc.ToUniversalTime()
                    : TimeSpan.Zero;

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
                TargetFrontierUtc = targetFrontierUtc,
                ThroughputWindowStartUtc = windowStartUtc,
                ThroughputWindowBoundedByDefinitionChange = boundedByChange,
                ObservedThroughputSamples = throughput.SucceededCount,
                RequiredThroughputSamples = opts.MinThroughputSamples,
                RequiredThroughputSpan = opts.MinThroughputSpan,
                ObservedThroughputSpan = observedSpan
            };

            if (!isEnabled || definition.IsPaused || queryWindow <= TimeSpan.Zero || eligibleEndUtc <= startFromUtc)
            {
                return notApplicable;
            }

            // Backlog is eligible work this job can actually do now: total eligible slices minus
            // those already completed, minus those blocked on an upstream dependency, minus terminal
            // dead-lettered slices. Excluding dependency-blocked slices keeps a job that is merely
            // waiting on upstream (for its most recent slices) from being reported as "catching up".
            // Dead-lettered slices are terminal -- they will not run again without an operator
            // rerun/repair -- so they are treated as done; counting them would leave a job that has
            // finished everything it will do on its own permanently reported as "catching up".
            var blockedSlices = Math.Max(0, dependencyBlockedSliceCount);
            var deadLetteredSlices = Math.Max(0, deadLetteredSliceCount);
            var eligibleTotal = (eligibleEndUtc - startFromUtc).Ticks / queryWindow.Ticks;
            var backlogSlices = (int)Math.Max(0, Math.Min(int.MaxValue, eligibleTotal - completedSliceCount - deadLetteredSlices - blockedSlices));
            var backlogDataTime = TimeSpan.FromTicks(queryWindow.Ticks * backlogSlices);

            var baseProjection = new CatchUpProjection
            {
                Status = CatchUpStatus.CaughtUp,
                BacklogSlices = backlogSlices,
                BacklogDataTime = backlogDataTime,
                BacklogBlockedSlices = blockedSlices,
                CompletedFrontierUtc = completedFrontierUtc,
                TargetFrontierUtc = targetFrontierUtc,
                ThroughputWindowStartUtc = windowStartUtc,
                ThroughputWindowBoundedByDefinitionChange = boundedByChange,
                ObservedThroughputSamples = throughput.SucceededCount,
                RequiredThroughputSamples = opts.MinThroughputSamples,
                RequiredThroughputSpan = opts.MinThroughputSpan,
                ObservedThroughputSpan = observedSpan
            };

            if (backlogSlices <= opts.MinBacklogSlices)
            {
                return baseProjection;
            }

            if (throughput.SucceededCount < opts.MinThroughputSamples
                || throughput.FirstCompletedUtc is null
                || throughput.LastCompletedUtc is null)
            {
                return baseProjection with { Status = CatchUpStatus.InsufficientData };
            }

            if (observedSpan < opts.MinThroughputSpan)
            {
                return baseProjection with { Status = CatchUpStatus.InsufficientData };
            }

            // N timestamps span (N-1) intervals; this is the average completion rate over the sample.
            var completionsPerHour = (throughput.SucceededCount - 1) / observedSpan.TotalHours;
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

            // Guard against an effectively-infinite projection (rate only marginally above real time
            // with a very large backlog): constructing the TimeSpan/ETA would overflow. At that point
            // the job will not catch up in any meaningful timeframe, so report it as not keeping up.
            var maxProjectionHours = (DateTimeOffset.MaxValue - nowUtc).TotalHours;
            if (double.IsNaN(projectedHours) || projectedHours >= maxProjectionHours)
            {
                return withRate with { Status = CatchUpStatus.NotKeepingUp };
            }

            var projected = TimeSpan.FromHours(projectedHours);

            return withRate with
            {
                Status = CatchUpStatus.CatchingUp,
                ProjectedCatchUp = projected,
                EtaUtc = nowUtc + projected
            };
        }
    }
}
