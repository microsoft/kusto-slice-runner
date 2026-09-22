// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Performance;
using static KoLite.Local.Sqlite.Tests.PerformanceTestStore;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqlitePerformanceCoverageTests : IDisposable
    {
        private readonly PerformanceTestStore store = new();

        public void Dispose() => store.Dispose();

        [Fact]
        public void Coverage_cutoff_does_not_change_table_population_or_percentiles()
        {
            var job = store.CreateJob();
            store.Capture("outside-before", job, At(30), At(60).AddTicks(-1));
            store.Capture("at-from", job, At(30), At(60));
            store.Capture("before-cutoff", job, At(30), At(115).AddTicks(-1));
            store.Capture("at-cutoff", job, At(30), At(115));
            store.Capture("recent", job, At(30), At(116));
            store.Capture("at-to", job, At(30), At(120));
            store.Capture("zero", job, At(30), At(90));
            store.Capture("failed", job, At(30), At(90), "FailedRetryable");
            store.Capture("suppressed", job, At(30), At(91), suppressed: true);
            store.Capture("running", job, At(30), null, "Started");
            store.Capture("unknown", job, At(30), At(95), "Unknown");
            var zero = store.Repository.GetPendingAttempts(At(130)).Single(attempt => attempt.AttemptId == "zero");
            store.Repository.ApplyStatistics([zero], [Statistics(zero, 0, 0, 0)], At(130));

            var row = Assert.Single(store.Repository.GetAggregates(At(60), At(120), coverageCutoffUtc: At(115)));
            var all = Assert.Single(store.Repository.GetAggregates(At(60), At(120)));

            Assert.Equal(7, row.CompletedAttempts);
            Assert.Equal(6, row.SucceededAttempts);
            Assert.Equal(new PerformanceCoverageCounts(3, 2), row.Coverage);
            Assert.Equal(new PerformanceCoverageCounts(5, 4), all.Coverage);
            Assert.Equal(all with { Coverage = row.Coverage }, row);
            Assert.Equal(new PerformancePercentiles(1, 0, 0, 0), row.CpuSeconds);
            Assert.Equal(PerformanceCoverageCounts.Empty,
                Assert.Single(store.Repository.GetAggregates(At(60), At(120), coverageCutoffUtc: At(60))).Coverage);
        }

        [Fact]
        public void Missing_any_resource_is_counted_once_with_the_same_validity_and_correlation_rules_as_the_table()
        {
            var job = store.CreateJob();
            foreach (var id in new[] { "zero", "no-cpu", "no-duration", "no-memory", "invalid-cpu", "no-correlation" })
            {
                store.Capture(id, job, At(10), At(20));
            }

            var pending = store.Repository.GetPendingAttempts(At(30))
                .Where(attempt => attempt.AttemptId != "no-correlation").ToArray();
            var statistics = pending.Select(attempt => attempt.AttemptId switch
            {
                "zero" => Statistics(attempt, 0, 0, 0),
                "no-cpu" => Statistics(attempt, null, 1, 1),
                "no-duration" => Statistics(attempt, 1, null, 1),
                "no-memory" => Statistics(attempt, 1, 1, null),
                _ => Statistics(attempt, double.NaN, 1, 1)
            }).ToArray();
            store.Repository.ApplyStatistics(pending, statistics, At(30));
            store.Execute("UPDATE performance_attempts SET cpu_seconds=9,duration_seconds=9,memory_peak_bytes=9 WHERE attempt_id='no-correlation';");

            var row = Assert.Single(store.Repository.GetAggregates(At(0), At(60), coverageCutoffUtc: At(55)));

            Assert.Equal(new PerformanceCoverageCounts(6, 5), row.Coverage);
            Assert.Equal(3, row.CpuSeconds.SampleCount);
            Assert.Equal(4, row.DurationSeconds.SampleCount);
            Assert.Equal(4, row.MemoryGiB.SampleCount);
            Assert.True(PerformanceCoveragePolicy.ShouldWarn(row.Coverage));
        }

        [Fact]
        public void Coverage_counts_query_attempts_across_chunks_and_respects_job_filters_without_double_counting_parents()
        {
            var chunked = store.CreateJob("chunked", chunks: 2);
            var complete = store.CreateJob("complete");
            for (var index = 0; index < 5; index++)
            {
                store.Capture($"missing-{index}", chunked, At(10), At(20), chunk: 0);
            }

            for (var index = 0; index < 20; index++)
            {
                store.Capture($"chunk-ok-{index}", chunked, At(10), At(20), chunk: 1);
            }

            for (var index = 0; index < 75; index++)
            {
                store.Capture($"job-ok-{index}", complete, At(10), At(20));
            }

            var measured = store.Repository.GetPendingAttempts(At(30))
                .Where(attempt => !attempt.AttemptId.StartsWith("missing-", StringComparison.Ordinal)).ToArray();
            store.Repository.ApplyStatistics(measured, measured.Select(attempt => Statistics(attempt)).ToArray(), At(30));

            var all = store.Repository.GetAggregates(At(0), At(60), coverageCutoffUtc: At(55));
            var combined = PerformanceCoverageCounts.Sum(all.Where(row => row.IsJobTotal).Select(row => row.Coverage));
            var selected = store.Repository.GetAggregates(At(0), At(60), [chunked.JobId], At(55));
            var total = Assert.Single(selected, row => row.IsJobTotal);

            Assert.Equal(new PerformanceCoverageCounts(100, 5), combined);
            Assert.False(PerformanceCoveragePolicy.ShouldWarn(combined));
            Assert.Equal(new PerformanceCoverageCounts(25, 5), total.Coverage);
            Assert.True(PerformanceCoveragePolicy.ShouldWarn(total.Coverage));
            Assert.Equal(new PerformanceCoverageCounts(5, 5), Assert.Single(selected, row => row.ChunkId == 0).Coverage);
            Assert.Equal(new PerformanceCoverageCounts(20, 0), Assert.Single(selected, row => row.ChunkId == 1).Coverage);
        }
    }
}
