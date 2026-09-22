// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using KoLite.Local.Core.Performance;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Performance;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;
using static KoLite.Local.Sqlite.Tests.PerformanceTestStore;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqlitePerformanceRepositoryTests : IDisposable
    {
        private readonly PerformanceTestStore store = new();
        private readonly ITestOutputHelper output;

        public SqlitePerformanceRepositoryTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        public void Dispose() => store.Dispose();

        [Fact]
        public void Sixteen_successful_chunks_and_one_failed_retry_are_seventeen_attempts()
        {
            var job = store.CreateJob(chunks: 16);
            for (var chunk = 0; chunk < 16; chunk++)
            {
                store.Capture($"chunk-{chunk}", job, At(10 + chunk), At(11 + chunk), chunk: chunk);
            }

            store.Capture("chunk-3-retry", job, At(8), At(9), "FailedRetryable", chunk: 3);
            var pending = store.Repository.GetPendingAttempts(At(40));
            store.Repository.ApplyStatistics(pending, pending.Select(attempt =>
                Statistics(attempt, cpu: int.Parse(attempt.AttemptId["chunk-".Length..]) + 1)).ToArray(), At(40));

            var rows = store.Repository.GetAggregates(At(0), At(40));
            var total = Assert.Single(rows, row => row.IsJobTotal);
            Assert.Equal(17, total.CompletedAttempts);
            Assert.Equal(16, total.SucceededAttempts);
            Assert.Equal(94.11764705882354, total.SuccessPercent!.Value, 10);
            Assert.Equal(new PerformancePercentiles(16, 8, 15, 16), total.CpuSeconds);
            var retried = Assert.Single(rows, row => row.ChunkId == 3);
            Assert.Equal(2, retried.CompletedAttempts);
            Assert.Equal(50d, retried.SuccessPercent);
            Assert.Equal(DurableSliceStatus.Queued, store.State.Get(job.JobId, At(0), At(5)).Status);
        }

        [Fact]
        public void Job_percentiles_pool_unequal_chunk_samples_and_do_not_average_percentiles_or_rates()
        {
            var job = store.CreateJob(chunks: 2);
            for (var value = 1; value <= 9; value++)
            {
                store.Capture($"sample-{value}", job, At(10), At(11), chunk: 0);
            }

            store.Capture("sample-1000", job, At(10), At(11), chunk: 1);
            store.Capture("retry", job, At(8), At(9), "FailedRetryable", chunk: 1);
            var pending = store.Repository.GetPendingAttempts(At(20));
            store.Repository.ApplyStatistics(pending, pending.Select(attempt =>
                Statistics(attempt, cpu: int.Parse(attempt.AttemptId["sample-".Length..]))).ToArray(), At(20));

            var rows = store.Repository.GetAggregates(At(0), At(20));
            var total = Assert.Single(rows, row => row.IsJobTotal);
            Assert.Equal(new PerformancePercentiles(10, 5, 9, 1000), total.CpuSeconds);
            Assert.Equal(1000d, Assert.Single(rows, row => row.ChunkId == 1).CpuSeconds.P50);
            Assert.Equal(100d * 10 / 11, total.SuccessPercent);
        }

        [Fact]
        public void Resource_families_have_independent_valid_sample_populations_and_zero_is_not_missing()
        {
            var job = store.CreateJob();
            foreach (var id in new[] { "zero", "cpu", "duration", "invalid" })
            {
                store.Capture(id, job, At(10), At(19));
            }

            var pending = store.Repository.GetPendingAttempts(At(20));
            var samples = pending.Select(attempt => attempt.AttemptId switch
            {
                "zero" => Statistics(attempt, 0, 0, 0),
                "cpu" => Statistics(attempt, 8, null, null),
                "duration" => Statistics(attempt, double.NaN, 10, -1, error: "Invalid CPU and memory."),
                _ => Statistics(attempt, double.PositiveInfinity, double.MaxValue, null)
            }).ToArray();
            store.Repository.ApplyStatistics(pending, samples, At(20));

            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(30)));
            Assert.Equal(4, total.CompletedAttempts);
            Assert.Equal(new PerformancePercentiles(2, 0, 8, 8), total.CpuSeconds);
            Assert.Equal(new PerformancePercentiles(2, 0, 10, 10), total.DurationSeconds);
            Assert.Equal(new PerformancePercentiles(1, 0, 0, 0), total.MemoryGiB);
            Assert.NotNull(store.Repository.GetCollectionStatus().LastError);
            Assert.Equal(3, store.Repository.GetCollectionStatus().AvailableSamples);
            Assert.Equal(540, (At(19) - At(10)).TotalSeconds);
            Assert.DoesNotContain(540d, new[] { total.DurationSeconds.P50, total.DurationSeconds.P90, total.DurationSeconds.P95 });
        }

        [Fact]
        public void Missing_measurements_never_become_local_elapsed_duration_or_synthetic_zero_cost()
        {
            var job = store.CreateJob(chunks: 1);
            store.Capture("missing", job, At(1), At(11), chunk: 0);
            store.Capture("suppressed", job, At(12), At(13), chunk: 0, suppressed: true);
            store.Capture("failed", job, At(14), At(15), "DeadLettered", chunk: 0);

            var pending = store.Repository.GetPendingAttempts(At(20));
            Assert.Equal("missing", Assert.Single(pending).AttemptId);
            store.Repository.ApplyStatistics(pending, [], At(20));
            var rows = store.Repository.GetAggregates(At(0), At(20));
            var total = Assert.Single(rows, row => row.IsJobTotal);
            Assert.Equal(3, total.CompletedAttempts);
            Assert.Equal(2, total.SucceededAttempts);
            Assert.Equal(PerformancePercentiles.Empty, total.DurationSeconds);
            Assert.Equal(PerformancePercentiles.Empty, total.CpuSeconds);
            Assert.Equal(PerformancePercentiles.Empty, total.MemoryGiB);
            Assert.Equal(0, Assert.Single(rows, row => !row.IsJobTotal).ChunkId);
        }

        [Fact]
        public void Completion_window_is_exact_half_open_utc_and_uses_all_known_terminal_outcomes()
        {
            var job = store.CreateJob();
            store.Capture("at-from", job, At(30), At(60).ToOffset(TimeSpan.FromHours(2)));
            store.Capture("retry", job, At(30), At(70), "FailedRetryable");
            store.Capture("failure", job, At(30), At(80), "Failed");
            store.Capture("dead", job, At(30), At(90), "DeadLettered");
            store.Capture("lost", job, At(30), At(120).AddTicks(-1), "LeaseLost");
            store.Capture("before", job, At(30), At(60).AddTicks(-1));
            store.Capture("at-to", job, At(30), At(120));
            store.Capture("running", job, At(30), null, "Started");
            store.Capture("started-with-end", job, At(30), At(100), "Started");
            store.Capture("unknown", job, At(30), At(100), "FutureStatus");

            var total = Assert.Single(store.Repository.GetAggregates(At(60), At(120)));
            Assert.Equal(5, total.CompletedAttempts);
            Assert.Equal(1, total.SucceededAttempts);
            Assert.Equal(20d, total.SuccessPercent);
            Assert.Empty(store.Repository.GetAggregates(At(60), At(60)));
            Assert.Throws<ArgumentOutOfRangeException>(() => store.Repository.GetAggregates(At(120), At(60)));
        }

        [Fact]
        public void Job_filter_applies_before_aggregation_and_empty_selection_is_empty()
        {
            var first = store.CreateJob("first");
            var second = store.CreateJob("second");
            store.Capture("first-attempt", first, At(10), At(11));
            store.Capture("second-attempt", second, At(10), At(11));

            var result = Assert.Single(store.Repository.GetAggregates(At(0), At(20), [second.JobId, second.JobId]));
            Assert.Equal(second.JobId, result.JobId);
            Assert.Empty(store.Repository.GetAggregates(At(0), At(20), []));
        }

        [Fact]
        public void Report_and_collector_queries_and_job_foreign_key_are_index_backed()
        {
            using var connection = store.Factory.OpenConnection();
            var range = QueryPlan(connection, SqlitePerformanceRepository.AggregateSql(false),
                ("$from", SqliteStorage.Utc(At(0))), ("$to", SqliteStorage.Utc(At(60))), ("$maxSeconds", SqlitePerformanceRepository.MaxMetricSeconds),
                ("$coverageCutoff", SqliteStorage.Utc(At(60))));
            var filtered = QueryPlan(connection, SqlitePerformanceRepository.AggregateSql(true),
                ("$from", SqliteStorage.Utc(At(0))), ("$to", SqliteStorage.Utc(At(60))), ("$maxSeconds", SqlitePerformanceRepository.MaxMetricSeconds),
                ("$jobs", "[\"one\",\"two\"]"), ("$coverageCutoff", SqliteStorage.Utc(At(60))));
            var pending = QueryPlan(connection, SqlitePerformanceRepository.PendingSql(false),
                ("$now", SqliteStorage.Utc(At(60))), ("$cutoff", SqliteStorage.Utc(At(0))), ("$take", 200));
            var delete = QueryPlan(connection, "SELECT 1 FROM performance_attempts WHERE job_id='one';");
            var history = QueryPlan(connection, SqlitePerformanceRepository.CurrentHistorySql(),
                ("$from", SqliteStorage.Utc(At(0))), ("$to", SqliteStorage.Utc(At(60))),
                ("$cursor", SqliteStorage.Utc(At(20))), ("$id", "attempt"), ("$take", 500));
            Assert.Contains("ix_performance_attempts_completed", range, StringComparison.Ordinal);
            Assert.Contains("ix_performance_attempts_job_completed", filtered, StringComparison.Ordinal);
            Assert.Contains("ix_performance_attempts_pending", pending, StringComparison.Ordinal);
            Assert.Contains("ix_performance_attempts_target_pending", pending, StringComparison.Ordinal);
            Assert.Contains("ix_performance_attempts_job_completed", delete, StringComparison.Ordinal);
            Assert.DoesNotContain("SCAN performance_attempts", delete, StringComparison.Ordinal);
            Assert.Contains("ix_slice_attempts_completed", history, StringComparison.Ordinal);
        }

        [Fact]
        public void One_hundred_thousand_attempts_have_exact_uncapped_aggregates_in_one_pipeline()
        {
            store.Execute("""
                WITH RECURSIVE jobs(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM jobs WHERE n<99)
                INSERT INTO job_definitions(job_id,activity_id,display_name,schedule_json)
                SELECT printf('scale-%03d',n), printf('scale-%03d',n), printf('scale-%03d',n), '{}' FROM jobs;
                WITH RECURSIVE samples(n) AS (VALUES(0) UNION ALL SELECT n+1 FROM samples WHERE n<99999)
                INSERT INTO performance_attempts (
                    attempt_id,job_id,slice_start_utc,slice_end_utc,attempt,status,completed_at_utc,
                    chunk_id,total_chunks,capture_source,collection_status,cpu_seconds,duration_seconds,memory_peak_bytes,
                    cluster_uri,database_name,server_activity_id,recorded_at_utc,updated_at_utc)
                SELECT 'sample-'||n, printf('scale-%03d',n/1000), $start, $end, 1, 'Succeeded', $completed,
                       CASE WHEN n/1000=0 THEN NULL ELSE n%1000%(1+(n/1000)%32) END,
                       CASE WHEN n/1000=0 THEN NULL ELSE 1+(n/1000)%32 END,
                       'Dispatch','Collected', n%1000+1, 2*(n%1000+1), 1073741824*(n%1000+1),
                       'https://performance-example.invalid','MetricsDb','server-'||n,$completed,$completed
                FROM samples;
                INSERT INTO performance_attempts (
                    attempt_id,job_id,slice_start_utc,slice_end_utc,attempt,status,completed_at_utc,capture_source,collection_status,recorded_at_utc,updated_at_utc)
                SELECT 'noise-'||attempt_id,job_id,slice_start_utc,slice_end_utc,1,'Succeeded',$old,'Dispatch','Unavailable',$old,$old
                FROM performance_attempts LIMIT 20000;
                """,
                ("$start", SqliteStorage.Utc(At(0))), ("$end", SqliteStorage.Utc(At(5))),
                ("$completed", SqliteStorage.Utc(At(10))), ("$old", SqliteStorage.Utc(At(10).AddDays(-31))));

            var factory = new CountingFactory(store.Factory);
            var repository = new SqlitePerformanceRepository(factory);
            var stopwatch = Stopwatch.StartNew();
            var rows = repository.GetAggregates(At(0), At(20));
            stopwatch.Stop();

            output.WriteLine($"Exact 100k-attempt aggregation took {stopwatch.Elapsed.TotalSeconds:0.000}s (informational).");
            Assert.Equal(1, factory.OpenCount);
            var totals = rows.Where(row => row.IsJobTotal).ToArray();
            Assert.Equal(100, totals.Length);
            Assert.Equal(100_000, totals.Sum(row => row.CompletedAttempts));
            Assert.All(totals, total =>
            {
                Assert.Equal(1000, total.CompletedAttempts);
                Assert.Equal(new PerformancePercentiles(1000, 500, 900, 950), total.CpuSeconds);
                Assert.Equal(new PerformancePercentiles(1000, 1000, 1800, 1900), total.DurationSeconds);
                Assert.Equal(new PerformancePercentiles(1000, 500, 900, 950), total.MemoryGiB);
            });
            var chunks32 = rows.Where(row => row.JobId == "scale-031" && !row.IsJobTotal).ToArray();
            Assert.Equal(Enumerable.Range(0, 32), chunks32.Select(row => row.ChunkId!.Value));
            foreach (var chunk in chunks32)
            {
                var samples = Enumerable.Range(1, 1000).Where(value => (value - 1) % 32 == chunk.ChunkId).ToArray();
                Assert.Equal(samples.Length, chunk.CompletedAttempts);
                Assert.Equal(samples[(int)Math.Ceiling(samples.Length * .95) - 1], chunk.CpuSeconds.P95);
            }
        }

        private static string QueryPlan(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            using var command = SqliteStorage.Command(connection, null, "EXPLAIN QUERY PLAN " + sql);
            foreach (var (name, value) in parameters) command.Add(name, value);
            using var reader = command.ExecuteReader();
            var plan = new List<string>();
            while (reader.Read()) plan.Add(reader.GetString(3));
            return string.Join(" | ", plan);
        }

        private sealed class CountingFactory : IKoLiteSqliteConnectionFactory
        {
            private readonly IKoLiteSqliteConnectionFactory inner;
            internal CountingFactory(IKoLiteSqliteConnectionFactory inner) => this.inner = inner;
            internal int OpenCount { get; private set; }
            public SqliteConnection OpenConnection()
            {
                OpenCount++;
                return inner.OpenConnection();
            }
        }
    }
}
