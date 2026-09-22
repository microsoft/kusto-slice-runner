// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Schedules;
using Ksr.Local.Core.Scheduling;

namespace Ksr.Local.Core.Tests
{
    public sealed class JobHealthEvaluatorTests
    {
        [Fact]
        public void No_recent_activity_is_healthy()
        {
            var health = JobHealthEvaluator.Evaluate(Array.Empty<string?>(), JobHealthPolicy.Complete, 0);

            Assert.Equal(JobHealthTier.Healthy, health.Tier);
            Assert.Equal(0, health.RecentConsidered);
        }

        [Fact]
        public void All_recent_succeeded_is_healthy()
        {
            var health = JobHealthEvaluator.Evaluate(Repeat("Completed", 10), JobHealthPolicy.Complete, 0);

            Assert.Equal(JobHealthTier.Healthy, health.Tier);
            Assert.Equal(10, health.RecentConsidered);
            Assert.Equal(10, health.RecentSucceeded);
            Assert.Equal(0, health.RecentFailed);
        }

        [Fact]
        public void Sustained_recent_failure_streak_is_attention()
        {
            var health = JobHealthEvaluator.Evaluate(Repeat("Failed", 10), JobHealthPolicy.Recent, 0);

            Assert.Equal(JobHealthTier.Attention, health.Tier);
            Assert.Equal(10, health.RecentFailed);
        }

        [Fact]
        public void Recent_deadletters_dominating_the_window_is_attention()
        {
            // newest-first: latest is a dead-letter, 6 of 10 considered failed -> broken now.
            var states = new string?[] { "DeadLettered", "DeadLettered", "Completed", "DeadLettered", "Completed", "DeadLettered", "DeadLettered", "Completed", "DeadLettered", "Completed" };

            var health = JobHealthEvaluator.Evaluate(states, JobHealthPolicy.Complete, 6);

            Assert.Equal(JobHealthTier.Attention, health.Tier);
        }

        [Fact]
        public void Healthy_now_with_a_single_recent_failure_is_borderline()
        {
            // Latest slice succeeded; one older failure inside the window -> watch, not broken.
            var states = new string?[] { "Completed", "Completed", "Failed", "Completed", "Completed", "Completed", "Completed", "Completed", "Completed", "Completed" };

            var health = JobHealthEvaluator.Evaluate(states, JobHealthPolicy.Recent, 0);

            Assert.Equal(JobHealthTier.Borderline, health.Tier);
            Assert.Equal(1, health.RecentFailed);
        }

        [Fact]
        public void Latest_failure_below_ratio_is_borderline_not_attention()
        {
            // Latest considered failed, but only 1 of 10 failed (ratio 0.1) -> borderline.
            var states = new string?[] { "Failed", "Completed", "Completed", "Completed", "Completed", "Completed", "Completed", "Completed", "Completed", "Completed" };

            var health = JobHealthEvaluator.Evaluate(states, JobHealthPolicy.Complete, 1);

            Assert.Equal(JobHealthTier.Borderline, health.Tier);
        }

        [Fact]
        public void Pending_states_are_ignored_and_latest_considered_drives_attention()
        {
            // Newest entries are pending (Running/Queued) and must be skipped; the latest resolved
            // slice failed and the resolved window is majority failure -> attention.
            var states = new string?[] { "Running", "Queued", "Failed", "Failed", "Failed", "Completed", "DependencyBlocked" };

            var health = JobHealthEvaluator.Evaluate(states, JobHealthPolicy.Recent, 0);

            Assert.Equal(JobHealthTier.Attention, health.Tier);
            Assert.Equal(4, health.RecentConsidered);
            Assert.Equal(3, health.RecentFailed);
            Assert.Equal(1, health.RecentSucceeded);
        }

        [Fact]
        public void Complete_policy_reports_gaps_even_when_recent_is_healthy()
        {
            var health = JobHealthEvaluator.Evaluate(Repeat("Completed", 10), JobHealthPolicy.Complete, 4);

            Assert.Equal(JobHealthTier.Healthy, health.Tier);
            Assert.True(health.TracksCompleteness);
            Assert.True(health.HasGaps);
            Assert.Equal(4, health.GapCount);
        }

        [Fact]
        public void Recent_policy_never_reports_gaps()
        {
            var health = JobHealthEvaluator.Evaluate(Repeat("Completed", 10), JobHealthPolicy.Recent, 12);

            Assert.False(health.TracksCompleteness);
            Assert.False(health.HasGaps);
            Assert.Equal(12, health.GapCount);
        }

        private static string?[] Repeat(string state, int count)
        {
            var result = new string?[count];
            Array.Fill(result, state);
            return result;
        }
    }
}
