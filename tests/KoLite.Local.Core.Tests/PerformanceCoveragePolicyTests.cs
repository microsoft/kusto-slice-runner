using KoLite.Local.Core.Performance;

namespace KoLite.Local.Core.Tests
{
    public sealed class PerformanceCoveragePolicyTests
    {
        [Theory]
        [InlineData(0, 0, false)]
        [InlineData(3, 3, false)]
        [InlineData(4, 4, false)]
        [InlineData(20, 4, false)]
        [InlineData(26, 5, false)]
        [InlineData(25, 5, true)]
        [InlineData(25, 6, true)]
        [InlineData(5, 5, true)]
        [InlineData(10000, 1999, false)]
        [InlineData(10000, 2000, true)]
        [InlineData(9007199254740995L, 1801439850948198L, false)]
        [InlineData(9007199254740995L, 1801439850948199L, true)]
        [InlineData(long.MaxValue, long.MaxValue, true)]
        public void Warning_requires_both_exact_percentage_and_minimum_missing_volume(long eligible, long missing, bool expected)
        {
            Assert.Equal(expected, PerformanceCoveragePolicy.ShouldWarn(new PerformanceCoverageCounts(eligible, missing)));
        }

        [Fact]
        public void Counts_are_weighted_instead_of_averaging_job_percentages()
        {
            var combined = PerformanceCoverageCounts.Sum([new(5, 5), new(95, 0)]);

            Assert.Equal(new PerformanceCoverageCounts(100, 5), combined);
            Assert.Equal(5m, combined.MissingPercent);
            Assert.False(PerformanceCoveragePolicy.ShouldWarn(combined));
            Assert.Null(PerformanceCoverageCounts.Empty.MissingPercent);
            Assert.Equal(PerformanceCoverageCounts.Empty, PerformanceCoverageCounts.Sum([]));
            Assert.Equal(TimeSpan.FromMinutes(5), PerformanceCoveragePolicy.GracePeriod);
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(1, -1)]
        [InlineData(1, 2)]
        public void Invalid_counts_cannot_be_treated_as_healthy(long eligible, long missing)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PerformanceCoverageCounts(eligible, missing));
        }

        [Fact]
        public void Summing_counts_does_not_silently_overflow()
        {
            Assert.Throws<OverflowException>(() =>
                PerformanceCoverageCounts.Sum([new(long.MaxValue, 0), new(1, 0)]));
        }
    }
}
