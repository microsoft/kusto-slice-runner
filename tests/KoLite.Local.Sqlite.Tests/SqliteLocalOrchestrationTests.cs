using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Orchestration;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteLocalOrchestrationTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "orchestration-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly ManualClock clock = new(At(15));

        public SqliteLocalOrchestrationTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "orchestration.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(factory).Migrate();
            catalog = new SqliteJobCatalogRepository(factory);
            state = new SqliteSliceStateRepository(factory);
            queue = new SqliteWorkQueueRepository(factory);
            observability = new SqliteOperationalReadModelRepository(factory);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        [Fact]
        public async Task Scheduler_and_worker_complete_eligible_slices_without_live_kusto()
        {
            catalog.Create(Schedule("job.happy", maxParallelism: 2));
            var scheduler = Scheduler(maxSlicesPerTick: 10);
            var executor = new RecordingExecutor();
            var worker = Worker(executor);

            var tick = scheduler.Tick();
            await worker.RunOnceAsync();
            await worker.RunOnceAsync();

            Assert.Equal(2, tick.Enqueued);
            Assert.Equal(2, executor.Requests.Count);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.happy", At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.happy", At(5), At(10)).Status);
            Assert.All(queue.List("job.happy"), item => Assert.Equal(DurableWorkQueueState.Completed, item.State));
        }

        [Fact]
        public void Scheduler_enqueues_missing_historical_slices_even_when_later_slices_are_completed()
        {
            var localClock = new ManualClock(At(10));
            catalog.Create(Schedule("job.backfill", maxParallelism: 10));
            state.Append("later-completed", "job.backfill", At(5), At(10), DurableSliceStatus.Completed, expectedVersion: 0);

            var tick = Scheduler(maxSlicesPerTick: 10, localClock).Tick();

            Assert.Equal(1, tick.Enqueued);
            Assert.Equal(DurableSliceStatus.Queued, state.Get("job.backfill", At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.backfill", At(5), At(10)).Status);
            Assert.Single(queue.List("job.backfill"));
        }

        [Fact]
        public void Scheduler_blocks_only_slices_with_missing_dependency_windows()
        {
            catalog.Create(Schedule("upstream", maxParallelism: 10));
            catalog.Create(Schedule("downstream", maxParallelism: 10, dependsOn: "upstream"));
            state.Append("u-0", "upstream", At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);
            state.Append("u-5", "upstream", At(5), At(10), DurableSliceStatus.Failed, expectedVersion: 0, reason: "boom");
            state.Append("u-10", "upstream", At(10), At(15), DurableSliceStatus.Completed, expectedVersion: 0);

            var tick = Scheduler(maxSlicesPerTick: 20).Tick();

            Assert.Equal(DurableSliceStatus.Queued, state.Get("downstream", At(0), At(5)).Status);
            Assert.Equal(DurableSliceStatus.DependencyBlocked, state.Get("downstream", At(5), At(10)).Status);
            Assert.Equal(DurableSliceStatus.Queued, state.Get("downstream", At(10), At(15)).Status);
            Assert.Equal(1, tick.DependencyBlocked);
        }

        [Fact]
        public void Scheduler_ticks_are_idempotent_and_honor_max_parallelism()
        {
            catalog.Create(Schedule("job.idempotent", maxParallelism: 1));
            var scheduler = Scheduler(maxSlicesPerTick: 10);

            var first = scheduler.Tick();
            var second = scheduler.Tick();

            Assert.Equal(1, first.Enqueued);
            Assert.Equal(0, second.Enqueued);
            Assert.Single(queue.List("job.idempotent"));
            Assert.Equal(1, queue.CountActive("job.idempotent", "default"));
            Assert.True(first.SkippedMaxParallelism > 0 || second.SkippedMaxParallelism > 0);
        }

        [Fact]
        public void Scheduler_does_not_enqueue_completed_slice_after_queue_completion()
        {
            var localClock = new ManualClock(At(5));
            catalog.Create(Schedule("job.completed-state", maxParallelism: 10));
            var scheduler = Scheduler(maxSlicesPerTick: 10, localClock);
            scheduler.Tick();
            var claimed = queue.Claim("default", "worker-completed", TimeSpan.FromMinutes(5), localClock.UtcNow);
            Assert.NotNull(claimed);
            var lease = state.AcquireLease("lease-completed-state", "job.completed-state", At(0), At(5), "worker-completed", TimeSpan.FromMinutes(5), localClock.UtcNow);
            Assert.NotNull(lease);
            Assert.True(state.CompleteLease("complete-completed-state", "job.completed-state", At(0), At(5), "worker-completed", lease.LeaseToken!, localClock.UtcNow));
            Assert.True(queue.Complete(claimed.QueueItemId, "worker-completed"));

            var later = scheduler.Tick();

            Assert.Equal(0, later.Enqueued);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.completed-state", At(0), At(5)).Status);
            Assert.Single(queue.List("job.completed-state"));
        }

        [Fact]
        public void Scheduler_does_not_enqueue_running_leased_slice_again()
        {
            var localClock = new ManualClock(At(5));
            catalog.Create(Schedule("job.running-state", maxParallelism: 10));
            var scheduler = Scheduler(maxSlicesPerTick: 10, localClock);
            scheduler.Tick();
            var claimed = queue.Claim("default", "worker-running", TimeSpan.FromMinutes(5), localClock.UtcNow);
            Assert.NotNull(claimed);
            Assert.NotNull(state.AcquireLease("lease-running-state", "job.running-state", At(0), At(5), "worker-running", TimeSpan.FromMinutes(5), localClock.UtcNow));

            var later = scheduler.Tick();

            Assert.Equal(0, later.Enqueued);
            Assert.Equal(DurableSliceStatus.Running, state.Get("job.running-state", At(0), At(5)).Status);
            Assert.Single(queue.List("job.running-state"));
        }

        [Fact]
        public void Scheduler_enqueues_missing_slice_after_dependencies_are_ready()
        {
            var localClock = new ManualClock(At(5));
            catalog.Create(Schedule("upstream.ready", maxParallelism: 10));
            catalog.Create(Schedule("downstream.missing", maxParallelism: 10, dependsOn: "upstream.ready"));
            state.Append("upstream-ready", "upstream.ready", At(0), At(5), DurableSliceStatus.Completed, expectedVersion: 0);

            var tick = Scheduler(maxSlicesPerTick: 10, localClock).Tick();

            Assert.Equal(1, tick.Enqueued);
            Assert.Equal(DurableSliceStatus.Queued, state.Get("downstream.missing", At(0), At(5)).Status);
            Assert.Single(queue.List("downstream.missing"));
        }

        [Fact]
        public async Task Worker_retries_retryable_failures_and_deadletters_after_max_attempts()
        {
            catalog.Create(Schedule("job.retry", maxParallelism: 1));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var progress = new RecordingProgressSink();
            var executor = new RecordingExecutor(
                LocalSliceOutputResult.Failure("Transient", "try again", isRetryable: true),
                LocalSliceOutputResult.Failure("Transient", "still broken", isRetryable: true));
            var worker = Worker(executor, new LocalWorkerOptions(MaxAttempts: 2, InitialRetryDelay: TimeSpan.FromMinutes(1), VisibilityTimeout: TimeSpan.FromMinutes(5)), progress);

            var first = await worker.RunOnceAsync();
            clock.Advance(TimeSpan.FromMinutes(1));
            var second = await worker.RunOnceAsync();

            Assert.True(first.Executed);
            Assert.False(first.DeadLettered);
            Assert.True(second.DeadLettered);
            Assert.Equal(DurableSliceStatus.DeadLettered, state.Get("job.retry", At(0), At(5)).Status);
            Assert.Equal(DurableWorkQueueState.DeadLettered, queue.List("job.retry").Single().State);
            Assert.Equal(2, queue.List("job.retry").Single().Attempts);
            Assert.Single(observability.GetRecentFailures(), f => f.JobId == "job.retry" && f.Status == "DeadLettered");
            Assert.Collection(progress.Started,
                firstStart =>
                {
                    Assert.Equal(LocalWorkerProgressStatus.Started, firstStart.Status);
                    Assert.Equal("job.retry", firstStart.ActivityId);
                    Assert.Equal(1, firstStart.Attempt);
                },
                secondStart =>
                {
                    Assert.Equal(LocalWorkerProgressStatus.Started, secondStart.Status);
                    Assert.Equal("job.retry", secondStart.ActivityId);
                    Assert.Equal(2, secondStart.Attempt);
                });
            Assert.Collection(progress.Finished,
                failed =>
                {
                    Assert.Equal(LocalWorkerProgressStatus.FailedRetryable, failed.Status);
                    Assert.Equal("job.retry", failed.ActivityId);
                    Assert.Equal(At(0), failed.SliceStartUtc);
                    Assert.Equal(At(5), failed.SliceEndUtc);
                    Assert.Equal(1, failed.Attempt);
                    Assert.Equal("Transient", failed.ErrorCode);
                    Assert.Equal("try again", failed.ErrorMessage);
                    Assert.True(failed.IsRetryable);
                },
                deadLettered =>
                {
                    Assert.Equal(LocalWorkerProgressStatus.DeadLettered, deadLettered.Status);
                    Assert.Equal(2, deadLettered.Attempt);
                    Assert.Equal("Transient", deadLettered.ErrorCode);
                    Assert.Equal("still broken", deadLettered.ErrorMessage);
                    Assert.True(deadLettered.DeadLettered);
                });
        }

        [Fact]
        public async Task Worker_does_not_retry_paused_job_until_resume()
        {
            var created = catalog.Create(Schedule("job.retry.pause", maxParallelism: 1));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var progress = new RecordingProgressSink();
            var executor = new RecordingExecutor(
                LocalSliceOutputResult.Failure("Transient", "try again", isRetryable: true),
                LocalSliceOutputResult.Success("test://retry-complete"));
            var worker = Worker(executor, new LocalWorkerOptions(MaxAttempts: 2, InitialRetryDelay: TimeSpan.FromMinutes(1), VisibilityTimeout: TimeSpan.FromMinutes(5)), progress);

            var first = await worker.RunOnceAsync();
            var disabled = catalog.SetEnabled("job.retry.pause", enabled: false, expectedVersion: created.CatalogVersion);
            clock.Advance(TimeSpan.FromMinutes(1));
            var whilePaused = await worker.RunOnceAsync();

            Assert.True(first.Executed);
            Assert.False(first.DeadLettered);
            Assert.False(whilePaused.ClaimedWork);
            Assert.Single(executor.Requests);
            var pausedQueueItem = queue.List("job.retry.pause").Single();
            Assert.Equal(DurableWorkQueueState.Queued, pausedQueueItem.State);
            Assert.Equal(1, pausedQueueItem.Attempts);
            Assert.Equal(DurableSliceStatus.Failed, state.Get("job.retry.pause", At(0), At(5)).Status);

            catalog.SetEnabled("job.retry.pause", enabled: true, expectedVersion: disabled.CatalogVersion);
            var afterResume = await worker.RunOnceAsync();

            Assert.True(afterResume.Executed);
            Assert.True(afterResume.Succeeded);
            Assert.Equal(2, executor.Requests.Count);
            var completedQueueItem = queue.List("job.retry.pause").Single();
            Assert.Equal(DurableWorkQueueState.Completed, completedQueueItem.State);
            Assert.Equal(2, completedQueueItem.Attempts);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.retry.pause", At(0), At(5)).Status);
            Assert.Collection(progress.Started,
                firstStart => Assert.Equal(1, firstStart.Attempt),
                retryStart => Assert.Equal(2, retryStart.Attempt));
        }

        [Fact]
        public async Task Worker_emits_progress_events_for_successful_execution()
        {
            catalog.Create(Schedule("job.progress", maxParallelism: 1));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var progress = new RecordingProgressSink();
            var worker = Worker(new RecordingExecutor(), new LocalWorkerOptions(WorkerId: "progress-worker"), progress);

            var run = await worker.RunOnceAsync();

            Assert.True(run.Succeeded);
            var started = Assert.Single(progress.Started);
            Assert.Equal(LocalWorkerProgressStatus.Started, started.Status);
            Assert.Equal("job.progress", started.ActivityId);
            Assert.Equal(At(0), started.SliceStartUtc);
            Assert.Equal(At(5), started.SliceEndUtc);
            Assert.Equal(1, started.Attempt);
            Assert.Equal("progress-worker", started.WorkerId);
            Assert.Null(started.CompletedAtUtc);

            var finished = Assert.Single(progress.Finished);
            Assert.Equal(LocalWorkerProgressStatus.Succeeded, finished.Status);
            Assert.Equal(started.QueueItemId, finished.QueueItemId);
            Assert.Equal(started.StartedAtUtc, finished.StartedAtUtc);
            Assert.Equal(clock.UtcNow, finished.CompletedAtUtc);
            Assert.False(finished.DeadLettered);
            Assert.Null(finished.ErrorMessage);
        }

        [Fact]
        public async Task Worker_sizes_queue_and_slice_leases_from_query_timeout_plus_buffer()
        {
            catalog.Create(Schedule("job.long-lease", maxParallelism: 1, queryTimeout: "00:10:00"));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var executor = new BlockingExecutor();
            var worker = Worker(executor);

            var run = worker.RunOnceAsync();
            await executor.WaitUntilExecutingAsync();

            var queueItem = Assert.Single(queue.List("job.long-lease"));
            Assert.Equal(At(27), queueItem.LockedUntilUtc);
            var sliceState = state.Get("job.long-lease", At(0), At(5));
            Assert.Equal(At(27), sliceState.LeaseExpiresAtUtc);

            executor.Complete(LocalSliceOutputResult.Success("test://long-lease"));
            Assert.True((await run).Succeeded);
        }

        [Fact]
        public async Task Worker_keeps_explicit_visibility_timeout_as_lease_floor()
        {
            catalog.Create(Schedule("job.visibility-floor", maxParallelism: 1, queryTimeout: "00:01:00"));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var executor = new BlockingExecutor();
            var worker = Worker(executor, new LocalWorkerOptions(WorkerId: "floor-worker", VisibilityTimeout: TimeSpan.FromMinutes(10)));

            var run = worker.RunOnceAsync();
            await executor.WaitUntilExecutingAsync();

            var queueItem = Assert.Single(queue.List("job.visibility-floor"));
            Assert.Equal(At(25), queueItem.LockedUntilUtc);
            var sliceState = state.Get("job.visibility-floor", At(0), At(5));
            Assert.Equal(At(25), sliceState.LeaseExpiresAtUtc);

            executor.Complete(LocalSliceOutputResult.Success("test://visibility-floor"));
            Assert.True((await run).Succeeded);
        }

        [Fact]
        public async Task Worker_does_not_execute_when_queue_lease_extension_fails()
        {
            catalog.Create(Schedule("job.extension-lost", maxParallelism: 1, queryTimeout: "00:01:00"));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var executor = new RecordingExecutor();
            var expiringClock = new SequenceClock(At(15), At(16));
            var worker = new SqliteLocalWorker(
                catalog,
                state,
                queue,
                observability,
                executor,
                expiringClock,
                new LocalWorkerOptions(WorkerId: "expiring-worker", VisibilityTimeout: TimeSpan.FromTicks(1)));

            var run = await worker.RunOnceAsync();

            Assert.True(run.ClaimedWork);
            Assert.False(run.Executed);
            Assert.False(run.Succeeded);
            Assert.Equal("Queue lease lost before execution.", run.Reason);
            Assert.Empty(executor.Requests);
            Assert.Equal(DurableSliceStatus.Queued, state.Get("job.extension-lost", At(0), At(5)).Status);
        }

        [Fact]
        public async Task Expired_queue_lease_is_reclaimed_without_duplicate_completion()
        {
            catalog.Create(Schedule("job.lease", maxParallelism: 1));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var claimed = queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(1), clock.UtcNow);
            Assert.NotNull(claimed);
            clock.Advance(TimeSpan.FromMinutes(2));
            var executor = new RecordingExecutor();
            var worker = Worker(executor, new LocalWorkerOptions(WorkerId: "fresh-worker", VisibilityTimeout: TimeSpan.FromMinutes(5)));

            var result = await worker.RunOnceAsync();

            Assert.True(result.Succeeded);
            var item = queue.List("job.lease").Single();
            Assert.Equal(2, item.Attempts);
            Assert.Equal(DurableWorkQueueState.Completed, item.State);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.lease", At(0), At(5)).Status);
        }

        [Fact]
        public async Task Claimed_queue_item_without_slice_lease_is_reclaimed_after_process_exit()
        {
            catalog.Create(Schedule("job.claimed.crash", maxParallelism: 1));
            var scheduler = Scheduler(maxSlicesPerTick: 1);
            scheduler.Tick();
            var claimed = queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(1), clock.UtcNow);
            Assert.NotNull(claimed);
            Assert.Equal(0, scheduler.Tick().Enqueued);
            Assert.Single(queue.List("job.claimed.crash"));

            clock.Advance(TimeSpan.FromMinutes(2));
            var executor = new RecordingExecutor();
            var worker = Worker(executor, new LocalWorkerOptions(WorkerId: "fresh-worker", VisibilityTimeout: TimeSpan.FromMinutes(5)));
            var result = await worker.RunOnceAsync();

            Assert.True(result.Succeeded);
            Assert.Single(executor.Requests);
            var item = Assert.Single(queue.List("job.claimed.crash"));
            Assert.Equal(2, item.Attempts);
            Assert.Equal(DurableWorkQueueState.Completed, item.State);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.claimed.crash", At(0), At(5)).Status);
        }

        [Fact]
        public async Task Running_slice_is_reclaimed_after_worker_dies_before_executor_returns()
        {
            catalog.Create(Schedule("job.running.crash", maxParallelism: 1));
            var scheduler = Scheduler(maxSlicesPerTick: 1);
            scheduler.Tick();
            var blockedExecutor = new BlockingExecutor();
            var staleWorker = Worker(blockedExecutor, new LocalWorkerOptions(WorkerId: "stale-worker", VisibilityTimeout: TimeSpan.FromMinutes(1)));
            var staleRun = staleWorker.RunOnceAsync();
            await blockedExecutor.WaitUntilExecutingAsync();

            Assert.Equal(DurableSliceStatus.Running, state.Get("job.running.crash", At(0), At(5)).Status);
            var leasedItem = Assert.Single(queue.List("job.running.crash"));
            Assert.Equal(DurableWorkQueueState.Leased, leasedItem.State);
            Assert.Equal(1, leasedItem.Attempts);
            Assert.Equal(0, scheduler.Tick().Enqueued);
            Assert.Single(queue.List("job.running.crash"));

            clock.Advance(TimeSpan.FromMinutes(4));
            var recoveryExecutor = new RecordingExecutor();
            var freshWorker = Worker(recoveryExecutor, new LocalWorkerOptions(WorkerId: "fresh-worker", VisibilityTimeout: TimeSpan.FromMinutes(5)));
            var recovered = await freshWorker.RunOnceAsync();

            Assert.True(recovered.Succeeded);
            Assert.Single(recoveryExecutor.Requests);
            var completedItem = Assert.Single(queue.List("job.running.crash"));
            Assert.Equal(2, completedItem.Attempts);
            Assert.Equal(DurableWorkQueueState.Completed, completedItem.State);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.running.crash", At(0), At(5)).Status);

            blockedExecutor.Complete(LocalSliceOutputResult.Success("test://stale-worker-finished-late"));
            var staleResult = await staleRun;
            Assert.True(staleResult.Executed);
            Assert.False(staleResult.Succeeded);
            Assert.Equal("Slice lease lost before completion.", staleResult.Reason);
        }

        [Fact]
        public async Task Completed_slice_with_unfinished_queue_completion_is_not_reexecuted_after_reclaim()
        {
            catalog.Create(Schedule("job.complete.crash", maxParallelism: 1));
            Scheduler(maxSlicesPerTick: 1).Tick();
            var claimed = queue.Claim("default", "stale-worker", TimeSpan.FromMinutes(1), clock.UtcNow);
            Assert.NotNull(claimed);
            var lease = state.AcquireLease("lease-before-queue-completion-crash", "job.complete.crash", At(0), At(5), "stale-worker", TimeSpan.FromMinutes(1), clock.UtcNow);
            Assert.NotNull(lease);
            Assert.True(state.CompleteLease("complete-before-queue-completion-crash", "job.complete.crash", At(0), At(5), "stale-worker", lease.LeaseToken!, clock.UtcNow));

            clock.Advance(TimeSpan.FromMinutes(2));
            var executor = new RecordingExecutor();
            var worker = Worker(executor, new LocalWorkerOptions(WorkerId: "fresh-worker", VisibilityTimeout: TimeSpan.FromMinutes(5)));
            var result = await worker.RunOnceAsync();

            Assert.True(result.ClaimedWork);
            Assert.False(result.Executed);
            Assert.True(result.Succeeded);
            Assert.Empty(executor.Requests);
            var item = Assert.Single(queue.List("job.complete.crash"));
            Assert.Equal(2, item.Attempts);
            Assert.Equal(DurableWorkQueueState.Completed, item.State);
            Assert.Equal(DurableSliceStatus.Completed, state.Get("job.complete.crash", At(0), At(5)).Status);
        }

        private SqliteLocalScheduler Scheduler(int maxSlicesPerTick, IClock? schedulerClock = null) => new(catalog, state, queue, observability, schedulerClock ?? clock, new LocalSchedulerOptions(MaxSlicesPerTick: maxSlicesPerTick));
        private SqliteLocalWorker Worker(ILocalSliceOutputExecutor executor, LocalWorkerOptions? options = null, ILocalWorkerProgressSink? progressSink = null) => new(catalog, state, queue, observability, executor, clock, options, progressSink);

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string Schedule(string activityId, int maxParallelism, string? dependsOn = null, string queryTimeout = "00:01:00") => $$"""
        {
          "activityId": "{{activityId}}",
          "functionName": "LocalFunction",
          "outputTable": "LocalOutput",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": {{maxParallelism}},
          "queryTimeout": "{{queryTimeout}}",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;

        private sealed class RecordingExecutor : ILocalSliceOutputExecutor
        {
            private readonly Queue<LocalSliceOutputResult> results;
            public List<SliceRange> Requests { get; } = [];
            public RecordingExecutor(params LocalSliceOutputResult[] results) => this.results = new Queue<LocalSliceOutputResult>(results);
            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                Requests.Add(slice);
                return Task.FromResult(results.Count == 0 ? LocalSliceOutputResult.Success($"test://{slice.ToKey().Value}") : results.Dequeue());
            }
        }

        private sealed class BlockingExecutor : ILocalSliceOutputExecutor
        {
            private readonly TaskCompletionSource<SliceRange> executing = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<LocalSliceOutputResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<SliceRange> Requests { get; } = [];

            public Task WaitUntilExecutingAsync() => executing.Task.WaitAsync(TimeSpan.FromSeconds(5));

            public void Complete(LocalSliceOutputResult result) => completion.SetResult(result);

            public Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default)
            {
                Requests.Add(slice);
                executing.SetResult(slice);
                return completion.Task;
            }
        }

        private sealed class RecordingProgressSink : ILocalWorkerProgressSink
        {
            public List<LocalWorkerProgressEvent> Started { get; } = [];
            public List<LocalWorkerProgressEvent> Finished { get; } = [];

            public void RecordStarted(LocalWorkerProgressEvent progress) => Started.Add(progress);
            public void RecordFinished(LocalWorkerProgressEvent progress) => Finished.Add(progress);
        }

        private sealed class SequenceClock : IClock
        {
            private readonly DateTimeOffset[] values;
            private int index;

            public SequenceClock(params DateTimeOffset[] values) => this.values = values;

            public DateTimeOffset UtcNow
            {
                get
                {
                    if (values.Length == 0)
                    {
                        throw new InvalidOperationException("SequenceClock requires at least one value.");
                    }

                    var value = values[Math.Min(index, values.Length - 1)].ToUniversalTime();
                    index++;
                    return value;
                }
            }
        }
    }
}
