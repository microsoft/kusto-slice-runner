// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Performance;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;
using static KoLite.Local.Sqlite.Tests.PerformanceTestStore;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqlitePerformanceWorkerTests : IDisposable
    {
        private readonly PerformanceTestStore store = new();
        private readonly ManualClock clock = new(At(10));

        public void Dispose() => store.Dispose();

        [Theory]
        [InlineData(null)]
        [InlineData(2)]
        public async Task Physical_attempt_context_is_committed_before_send_with_dispatch_target_and_stable_execution_identity(int? chunks)
        {
            var job = store.CreateJob(chunks: chunks);
            var queue = new SqliteWorkQueueRepository(store.Factory);
            var chunkState = new SqliteChunkStateRepository(store.Factory);
            var slice = new SliceRange(job.JobId, At(0), At(5));
            var execution = chunks.HasValue ? SliceExecutionUnit.Chunk(slice, 1, chunks.Value) : SliceExecutionUnit.Unchunked(slice);
            if (chunks.HasValue)
            {
                chunkState.EnsureWindow(slice, chunks.Value, "test");
                chunkState.MarkQueued("queued", execution, actor: "test");
            }

            var item = queue.Enqueue(job.JobId, At(0), At(5), $"normal|{execution.ExecutionKey}", clock.UtcNow,
                chunkId: execution.ChunkId, totalChunks: execution.TotalChunks);
            var contexts = new List<LocalSliceAttemptContext>();
            var keys = new List<string>();
            var executor = new ContextExecutor((definition, unit, context, _) =>
            {
                contexts.Add(context);
                keys.Add(unit.ExecutionKey);
                Assert.Equal($"{item.QueueItemId}:{contexts.Count}", context.AttemptId);
                Assert.Equal($"KoLite.Local.Output;attempt|{context.AttemptId}", context.ClientRequestId);
                Assert.Equal("Started", store.Scalar("SELECT status FROM performance_attempts WHERE attempt_id=$id;", ("$id", context.AttemptId)));
                Assert.Equal(context.ClientRequestId, store.Scalar("SELECT client_request_id FROM performance_attempts WHERE attempt_id=$id;", ("$id", context.AttemptId)));
                Assert.Equal(definition.Target.ClusterUri.TrimEnd('/'), store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id=$id;", ("$id", context.AttemptId)));
                Assert.Equal(SqliteStorage.Utc(clock.UtcNow), store.Scalar("SELECT started_at_utc FROM performance_attempts WHERE attempt_id=$id;", ("$id", context.AttemptId)));
                if (contexts.Count == 1)
                {
                    store.Catalog.Update(job.JobId, Schedule(job.ActivityId, chunks, "https://changed-target.invalid", "ChangedDb"), job.CatalogVersion);
                    return Task.FromResult(LocalSliceOutputResult.Failure("Retry", "retry once"));
                }

                return Task.FromResult(LocalSliceOutputResult.Success("same-output") with { DuplicateSuppressed = true });
            });
            var worker = new SqliteLocalWorker(store.Catalog, store.State, queue, store.Observability, executor, clock,
                new LocalWorkerOptions(WorkerId: "test"), chunkState: chunkState);

            Assert.False((await worker.RunOnceAsync()).Succeeded);
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.True((await worker.RunOnceAsync()).Succeeded);

            Assert.Equal(2, contexts.Select(context => context.ClientRequestId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(keys, key => Assert.Equal(execution.ExecutionKey, key));
            Assert.Equal($"normal|{execution.ExecutionKey}", Assert.Single(queue.List(job.JobId)).IdempotencyKey);
            Assert.Equal(Cluster, store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id=$id;", ("$id", contexts[0].AttemptId)));
            Assert.Equal(1, store.Count("SELECT catalog_version FROM performance_attempts WHERE attempt_id=$id;", ("$id", contexts[0].AttemptId)));
            Assert.Equal("https://changed-target.invalid", store.Scalar("SELECT cluster_uri FROM performance_attempts WHERE attempt_id=$id;", ("$id", contexts[1].AttemptId)));
            Assert.Equal(2, store.Count("SELECT catalog_version FROM performance_attempts WHERE attempt_id=$id;", ("$id", contexts[1].AttemptId)));
            Assert.Equal(1, store.Count("SELECT duplicate_suppressed FROM performance_attempts WHERE attempt_id=$id;", ("$id", contexts[1].AttemptId)));
            Assert.Empty(store.Repository.GetPendingAttempts(At(30)));
            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(30)), row => row.IsJobTotal);
            Assert.Equal(2, total.CompletedAttempts);
            Assert.Equal(1, total.SucceededAttempts);
            Assert.Equal(PerformancePercentiles.Empty, total.CpuSeconds);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Fault_or_timeout_preserves_pre_send_context_without_an_output_success(bool timeout)
        {
            var job = store.CreateJob();
            if (timeout)
            {
                job = store.Catalog.Update(job.JobId, Schedule(job.ActivityId).Replace("\"queryTimeout\":\"00:01:00\"", "\"queryTimeout\":\"00:00:00.0500000\"", StringComparison.Ordinal), job.CatalogVersion);
            }

            var queue = new SqliteWorkQueueRepository(store.Factory);
            var item = queue.Enqueue(job.JobId, At(0), At(5), "attempt", clock.UtcNow);
            LocalSliceAttemptContext? sent = null;
            var executor = new ContextExecutor(async (_, _, context, cancellation) =>
            {
                sent = context;
                if (timeout)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                }

                throw new TimeoutException("test fault after dispatch");
            });
            var worker = new SqliteLocalWorker(store.Catalog, store.State, queue, store.Observability, executor, clock,
                new LocalWorkerOptions(WorkerId: "test", ClientTimeoutBuffer: TimeSpan.Zero));

            var result = await worker.RunOnceAsync();

            Assert.False(result.Succeeded);
            Assert.NotNull(sent);
            Assert.Equal("FailedRetryable", store.Scalar("SELECT status FROM performance_attempts;"));
            Assert.Equal(sent!.ClientRequestId, store.Scalar("SELECT client_request_id FROM performance_attempts;"));
            Assert.Equal(SqliteStorage.Utc(At(10)), store.Scalar("SELECT started_at_utc FROM performance_attempts;"));
            Assert.Equal($"{item.QueueItemId}:1", sent.AttemptId);
            Assert.Empty(store.Repository.GetPendingAttempts(At(30)));
            Assert.Equal(DurableWorkQueueState.Queued, Assert.Single(queue.List(job.JobId)).State);
        }

        [Fact]
        public async Task Lease_loss_records_a_completed_failed_attempt_and_retains_the_dispatch_context()
        {
            var job = store.CreateJob();
            var queue = new SqliteWorkQueueRepository(store.Factory);
            queue.Enqueue(job.JobId, At(0), At(5), "attempt", clock.UtcNow);
            var executor = new ContextExecutor((_, _, context, _) =>
            {
                var current = store.State.Get(job.JobId, At(0), At(5));
                store.State.Append("other-completion", job.JobId, At(0), At(5), DurableSliceStatus.Completed, current.Version);
                Assert.Equal(context.ClientRequestId, store.Scalar("SELECT client_request_id FROM performance_attempts;"));
                return Task.FromResult(LocalSliceOutputResult.Success());
            });
            var worker = new SqliteLocalWorker(store.Catalog, store.State, queue, store.Observability, executor, clock);

            var result = await worker.RunOnceAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("LeaseLost", store.Scalar("SELECT status FROM performance_attempts;"));
            Assert.NotNull(store.Scalar("SELECT client_request_id FROM performance_attempts;"));
            var total = Assert.Single(store.Repository.GetAggregates(At(0), At(30)));
            Assert.Equal(1, total.CompletedAttempts);
            Assert.Equal(0, total.SucceededAttempts);
            Assert.Empty(store.Repository.GetPendingAttempts(At(30)));
        }

        [Fact]
        public async Task Host_cancellation_leaves_only_the_original_incomplete_capture_and_the_live_lease()
        {
            var job = store.CreateJob();
            var queue = new SqliteWorkQueueRepository(store.Factory);
            queue.Enqueue(job.JobId, At(0), At(5), "attempt", clock.UtcNow);
            var executor = new ContextExecutor((_, _, _, token) => Task.FromCanceled<LocalSliceOutputResult>(token));
            var worker = new SqliteLocalWorker(store.Catalog, store.State, queue, store.Observability, executor, clock);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.RunOnceAsync(cancellation.Token));

            Assert.Equal("Started", store.Scalar("SELECT status FROM performance_attempts;"));
            Assert.Null(store.Scalar("SELECT completed_at_utc FROM performance_attempts;"));
            Assert.NotNull(store.Scalar("SELECT client_request_id FROM performance_attempts;"));
            Assert.Empty(store.Repository.GetAggregates(At(0), At(30)));
            Assert.Empty(store.Repository.GetPendingAttempts(At(30)));
            Assert.Equal(DurableWorkQueueState.Leased, Assert.Single(queue.List(job.JobId)).State);
        }

        [Fact]
        public void Attempt_and_performance_fact_writes_commit_or_rollback_together()
        {
            var job = store.CreateJob();
            store.Execute("""
                CREATE TRIGGER fail_performance_capture BEFORE INSERT ON performance_attempts
                BEGIN SELECT RAISE(ABORT,'test capture failure'); END;
                """);

            Assert.Throws<SqliteException>(() => store.Capture("atomic", job, At(10), null, "Started"));
            Assert.Equal(0, store.Count("SELECT COUNT(*) FROM slice_attempts;"));
            Assert.Equal(0, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
            store.Execute("DROP TRIGGER fail_performance_capture;");
            store.Capture("atomic", job, At(10), null, "Started");
            Assert.Equal(1, store.Count("SELECT COUNT(*) FROM slice_attempts;"));
            Assert.Equal(1, store.Count("SELECT COUNT(*) FROM performance_attempts;"));
        }

        [Fact]
        public void Repeated_completion_preserves_the_original_start_and_correlation()
        {
            var job = store.CreateJob();
            store.Capture("attempt", job, At(10), null, "Started");
            store.Capture("attempt", job, At(12), At(13), clientRequestId: "unexpected replacement");
            store.Observability.RecordAttempt("attempt", job.JobId, At(0), At(5), 1, "Succeeded", "worker", null, At(13));

            Assert.Equal(SqliteStorage.Utc(At(10)), store.Scalar("SELECT started_at_utc FROM slice_attempts;"));
            Assert.Equal(SqliteStorage.Utc(At(10)), store.Scalar("SELECT started_at_utc FROM performance_attempts;"));
            Assert.Equal("KoLite.Local.Output;attempt|attempt", store.Scalar("SELECT client_request_id FROM performance_attempts;"));
            Assert.Equal(1, Assert.Single(store.Repository.GetAggregates(At(0), At(30))).CompletedAttempts);
        }

        private sealed class ContextExecutor : ILocalSliceOutputExecutor
        {
            private readonly Func<JobDefinition, SliceExecutionUnit, LocalSliceAttemptContext, CancellationToken, Task<LocalSliceOutputResult>> execute;

            internal ContextExecutor(Func<JobDefinition, SliceExecutionUnit, LocalSliceAttemptContext, CancellationToken, Task<LocalSliceOutputResult>> execute)
            {
                this.execute = execute;
            }

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default) =>
                throw new InvalidOperationException("The worker must use the physical-attempt overload.");

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceExecutionUnit execution, LocalSliceAttemptContext attempt, CancellationToken cancellationToken = default) =>
                execute(job, execution, attempt, cancellationToken);
        }
    }
}
