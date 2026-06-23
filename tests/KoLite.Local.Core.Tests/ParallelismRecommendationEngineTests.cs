using KoLite.Local.Core.Throttling;

namespace KoLite.Local.Core.Tests
{
    public sealed class ParallelismRecommendationEngineTests
    {
        [Theory]
        // D / W * factor, ceil, floored at 1.
        [InlineData(8, 5, 1.0, 2)]   // 1.6 -> 2
        [InlineData(8, 5, 1.5, 3)]   // 2.4 -> 3
        [InlineData(0.5, 5, 1.5, 1)] // 0.15 -> 1 (floored)
        [InlineData(10, 5, 1.0, 2)]  // exactly 2.0 -> 2
        [InlineData(11, 5, 1.0, 3)]  // 2.2 -> 3
        public void Keep_up_floor_uses_ceiling_of_duration_over_window(double durationMinutes, double windowMinutes, double factor, int expected)
        {
            var floor = ParallelismRecommendationEngine.ComputeKeepUpFloor(
                TimeSpan.FromMinutes(windowMinutes),
                TimeSpan.FromMinutes(durationMinutes),
                factor);

            Assert.Equal(expected, floor);
        }

        [Fact]
        public void Keep_up_floor_is_null_without_a_duration_sample()
        {
            Assert.Null(ParallelismRecommendationEngine.ComputeKeepUpFloor(TimeSpan.FromMinutes(5), observedSliceDuration: null, 1.5));
        }

        [Fact]
        public void Over_provisioned_job_is_recommended_for_reduction_to_the_floor()
        {
            // W=5m, D=2m, factor 1.0 => floor ceil(0.4)=1; current 8 => headroom 7.
            var result = Single(Snapshot(current: 8, window: 5, duration: 2, samples: 20), factor: 1.0);

            Assert.Equal(ParallelismRecommendationStatus.Recommended, result.Status);
            Assert.Equal(1, result.KeepUpFloor);
            Assert.Equal(1, result.RecommendedMaxParallelism);
            Assert.Equal(7, result.Headroom);
            Assert.Equal(288d, result.SlicesPerDay, 3);
        }

        [Fact]
        public void Job_at_the_floor_is_not_recommended_for_reduction()
        {
            // W=5m, D=8m, factor 1.0 => floor ceil(1.6)=2; current 2 => headroom 0.
            var result = Single(Snapshot(current: 2, window: 5, duration: 8, samples: 20), factor: 1.0);

            Assert.Equal(ParallelismRecommendationStatus.AtOrBelowKeepUpFloor, result.Status);
            Assert.Equal(2, result.KeepUpFloor);
            Assert.Null(result.RecommendedMaxParallelism);
            Assert.Equal(0, result.Headroom);
        }

        [Fact]
        public void Job_below_the_floor_is_flagged_not_reduced()
        {
            // W=5m, D=20m, factor 1.0 => floor ceil(4)=4; current 1 => headroom -3.
            var result = Single(Snapshot(current: 1, window: 5, duration: 20, samples: 20), factor: 1.0);

            Assert.Equal(ParallelismRecommendationStatus.AtOrBelowKeepUpFloor, result.Status);
            Assert.Equal(4, result.KeepUpFloor);
            Assert.Null(result.RecommendedMaxParallelism);
            Assert.Equal(-3, result.Headroom);
        }

        [Fact]
        public void Job_without_duration_data_is_insufficient_data()
        {
            var result = Single(Snapshot(current: 8, window: 5, duration: null, samples: 0), factor: 1.5);

            Assert.Equal(ParallelismRecommendationStatus.InsufficientData, result.Status);
            Assert.Null(result.KeepUpFloor);
            Assert.Null(result.RecommendedMaxParallelism);
            Assert.Null(result.Headroom);
        }

        [Fact]
        public void Recommendations_rank_actionable_reductions_by_headroom_then_others()
        {
            var snapshots = new[]
            {
                Snapshot("at-floor", current: 2, window: 5, duration: 8, samples: 20),   // floor 2, headroom 0
                Snapshot("no-data", current: 9, window: 5, duration: null, samples: 0),  // insufficient
                Snapshot("small-headroom", current: 3, window: 5, duration: 2, samples: 20), // floor 1, headroom 2
                Snapshot("big-headroom", current: 12, window: 5, duration: 2, samples: 20),  // floor 1, headroom 11
            };

            var ranked = ParallelismRecommendationEngine.Recommend(snapshots, Options(1.0));

            Assert.Collection(
                ranked,
                r => Assert.Equal("big-headroom", r.ActivityId),
                r => Assert.Equal("small-headroom", r.ActivityId),
                r => Assert.Equal("at-floor", r.ActivityId),
                r => Assert.Equal("no-data", r.ActivityId));

            Assert.Equal(ParallelismRecommendationStatus.Recommended, ranked[0].Status);
            Assert.Equal(ParallelismRecommendationStatus.Recommended, ranked[1].Status);
            Assert.Equal(ParallelismRecommendationStatus.AtOrBelowKeepUpFloor, ranked[2].Status);
            Assert.Equal(ParallelismRecommendationStatus.InsufficientData, ranked[3].Status);
        }

        private static ParallelismRecommendation Single(JobThrottleSnapshot snapshot, double factor) =>
            Assert.Single(ParallelismRecommendationEngine.Recommend(new[] { snapshot }, Options(factor)));

        private static ParallelismRecommendationOptions Options(double factor) =>
            new() { KeepUpSafetyFactor = factor };

        private static JobThrottleSnapshot Snapshot(double current, double window, double? duration, int samples) =>
            Snapshot("job", current, window, duration, samples);

        private static JobThrottleSnapshot Snapshot(string activityId, double current, double window, double? duration, int samples) =>
            new(
                JobId: activityId + "-id",
                ActivityId: activityId,
                ClusterUri: "https://sample-data.centralus.kusto.windows.net",
                CurrentMaxParallelism: (int)current,
                QueryWindowSize: TimeSpan.FromMinutes(window),
                InFlightCount: 1,
                ObservedSliceDuration: duration is { } d ? TimeSpan.FromMinutes(d) : null,
                DurationSampleCount: samples);
    }
}
