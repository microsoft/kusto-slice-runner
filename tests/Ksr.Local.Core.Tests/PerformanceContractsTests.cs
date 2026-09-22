// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Performance;

namespace Ksr.Local.Core.Tests
{
    public sealed class PerformanceContractsTests
    {
        [Theory]
        [InlineData("Succeeded", true)]
        [InlineData("FailedRetryable", true)]
        [InlineData("Failed", true)]
        [InlineData("DeadLettered", true)]
        [InlineData("LeaseLost", true)]
        [InlineData("Started", false)]
        [InlineData("Completed", false)]
        [InlineData("Unknown", false)]
        [InlineData(null, false)]
        public void Only_known_completed_attempt_outcomes_are_included(string? status, bool included)
        {
            Assert.Equal(included, PerformanceOutcomes.IsCompleted(status));
        }

        [Fact]
        public void Success_percentage_counts_attempts_instead_of_logical_windows()
        {
            var row = Row(17, 16);

            Assert.Equal(16 * 100d / 17, row.SuccessPercent);
            Assert.Null(Row(0, 0).SuccessPercent);
            Assert.Equal(100, Row(long.MaxValue, long.MaxValue).SuccessPercent);
        }

        private static PerformanceAggregateRow Row(long attempts, long successes) =>
            new("job", null, true, attempts, successes,
                PerformancePercentiles.Empty, PerformancePercentiles.Empty, PerformancePercentiles.Empty);
    }
}
