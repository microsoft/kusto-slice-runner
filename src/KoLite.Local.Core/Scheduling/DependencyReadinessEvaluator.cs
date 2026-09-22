// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Schedules;

namespace KoLite.Local.Core.Scheduling
{
    public sealed record DependencyReadiness(bool IsReady, IReadOnlyList<SliceKey> MissingSlices)
    {
        public static DependencyReadiness Ready { get; } = new(true, Array.Empty<SliceKey>());
    }

    public static class DependencyReadinessEvaluator
    {
        public static DependencyReadiness Evaluate(
            JobDefinition downstream,
            SliceRange downstreamSlice,
            IReadOnlyDictionary<string, JobDefinition> jobsById,
            IReadOnlySet<SliceKey> completedSlices)
        {
            var missing = new List<SliceKey>();
            foreach (var dependency in downstream.DependsOn)
            {
                var upstreamId = dependency.Id;
                if (string.IsNullOrWhiteSpace(upstreamId) || !jobsById.TryGetValue(upstreamId, out var upstream))
                {
                    var reference = string.IsNullOrWhiteSpace(upstreamId) ? dependency.ActivityId ?? "<unknown>" : upstreamId;
                    missing.Add(SliceKey.Create(reference, downstreamSlice.StartUtc, downstreamSlice.EndUtc));
                    continue;
                }

                var required = RequiredUpstreamSlices(upstream, downstreamSlice);
                missing.AddRange(required.Where(slice => !completedSlices.Contains(slice.ToKey())).Select(slice => slice.ToKey()));
            }

            return missing.Count == 0 ? DependencyReadiness.Ready : new DependencyReadiness(false, missing);
        }

        public static IReadOnlyList<SliceRange> RequiredUpstreamSlices(JobDefinition upstream, SliceRange downstreamSlice)
        {
            if (string.IsNullOrWhiteSpace(upstream.Id)) throw new ArgumentException("Upstream job id is required.", nameof(upstream));
            if (upstream.QueryWindowSize <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(upstream), "Query window size must be positive.");

            var upstreamStart = upstream.StartFrom.ToUniversalTime();
            var downstreamStart = downstreamSlice.StartUtc.ToUniversalTime();
            var downstreamEnd = downstreamSlice.EndUtc.ToUniversalTime();
            if (downstreamEnd <= upstreamStart)
            {
                return Array.Empty<SliceRange>();
            }

            var windowTicks = upstream.QueryWindowSize.Ticks;
            var firstWindowIndex = downstreamStart <= upstreamStart ? 0 : (downstreamStart - upstreamStart).Ticks / windowTicks;
            var firstWindowStart = upstreamStart + TimeSpan.FromTicks(firstWindowIndex * windowTicks);

            var required = new List<SliceRange>();
            for (var cursor = firstWindowStart; cursor < downstreamEnd; cursor += upstream.QueryWindowSize)
            {
                required.Add(new SliceRange(upstream.Id!, cursor, cursor + upstream.QueryWindowSize));
            }

            return required;
        }
    }
}
