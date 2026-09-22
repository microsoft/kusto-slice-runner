// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Schedules;
using Ksr.Local.Core.Time;

namespace Ksr.Local.Core.Scheduling
{
    public static class SliceEnumerator
    {
        public static IReadOnlyList<SliceRange> EnumerateEligible(JobDefinition job, IClock clock) =>
            Enumerate(RequireJobId(job), job.StartFrom, EligibleEnd(job, clock.UtcNow), job.QueryWindowSize).ToArray();

        public static IEnumerable<SliceRange> Enumerate(string jobId, DateTimeOffset startFromUtc, DateTimeOffset eligibleEndUtc, TimeSpan queryWindowSize)
        {
            if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("Job id is required.", nameof(jobId));
            if (queryWindowSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(queryWindowSize), "Query window size must be positive.");
            startFromUtc = startFromUtc.ToUniversalTime();
            eligibleEndUtc = eligibleEndUtc.ToUniversalTime();
            if (eligibleEndUtc <= startFromUtc) yield break;

            var wholeWindowCount = (eligibleEndUtc - startFromUtc).Ticks / queryWindowSize.Ticks;
            var alignedEnd = startFromUtc + TimeSpan.FromTicks(wholeWindowCount * queryWindowSize.Ticks);
            for (var cursor = startFromUtc; cursor < alignedEnd; cursor += queryWindowSize)
            {
                yield return new SliceRange(jobId, cursor, cursor + queryWindowSize);
            }
        }

        private static string RequireJobId(JobDefinition job) =>
            string.IsNullOrWhiteSpace(job.Id)
                ? throw new InvalidOperationException($"Job '{job.ActivityId}' has no durable id; slices cannot be enumerated.")
                : job.Id!;

        private static DateTimeOffset EligibleEnd(JobDefinition job, DateTimeOffset utcNow)
        {
            var delayedEnd = utcNow.ToUniversalTime() - job.DelayFromUtcNow;
            return job.EndOn is { } endOn && endOn < delayedEnd ? endOn : delayedEnd;
        }
    }
}
