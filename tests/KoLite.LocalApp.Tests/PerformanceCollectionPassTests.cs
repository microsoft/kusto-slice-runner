using KoLite.Local.Core.Performance;
using KoLite.Local.Core.Time;
using KoLite.LocalApp.Performance;
using Microsoft.Extensions.Logging.Abstractions;

namespace KoLite.LocalApp.Tests
{
    public sealed class PerformanceCollectionPassTests
    {
        private static readonly DateTimeOffset Now = new(2026, 9, 11, 20, 0, 0, TimeSpan.Zero);

        [Fact]
        public async Task Empty_pending_work_does_not_contact_Kusto()
        {
            var store = new RecordingStore();
            var reader = new RecordingReader();
            var pass = Create(store, reader);

            Assert.True(await pass.RunAsync(true, false, CancellationToken.None));
            Assert.Equal(1, store.Initializations);
            Assert.Empty(reader.Queries);
            Assert.Empty(store.Applied);
        }

        [Fact]
        public async Task Local_history_is_batched_and_completed_before_remote_collection()
        {
            var store = new RecordingStore { HistoryComplete = false, Pending = [Attempt(0)] };
            var reader = new RecordingReader();
            var pass = Create(store, reader, new PerformanceCollectionSchedule { LocalBatchesPerPass = 2 });

            Assert.False(await pass.RunAsync(true, false, CancellationToken.None));
            Assert.Equal(2, store.HistoryBatches);
            Assert.Empty(reader.Queries);

            store.HistoryComplete = true;
            Assert.True(await pass.RunAsync(false, true, CancellationToken.None));
            Assert.Equal(1, store.Initializations);
            Assert.True(store.LastOldestFirst);
            Assert.Single(reader.Queries);
            Assert.Single(store.Applied);
        }

        [Fact]
        public async Task Reads_are_target_scoped_time_bounded_and_written_after_completion()
        {
            var first = Attempt(0) with { StartedAtUtc = Now.AddDays(-40) };
            var second = Attempt(1) with { ClusterUri = "https://another-cluster.invalid", Database = "OtherDb" };
            var store = new RecordingStore { Pending = [first, second] };
            var reader = new RecordingReader();
            var pass = Create(store, reader);

            await pass.RunAsync(false, false, CancellationToken.None);

            Assert.Equal(2, reader.Queries.Count);
            Assert.Equal("DemoDb", reader.Queries[0].Database);
            Assert.Equal(Now.AddDays(-30), reader.Queries[0].FromUtc);
            Assert.Equal(Now, reader.Queries[0].ToUtc);
            Assert.Equal(new[] { first.ClientRequestId }, reader.Queries[0].ClientRequestIds);
            Assert.Equal("OtherDb", reader.Queries[1].Database);
            Assert.Equal(1, reader.MaxConcurrent);
            Assert.Equal(2, store.Applied.Count);
            Assert.Equal(1, store.SuccessfulPasses);
        }

        [Fact]
        public async Task Oversized_results_are_split_without_parallel_requests_or_lost_attempts()
        {
            var store = new RecordingStore { Pending = Enumerable.Range(0, 4).Select(Attempt).ToArray() };
            var reader = new RecordingReader { MaxIdsPerResult = 1 };
            var pass = Create(store, reader);

            await pass.RunAsync(false, false, CancellationToken.None);

            Assert.Equal(7, reader.Queries.Count);
            Assert.Equal(1, reader.MaxConcurrent);
            Assert.Equal(4, store.Applied.Count);
            Assert.Equal(4, store.Applied.SelectMany(batch => batch).Distinct(StringComparer.Ordinal).Count());
            Assert.Empty(store.Failures);
        }

        [Fact]
        public async Task Request_budget_defers_remaining_work_instead_of_looping_without_a_bound()
        {
            var store = new RecordingStore { Pending = Enumerable.Range(0, 4).Select(Attempt).ToArray() };
            var reader = new RecordingReader { MaxIdsPerResult = 1 };
            var pass = Create(store, reader, new PerformanceCollectionSchedule { RemoteRequestsPerPass = 2 });

            await pass.RunAsync(false, false, CancellationToken.None);

            Assert.Equal(2, reader.Queries.Count);
            Assert.Empty(store.Applied);
            Assert.Equal(4, store.Failures.SelectMany(failure => failure.AttemptIds).Distinct(StringComparer.Ordinal).Count());
            Assert.All(store.Failures, failure => Assert.Contains("request budget", failure.Message, StringComparison.Ordinal));
            Assert.Equal(0, store.SuccessfulPasses);
        }

        [Fact]
        public async Task Reader_failure_is_recorded_without_applying_results_or_reporting_success()
        {
            var store = new RecordingStore { Pending = [Attempt(0)] };
            var reader = new RecordingReader { Failure = new InvalidOperationException("Temporary metadata access failure.") };

            Assert.True(await Create(store, reader).RunAsync(false, false, CancellationToken.None));

            var failure = Assert.Single(store.Failures);
            Assert.Contains("Temporary metadata access failure", failure.Message, StringComparison.Ordinal);
            Assert.Empty(store.Applied);
            Assert.Equal(0, store.SuccessfulPasses);
        }

        [Fact]
        public async Task Host_cancellation_is_not_recorded_as_a_metadata_failure()
        {
            var store = new RecordingStore { Pending = [Attempt(0)] };
            var reader = new RecordingReader { BlockUntilCancelled = true };
            using var cancellation = new CancellationTokenSource();
            var operation = Create(store, reader).RunAsync(false, false, cancellation.Token);
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Empty(store.Applied);
            Assert.Empty(store.Failures);
        }

        [Fact]
        public async Task Drain_prevents_even_local_history_initialization()
        {
            var store = new RecordingStore { Pending = [Attempt(0)] };
            var reader = new RecordingReader();
            var drain = new LocalShutdownDrainCoordinator();
            drain.RequestDrain(Now, "test");

            Assert.True(await Create(store, reader, drain: drain).RunAsync(true, false, CancellationToken.None));
            Assert.Equal(0, store.Initializations);
            Assert.Equal(0, store.HistoryBatches);
            Assert.Empty(reader.Queries);
        }

        private static PerformanceCollectionPass Create(
            RecordingStore store,
            RecordingReader reader,
            PerformanceCollectionSchedule? schedule = null,
            LocalShutdownDrainCoordinator? drain = null) =>
            new(store, reader, new ManualClock(Now), drain ?? new LocalShutdownDrainCoordinator(),
                schedule ?? new PerformanceCollectionSchedule(), NullLogger<PerformanceCollectionPass>.Instance);

        private static PerformancePendingAttempt Attempt(int index) =>
            new($"attempt-{index}", "job", "https://example-cluster.invalid", "DemoDb",
                $"KoLite.Local.Output;attempt|{index}", Now.AddMinutes(-5), Now.AddMinutes(-4), false, 0);

        private sealed class RecordingStore : IPerformanceCollectionStore
        {
            public int Initializations { get; private set; }
            public int HistoryBatches { get; private set; }
            public int SuccessfulPasses { get; private set; }
            public bool HistoryComplete { get; set; } = true;
            public bool LastOldestFirst { get; private set; }
            public IReadOnlyList<PerformancePendingAttempt> Pending { get; set; } = [];
            public List<string[]> Applied { get; } = [];
            public List<(string[] AttemptIds, string Message)> Failures { get; } = [];

            public void BeginHistoryReconciliation(DateTimeOffset nowUtc) => Initializations++;
            public bool BackfillBatch(DateTimeOffset nowUtc, int batchSize = 500)
            {
                HistoryBatches++;
                return HistoryComplete;
            }

            public IReadOnlyList<PerformancePendingAttempt> GetPendingAttempts(DateTimeOffset nowUtc, int take = 200, bool oldestFirst = false)
            {
                LastOldestFirst = oldestFirst;
                return Pending.Take(take).ToArray();
            }

            public void ApplyStatistics(IReadOnlyList<PerformancePendingAttempt> attempts, IReadOnlyList<KustoCommandStatistics> statistics, DateTimeOffset nowUtc) =>
                Applied.Add(attempts.Select(item => item.AttemptId).ToArray());

            public void RecordCollectionFailure(IReadOnlyList<PerformancePendingAttempt> attempts, DateTimeOffset nowUtc, string message) =>
                Failures.Add((attempts.Select(item => item.AttemptId).ToArray(), message));

            public void RecordPassSuccess(DateTimeOffset nowUtc) => SuccessfulPasses++;
        }

        private sealed class RecordingReader : IKustoCommandStatisticsReader
        {
            private readonly object gate = new();
            private int concurrent;
            public List<KustoCommandStatisticsQuery> Queries { get; } = [];
            public int MaxConcurrent { get; private set; }
            public int MaxIdsPerResult { get; init; } = 200;
            public Exception? Failure { get; init; }
            public bool BlockUntilCancelled { get; init; }
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<IReadOnlyList<KustoCommandStatistics>> ReadAsync(KustoCommandStatisticsQuery query, CancellationToken cancellationToken = default)
            {
                lock (gate)
                {
                    Queries.Add(query);
                    concurrent++;
                    MaxConcurrent = Math.Max(MaxConcurrent, concurrent);
                }
                Entered.TrySetResult();
                try
                {
                    if (BlockUntilCancelled)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    if (Failure is not null) throw Failure;
                    if (query.ClientRequestIds.Count > MaxIdsPerResult)
                    {
                        throw new CommandStatisticsResultTooLargeException("Fixture requires smaller request batches.");
                    }

                    await Task.Yield();
                    return query.ClientRequestIds.Select(id => new KustoCommandStatistics(
                        id, Guid.NewGuid(), Now.AddMinutes(-5), Now.AddMinutes(-4), "Completed", 1, 2, 1024)).ToArray();
                }
                finally
                {
                    lock (gate)
                    {
                        concurrent--;
                    }
                }
            }
        }
    }
}
