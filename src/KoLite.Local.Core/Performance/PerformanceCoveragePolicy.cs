namespace KoLite.Local.Core.Performance
{
    public sealed record PerformanceCoverageCounts
    {
        public static PerformanceCoverageCounts Empty { get; } = new(0, 0);

        public PerformanceCoverageCounts(long eligibleAttempts, long missingAttempts)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(eligibleAttempts);
            ArgumentOutOfRangeException.ThrowIfNegative(missingAttempts);
            if (missingAttempts > eligibleAttempts)
            {
                throw new ArgumentOutOfRangeException(nameof(missingAttempts), "Missing attempts cannot exceed eligible attempts.");
            }

            EligibleAttempts = eligibleAttempts;
            MissingAttempts = missingAttempts;
        }

        public long EligibleAttempts { get; }
        public long MissingAttempts { get; }
        public decimal? MissingPercent => EligibleAttempts == 0 ? null : MissingAttempts * 100m / EligibleAttempts;

        public static PerformanceCoverageCounts Sum(IEnumerable<PerformanceCoverageCounts> counts)
        {
            ArgumentNullException.ThrowIfNull(counts);
            long eligible = 0;
            long missing = 0;
            foreach (var count in counts)
            {
                ArgumentNullException.ThrowIfNull(count);
                eligible = checked(eligible + count.EligibleAttempts);
                missing = checked(missing + count.MissingAttempts);
            }

            return new PerformanceCoverageCounts(eligible, missing);
        }
    }

    public static class PerformanceCoveragePolicy
    {
        public const int ThresholdPercent = 20;
        public const int MinimumMissingAttempts = 5;
        public const int GracePeriodMinutes = 5;
        public static TimeSpan GracePeriod { get; } = TimeSpan.FromMinutes(GracePeriodMinutes);

        public static bool ShouldWarn(PerformanceCoverageCounts coverage)
        {
            ArgumentNullException.ThrowIfNull(coverage);
            return coverage.EligibleAttempts > 0
                && coverage.MissingAttempts >= MinimumMissingAttempts
                && coverage.MissingAttempts * 100m >= coverage.EligibleAttempts * (decimal)ThresholdPercent;
        }
    }
}
