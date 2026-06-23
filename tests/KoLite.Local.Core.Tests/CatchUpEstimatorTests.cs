using System.Globalization;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Core.Tests
{
    public sealed class CatchUpEstimatorTests
    {
        [Fact]
        public void Caught_up_job_with_no_backlog_is_not_displayed()
        {
            // now == startFrom + 12h, delay 0 => 12 eligible 1h slices, all complete.
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-01T12:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-01T12:00:00Z"),
                completedSliceCount: 12,
                Sample(count: 11, first: "2026-01-01T10:00:00Z", last: "2026-01-01T12:00:00Z"));

            Assert.Equal(CatchUpStatus.CaughtUp, projection.Status);
            Assert.False(projection.ShouldDisplay);
            Assert.Equal(0, projection.BacklogSlices);
        }

        [Fact]
        public void Single_eligible_slice_below_floor_is_not_displayed()
        {
            // 12 eligible slices, 11 complete => backlog of 1 (<= MinBacklogSlices).
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-01T12:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-01T11:00:00Z"),
                completedSliceCount: 11,
                Sample(count: 11, first: "2026-01-01T10:00:00Z", last: "2026-01-01T12:00:00Z"));

            Assert.Equal(CatchUpStatus.CaughtUp, projection.Status);
            Assert.Equal(1, projection.BacklogSlices);
            Assert.False(projection.ShouldDisplay);
        }

        [Fact]
        public void Large_backlog_with_fast_rate_projects_catch_up_time()
        {
            // 240 eligible 1h slices, 200 complete => 40h backlog. Rate R = 5 data-h/wall-h.
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 11, first: "2026-01-10T20:00:00Z", last: "2026-01-10T22:00:00Z"));

            Assert.Equal(CatchUpStatus.CatchingUp, projection.Status);
            Assert.True(projection.ShouldDisplay);
            Assert.Equal(40, projection.BacklogSlices);
            Assert.Equal(TimeSpan.FromHours(40), projection.BacklogDataTime);
            Assert.Equal(5.0, projection.RealTimeMultiple!.Value, 6);
            Assert.Equal(5.0, projection.SlicesPerHour!.Value, 6);
            Assert.Equal(TimeSpan.FromHours(10), projection.ProjectedCatchUp);
            Assert.Equal(Utc("2026-01-11T10:00:00Z"), projection.EtaUtc);
        }

        [Fact]
        public void Large_backlog_with_rate_at_or_below_real_time_is_not_keeping_up()
        {
            // R = 1 exactly (10 intervals over 10h, 1h window) => never catches up.
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 11, first: "2026-01-10T12:00:00Z", last: "2026-01-10T22:00:00Z"));

            Assert.Equal(CatchUpStatus.NotKeepingUp, projection.Status);
            Assert.True(projection.ShouldDisplay);
            Assert.Equal(1.0, projection.RealTimeMultiple!.Value, 6);
            Assert.Null(projection.ProjectedCatchUp);
            Assert.Null(projection.EtaUtc);
        }

        [Fact]
        public void Backlog_that_clears_quickly_is_negligible()
        {
            // 12 eligible 5m slices, 6 complete => 30m backlog. R = 10 => ~3.3m to clear.
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-01T01:00:00Z"),
                Job(window: TimeSpan.FromMinutes(5), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-01T00:30:00Z"),
                completedSliceCount: 6,
                Sample(count: 61, first: "2026-01-01T00:30:00Z", last: "2026-01-01T01:00:00Z"));

            Assert.Equal(CatchUpStatus.Negligible, projection.Status);
            Assert.False(projection.ShouldDisplay);
        }

        [Fact]
        public void Too_few_completions_yields_insufficient_data()
        {
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 2, first: "2026-01-10T20:00:00Z", last: "2026-01-10T22:00:00Z"));

            Assert.Equal(CatchUpStatus.InsufficientData, projection.Status);
            Assert.True(projection.ShouldDisplay);
            Assert.Equal(40, projection.BacklogSlices);
            Assert.Null(projection.RealTimeMultiple);
            // No definition change supplied: the sample window is the recent lookback floor.
            Assert.False(projection.ThroughputWindowBoundedByDefinitionChange);
            Assert.Equal(Utc("2026-01-10T18:00:00Z"), projection.ThroughputWindowStartUtc);
            Assert.Equal(2, projection.ObservedThroughputSamples);
            Assert.Equal(3, projection.RequiredThroughputSamples);
            Assert.Equal(TimeSpan.FromMinutes(10), projection.RequiredThroughputSpan);
        }

        [Fact]
        public void Sample_span_below_minimum_yields_insufficient_data()
        {
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 5, first: "2026-01-10T21:55:00Z", last: "2026-01-10T22:00:00Z"));

            Assert.Equal(CatchUpStatus.InsufficientData, projection.Status);
            Assert.True(projection.ShouldDisplay);
            Assert.Equal(5, projection.ObservedThroughputSamples);
        }

        [Fact]
        public void Insufficient_data_after_recent_definition_change_is_bounded_by_change()
        {
            // A definition change 1h ago shortens the sample window to start at the change, so only a
            // couple of completions exist under the new definition => InsufficientData, still shown.
            var projection = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 2, first: "2026-01-10T23:10:00Z", last: "2026-01-10T23:40:00Z"),
                lastDefinitionChangeUtc: Utc("2026-01-10T23:00:00Z"));

            Assert.Equal(CatchUpStatus.InsufficientData, projection.Status);
            Assert.True(projection.ShouldDisplay);
            Assert.True(projection.ThroughputWindowBoundedByDefinitionChange);
            Assert.Equal(Utc("2026-01-10T23:00:00Z"), projection.ThroughputWindowStartUtc);
            Assert.Equal(2, projection.ObservedThroughputSamples);
        }

        [Fact]
        public void Paused_or_disabled_job_is_not_applicable()
        {
            var paused = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero, paused: true),
                isEnabled: true,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 11, first: "2026-01-10T20:00:00Z", last: "2026-01-10T22:00:00Z"));

            var disabled = CatchUpEstimator.Estimate(
                Utc("2026-01-11T00:00:00Z"),
                Job(window: TimeSpan.FromHours(1), delay: TimeSpan.Zero),
                isEnabled: false,
                completedFrontierUtc: Utc("2026-01-09T08:00:00Z"),
                completedSliceCount: 200,
                Sample(count: 11, first: "2026-01-10T20:00:00Z", last: "2026-01-10T22:00:00Z"));

            Assert.Equal(CatchUpStatus.NotApplicable, paused.Status);
            Assert.Equal(CatchUpStatus.NotApplicable, disabled.Status);
        }

        [Fact]
        public void Throughput_window_uses_recent_definition_change_when_within_lookback()
        {
            var start = CatchUpEstimator.ResolveThroughputWindowStart(
                Utc("2026-01-01T12:00:00Z"),
                lastDefinitionChangeUtc: Utc("2026-01-01T09:00:00Z"),
                maxLookback: TimeSpan.FromHours(6));

            Assert.Equal(Utc("2026-01-01T09:00:00Z"), start);
        }

        [Fact]
        public void Throughput_window_falls_back_to_lookback_when_change_is_old_or_absent()
        {
            var withOldChange = CatchUpEstimator.ResolveThroughputWindowStart(
                Utc("2026-01-01T12:00:00Z"),
                lastDefinitionChangeUtc: Utc("2026-01-01T01:00:00Z"),
                maxLookback: TimeSpan.FromHours(6));

            var withNoChange = CatchUpEstimator.ResolveThroughputWindowStart(
                Utc("2026-01-01T12:00:00Z"),
                lastDefinitionChangeUtc: null,
                maxLookback: TimeSpan.FromHours(6));

            Assert.Equal(Utc("2026-01-01T06:00:00Z"), withOldChange);
            Assert.Equal(Utc("2026-01-01T06:00:00Z"), withNoChange);
        }

        private static CatchUpThroughputSample Sample(int count, string first, string last) =>
            new(count, Utc(first), Utc(last));

        private static JobDefinition Job(TimeSpan window, TimeSpan delay, bool paused = false) => new()
        {
            Id = "catchup",
            ActivityId = "catchup",
            FunctionName = "Fn",
            OutputTable = "Output",
            QueryWindowSize = window,
            DelayFromUtcNow = delay,
            MaxParallelism = 4,
            QueryTimeout = TimeSpan.FromMinutes(20),
            StartFrom = Utc("2026-01-01T00:00:00Z"),
            IsPaused = paused,
            Target = new JobTarget { ClusterUri = "https://kolite-example.invalid", Database = "DemoDb" }
        };

        private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }
}
