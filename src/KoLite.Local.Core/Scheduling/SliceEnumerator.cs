using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Time;

namespace KoLite.Local.Core.Scheduling
{
    public static class SliceEnumerator
    {
        public static IReadOnlyList<SliceRange> EnumerateEligible(JobDefinition job, IClock clock) =>
            Enumerate(job.ActivityId, job.StartFrom, EligibleEnd(job, clock.UtcNow), job.QueryWindowSize).ToArray();

        public static IEnumerable<SliceRange> Enumerate(string activityId, DateTimeOffset startFromUtc, DateTimeOffset eligibleEndUtc, TimeSpan queryWindowSize)
        {
            if (string.IsNullOrWhiteSpace(activityId)) throw new ArgumentException("Activity id is required.", nameof(activityId));
            if (queryWindowSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(queryWindowSize), "Query window size must be positive.");
            startFromUtc = startFromUtc.ToUniversalTime();
            eligibleEndUtc = eligibleEndUtc.ToUniversalTime();
            if (eligibleEndUtc <= startFromUtc) yield break;

            var wholeWindowCount = (eligibleEndUtc - startFromUtc).Ticks / queryWindowSize.Ticks;
            var alignedEnd = startFromUtc + TimeSpan.FromTicks(wholeWindowCount * queryWindowSize.Ticks);
            for (var cursor = startFromUtc; cursor < alignedEnd; cursor += queryWindowSize)
            {
                yield return new SliceRange(activityId, cursor, cursor + queryWindowSize);
            }
        }

        private static DateTimeOffset EligibleEnd(JobDefinition job, DateTimeOffset utcNow)
        {
            var delayedEnd = utcNow.ToUniversalTime() - job.DelayFromUtcNow;
            return job.EndOn is { } endOn && endOn < delayedEnd ? endOn : delayedEnd;
        }
    }
}
