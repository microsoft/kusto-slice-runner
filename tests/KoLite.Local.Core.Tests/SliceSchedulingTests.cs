using System.Globalization;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;

namespace KoLite.Local.Core.Tests
{
    public sealed class SliceSchedulingTests
    {
        [Fact]
        public void Slice_key_round_trips_and_sorts_by_activity_then_window()
        {
            var aLater = SliceKey.Create("a", Utc("2024-01-01T02:00:00Z"), Utc("2024-01-01T03:00:00Z"));
            var bFirst = SliceKey.Create("b", Utc("2024-01-01T00:00:00Z"), Utc("2024-01-01T01:00:00Z"));
            var aFirst = SliceKey.Create("a", Utc("2024-01-01T00:00:00Z"), Utc("2024-01-01T01:00:00Z"));

            Assert.True(SliceKey.TryParse(aFirst.Value, out var parsed, out var range));
            Assert.Equal(aFirst, parsed);
            Assert.Equal("a", range.ActivityId);
            Assert.Equal(Utc("2024-01-01T00:00:00Z"), range.StartUtc);
            Assert.Equal(Utc("2024-01-01T01:00:00Z"), range.EndUtc);

            var sorted = new[] { bFirst, aLater, aFirst }.Order().ToArray();
            Assert.Equal(new[] { aFirst, aLater, bFirst }, sorted);
        }

        [Fact]
        public void Slice_enumeration_respects_delay_endOn_and_whole_utc_boundaries()
        {
            var job = Job("demo", start: "2024-01-01T00:30:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.FromMinutes(15), endOn: "2024-01-01T03:45:00Z");
            var clock = new ManualClock(Utc("2024-01-01T05:10:00Z"));

            var slices = SliceEnumerator.EnumerateEligible(job, clock);

            Assert.Equal(3, slices.Count);
            Assert.Equal(Utc("2024-01-01T00:30:00Z"), slices[0].StartUtc);
            Assert.Equal(Utc("2024-01-01T01:30:00Z"), slices[0].EndUtc);
            Assert.Equal(Utc("2024-01-01T02:30:00Z"), slices[2].StartUtc);
            Assert.Equal(Utc("2024-01-01T03:30:00Z"), slices[2].EndUtc);
        }

        [Fact]
        public void Slice_enumeration_uses_delay_from_utc_now_when_endOn_is_absent()
        {
            var job = Job("demo", start: "2024-01-01T00:00:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.FromMinutes(15));
            var clock = new ManualClock(Utc("2024-01-01T02:10:00Z"));

            var slices = SliceEnumerator.EnumerateEligible(job, clock);

            Assert.Single(slices);
            Assert.Equal(Utc("2024-01-01T01:00:00Z"), slices[0].EndUtc);
        }

        [Fact]
        public void Dependency_readiness_is_evaluated_per_downstream_slice_window()
        {
            var upstream = Job("upstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.Zero);
            var downstream = Job("downstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.Zero, dependencies: ["upstream"]);
            var jobs = new Dictionary<string, JobDefinition> { [upstream.ActivityId] = upstream, [downstream.ActivityId] = downstream };
            var completed = new HashSet<SliceKey>
            {
                SliceKey.Create("upstream", Utc("2024-01-01T11:00:00Z"), Utc("2024-01-01T12:00:00Z")),
                SliceKey.Create("upstream", Utc("2024-01-01T12:00:00Z"), Utc("2024-01-01T13:00:00Z"))
            };

            var blocked = DependencyReadinessEvaluator.Evaluate(downstream, new SliceRange("downstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T11:00:00Z")), jobs, completed);
            var readyLater = DependencyReadinessEvaluator.Evaluate(downstream, new SliceRange("downstream", Utc("2024-01-01T11:00:00Z"), Utc("2024-01-01T12:00:00Z")), jobs, completed);

            Assert.False(blocked.IsReady);
            Assert.Single(blocked.MissingSlices);
            Assert.True(readyLater.IsReady);
        }

        [Fact]
        public void Misaligned_equal_size_dependency_requires_all_overlapping_upstream_slices()
        {
            var upstream = Job("upstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.Zero);
            var downstream = Job("downstream", start: "2024-01-01T10:30:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.Zero, dependencies: ["upstream"]);
            var downstreamSlice = new SliceRange("downstream", Utc("2024-01-01T10:30:00Z"), Utc("2024-01-01T11:30:00Z"));
            var jobs = new Dictionary<string, JobDefinition> { [upstream.ActivityId] = upstream, [downstream.ActivityId] = downstream };
            var completed = new HashSet<SliceKey>
            {
                SliceKey.Create("upstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T11:00:00Z"))
            };

            var required = DependencyReadinessEvaluator.RequiredUpstreamSlices(upstream, downstreamSlice);
            var readiness = DependencyReadinessEvaluator.Evaluate(downstream, downstreamSlice, jobs, completed);

            Assert.Equal(
                [
                    SliceKey.Create("upstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T11:00:00Z")),
                    SliceKey.Create("upstream", Utc("2024-01-01T11:00:00Z"), Utc("2024-01-01T12:00:00Z"))
                ],
                required.Select(slice => slice.ToKey()).ToArray());
            Assert.False(readiness.IsReady);
            Assert.Equal([SliceKey.Create("upstream", Utc("2024-01-01T11:00:00Z"), Utc("2024-01-01T12:00:00Z"))], readiness.MissingSlices);
        }

        [Fact]
        public void Larger_upstream_window_blocks_until_overlapping_window_is_complete()
        {
            var upstream = Job("upstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(2), delay: TimeSpan.Zero);
            var downstream = Job("downstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.Zero, dependencies: ["upstream"]);
            var downstreamSlice = new SliceRange("downstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T11:00:00Z"));
            var jobs = new Dictionary<string, JobDefinition> { [upstream.ActivityId] = upstream, [downstream.ActivityId] = downstream };

            var blocked = DependencyReadinessEvaluator.Evaluate(downstream, downstreamSlice, jobs, new HashSet<SliceKey>());
            var ready = DependencyReadinessEvaluator.Evaluate(
                downstream,
                downstreamSlice,
                jobs,
                new HashSet<SliceKey> { SliceKey.Create("upstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T12:00:00Z")) });

            Assert.False(blocked.IsReady);
            Assert.Equal([SliceKey.Create("upstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T12:00:00Z"))], blocked.MissingSlices);
            Assert.True(ready.IsReady);
        }

        [Fact]
        public void Later_downstream_slices_are_ready_when_their_own_required_upstream_windows_are_complete()
        {
            var upstream = Job("upstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(2), delay: TimeSpan.Zero);
            var downstream = Job("downstream", start: "2024-01-01T10:00:00Z", window: TimeSpan.FromHours(1), delay: TimeSpan.Zero, dependencies: ["upstream"]);
            var jobs = new Dictionary<string, JobDefinition> { [upstream.ActivityId] = upstream, [downstream.ActivityId] = downstream };
            var completed = new HashSet<SliceKey>
            {
                SliceKey.Create("upstream", Utc("2024-01-01T12:00:00Z"), Utc("2024-01-01T14:00:00Z"))
            };

            var blockedEarly = DependencyReadinessEvaluator.Evaluate(
                downstream,
                new SliceRange("downstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T11:00:00Z")),
                jobs,
                completed);
            var readyLater = DependencyReadinessEvaluator.Evaluate(
                downstream,
                new SliceRange("downstream", Utc("2024-01-01T12:00:00Z"), Utc("2024-01-01T13:00:00Z")),
                jobs,
                completed);

            Assert.False(blockedEarly.IsReady);
            Assert.Equal([SliceKey.Create("upstream", Utc("2024-01-01T10:00:00Z"), Utc("2024-01-01T12:00:00Z"))], blockedEarly.MissingSlices);
            Assert.True(readyLater.IsReady);
        }

        private static JobDefinition Job(string activityId, string start, TimeSpan window, TimeSpan delay, string? endOn = null, IReadOnlyList<string>? dependencies = null) => new()
        {
            ActivityId = activityId,
            FunctionName = "Fn",
            OutputTable = "Output",
            QueryWindowSize = window,
            DelayFromUtcNow = delay,
            MaxParallelism = 1,
            QueryTimeout = TimeSpan.FromMinutes(1),
            StartFrom = Utc(start),
            EndOn = endOn is null ? null : Utc(endOn),
            Target = new JobTarget { ClusterUri = "https://kolite-example.invalid", Database = "DemoDb" },
            DependsOn = dependencies?.Select(d => new DependentJob { ActivityId = d }).ToArray() ?? []
        };

        private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();
    }
}
