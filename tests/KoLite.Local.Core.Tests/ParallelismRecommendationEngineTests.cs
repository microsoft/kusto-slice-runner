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

        [Theory]
        // catchUpFloor = ceil((D/W) * (1 + B/T) * factor).
        // W=5m, D=5m => D/W=1. B=10h, T=10h => (1 + 1) = 2 => ceil(2) = 2.
        [InlineData(5, 5, 10, 10, 1.0, 2)]
        // W=5m, D=2m => 0.4. B=48h, T=24h => 0.4*(1+2)=1.2 => ceil 2.
        [InlineData(2, 5, 48, 24, 1.0, 2)]
        // Safety factor scales the floor: 0.4*(1+2)*1.5 = 1.8 => ceil 2.
        [InlineData(2, 5, 48, 24, 1.5, 2)]
        // No backlog => null (the keep-up floor governs instead).
        [InlineData(5, 5, 0, 10, 1.0, 0)]
        public void Catch_up_floor_sizes_to_clear_backlog_within_target(double durationMinutes, double windowMinutes, double backlogHours, double targetHours, double factor, int expected)
        {
            var floor = ParallelismRecommendationEngine.ComputeCatchUpFloor(
                TimeSpan.FromMinutes(windowMinutes),
                TimeSpan.FromMinutes(durationMinutes),
                TimeSpan.FromHours(backlogHours),
                TimeSpan.FromHours(targetHours),
                factor);

            if (expected == 0)
            {
                Assert.Null(floor);
            }
            else
            {
                Assert.Equal(expected, floor);
            }
        }

        [Fact]
        public void Catch_up_eta_scales_inversely_with_parallelism()
        {
            // W=5m, D=5m => R(P) = P. B=10h. ETA(P) = B / (R-1) = 10h / (P-1).
            var window = TimeSpan.FromMinutes(5);
            var duration = TimeSpan.FromMinutes(5);
            var backlog = TimeSpan.FromHours(10);

            Assert.Equal(TimeSpan.FromHours(10), ParallelismRecommendationEngine.ComputeCatchUpEta(window, duration, backlog, parallelism: 2));
            Assert.Equal(TimeSpan.FromHours(2.5), ParallelismRecommendationEngine.ComputeCatchUpEta(window, duration, backlog, parallelism: 5));
            // R(P) <= 1 never catches up => null.
            Assert.Null(ParallelismRecommendationEngine.ComputeCatchUpEta(window, duration, backlog, parallelism: 1));
        }

        [Fact]
        public void Catch_up_eta_is_null_when_the_projection_would_overflow()
        {
            // Rate only marginally above real time (R just over 1) with an enormous backlog: the raw
            // ETA exceeds TimeSpan.MaxValue and must be reported as null ("won't catch up") not throw.
            // W=5m, D≈5m so R(1) is just above 1; backlog ~100 years of data-time.
            var window = TimeSpan.FromMinutes(5);
            var duration = TimeSpan.FromTicks(window.Ticks - 1);
            var backlog = TimeSpan.FromDays(365 * 100);

            Assert.Null(ParallelismRecommendationEngine.ComputeCatchUpEta(window, duration, backlog, parallelism: 1));
        }

        [Fact]
        public void Backfilling_job_is_recommended_to_the_catch_up_floor_not_the_keep_up_floor()
        {
            // W=5m, D=5m, factor 1.0 => keep-up floor 1. Backlog 10h, target 10h => catch-up floor 2.
            // Current 8 => suggested 2 (not 1), headroom 6, ETA 8 -> 2 shown.
            var result = Single(BackfillSnapshot(current: 8, window: 5, duration: 5, backlogHours: 10), factor: 1.0, targetHours: 10);

            Assert.Equal(ParallelismRecommendationStatus.Recommended, result.Status);
            Assert.True(result.IsBackfilling);
            Assert.Equal(1, result.KeepUpFloor);
            Assert.Equal(2, result.CatchUpFloor);
            Assert.Equal(2, result.RecommendedMaxParallelism);
            Assert.Equal(6, result.Headroom);
            Assert.NotNull(result.CurrentCatchUpEta);
            Assert.NotNull(result.ProjectedCatchUpEta);
            // ETA(P) = B / (R-1), R = P*W/D. Current P=8 => 10h/7; suggested P=2 => 10h/1.
            Assert.True(Math.Abs((result.CurrentCatchUpEta.Value - TimeSpan.FromHours(10.0 / 7)).TotalSeconds) < 1);
            Assert.True(Math.Abs((result.ProjectedCatchUpEta.Value - TimeSpan.FromHours(10)).TotalSeconds) < 1);
        }

        [Fact]
        public void Backfilling_job_already_at_catch_up_floor_is_not_reduced()
        {
            // Catch-up floor 2; current 2 => no reduction even though the keep-up floor is 1.
            var result = Single(BackfillSnapshot(current: 2, window: 5, duration: 5, backlogHours: 10), factor: 1.0, targetHours: 10);

            Assert.Equal(ParallelismRecommendationStatus.AtOrBelowKeepUpFloor, result.Status);
            Assert.True(result.IsBackfilling);
            Assert.Equal(2, result.CatchUpFloor);
            Assert.Null(result.RecommendedMaxParallelism);
            Assert.Equal(0, result.Headroom);
        }

        private static ParallelismRecommendation Single(JobThrottleSnapshot snapshot, double factor) =>
            Assert.Single(ParallelismRecommendationEngine.Recommend(new[] { snapshot }, Options(factor)));

        private static ParallelismRecommendation Single(JobThrottleSnapshot snapshot, double factor, double targetHours) =>
            Assert.Single(ParallelismRecommendationEngine.Recommend(
                new[] { snapshot },
                new ParallelismRecommendationOptions { KeepUpSafetyFactor = factor, CatchUpTargetDuration = TimeSpan.FromHours(targetHours) }));

        private static ParallelismRecommendationOptions Options(double factor) =>
            new() { KeepUpSafetyFactor = factor };

        private static JobThrottleSnapshot Snapshot(double current, double window, double? duration, int samples) =>
            Snapshot("job", current, window, duration, samples);

        private static JobThrottleSnapshot BackfillSnapshot(double current, double window, double duration, double backlogHours)
        {
            var queryWindow = TimeSpan.FromMinutes(window);
            var backlogDataTime = TimeSpan.FromHours(backlogHours);
            var backlogSlices = (int)(backlogDataTime.Ticks / queryWindow.Ticks);
            return new JobThrottleSnapshot(
                JobId: "backfill-id",
                ActivityId: "backfill",
                ClusterUri: "https://sample-data.centralus.kusto.windows.net",
                CurrentMaxParallelism: (int)current,
                CatalogVersion: 1,
                QueryWindowSize: queryWindow,
                InFlightCount: (int)current,
                ObservedSliceDuration: TimeSpan.FromMinutes(duration),
                DurationSampleCount: 20,
                BacklogSlices: backlogSlices,
                BacklogDataTime: backlogDataTime,
                IsBackfilling: true);
        }

        private static JobThrottleSnapshot Snapshot(string activityId, double current, double window, double? duration, int samples) =>
            new(
                JobId: activityId + "-id",
                ActivityId: activityId,
                ClusterUri: "https://sample-data.centralus.kusto.windows.net",
                CurrentMaxParallelism: (int)current,
                CatalogVersion: 1,
                QueryWindowSize: TimeSpan.FromMinutes(window),
                InFlightCount: 1,
                ObservedSliceDuration: duration is { } d ? TimeSpan.FromMinutes(d) : null,
                DurationSampleCount: samples);
    }
}
