using System.Text.Json;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;

namespace KoLite.Local.Sqlite.Orchestration
{
    public sealed class SqliteLocalScheduler
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly IClock clock;
        private readonly LocalSchedulerOptions options;

        public SqliteLocalScheduler(SqliteJobCatalogRepository catalog, SqliteSliceStateRepository state, SqliteWorkQueueRepository queue, SqliteOperationalReadModelRepository observability, IClock clock, LocalSchedulerOptions? options = null)
        {
            this.catalog = catalog; this.state = state; this.queue = queue; this.observability = observability; this.clock = clock; this.options = options ?? new LocalSchedulerOptions();
        }

        public LocalSchedulerTickResult Tick()
        {
            var jobs = catalog.List(enabledOnly: true);
            var jobsById = jobs.ToDictionary(j => j.JobId, j => j.Definition, StringComparer.Ordinal);
            var enqueued = 0; var blocked = 0; var completed = 0; var maxSkipped = 0;

            var completedSlices = state.ListCompletedSliceKeys();

            foreach (var record in jobs)
            {
                var job = record.Definition;
                var activeForJob = queue.CountActive(record.JobId, options.QueueName);
                var sliceStates = state.ListSliceStates(record.JobId);
                foreach (var slice in SliceEnumerator.EnumerateEligible(job, clock).OrderBy(s => s.StartUtc))
                {
                    if (enqueued >= options.MaxSlicesPerTick) break;
                    var current = sliceStates.TryGetValue(slice.StartUtc.ToUniversalTime(), out var existing)
                        ? existing
                        : new SliceSchedulingState(DurableSliceStatus.Missing, 0);
                    if (!CanSchedulerEnqueue(current.Status))
                    {
                        if (current.Status == DurableSliceStatus.Completed) completed++;
                        continue;
                    }

                    var readiness = DependencyReadinessEvaluator.Evaluate(job, slice, jobsById, completedSlices);
                    if (!readiness.IsReady)
                    {
                        if (current.Status == DurableSliceStatus.Missing)
                        {
                            state.Append(OperationId("dependency-blocked", slice), record.JobId, slice.StartUtc, slice.EndUtc, DurableSliceStatus.DependencyBlocked, expectedVersion: current.Version, reason: string.Join(",", readiness.MissingSlices.Select(s => s.Value)));
                            observability.RecordScheduledSlice(record.JobId, slice.StartUtc, slice.EndUtc, "DependencyBlocked", clock.UtcNow, slice.EndUtc);
                            observability.RecordLog("Information", "Slice blocked by dependencies.", "scheduler", record.JobId, slice.StartUtc, slice.EndUtc, JsonSerializer.Serialize(new { activityId = record.ActivityId, missing = readiness.MissingSlices.Select(s => s.Value).ToArray() }));
                        }
                        blocked++;
                        continue;
                    }

                    if (activeForJob >= Math.Max(1, job.MaxParallelism))
                    {
                        maxSkipped++;
                        break;
                    }

                    var expected = current.Version;
                    if (current.Status is DurableSliceStatus.Missing or DurableSliceStatus.DependencyBlocked)
                    {
                        state.Append(OperationId("queued", slice), record.JobId, slice.StartUtc, slice.EndUtc, DurableSliceStatus.Queued, expected, actor: "scheduler");
                    }

                    queue.Enqueue(record.JobId, slice.StartUtc, slice.EndUtc, IdempotencyKey(slice), clock.UtcNow, queueName: options.QueueName, maxAttempts: 3, payloadJson: JsonSerializer.Serialize(new { jobId = record.JobId, sliceKey = slice.ToKey().Value }));
                    observability.RecordScheduledSlice(record.JobId, slice.StartUtc, slice.EndUtc, "Queued", clock.UtcNow, clock.UtcNow);
                    observability.RecordLog("Information", "Slice enqueued.", "scheduler", record.JobId, slice.StartUtc, slice.EndUtc);
                    enqueued++; activeForJob++;
                }
            }

            return new LocalSchedulerTickResult(enqueued, blocked, completed, maxSkipped);
        }

        private static string IdempotencyKey(SliceRange slice) => $"normal|{slice.ToKey().Value}";
        private static string OperationId(string prefix, SliceRange slice) => $"{prefix}|{slice.ToKey().Value}";
        private static bool CanSchedulerEnqueue(DurableSliceStatus status) => status is DurableSliceStatus.Missing or DurableSliceStatus.DependencyBlocked;
    }

    public sealed class SqliteLocalWorker
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteOperationalReadModelRepository observability;
        private readonly ILocalSliceOutputExecutor executor;
        private readonly IClock clock;
        private readonly LocalWorkerOptions options;
        private readonly ILocalWorkerProgressSink progressSink;

        public SqliteLocalWorker(SqliteJobCatalogRepository catalog, SqliteSliceStateRepository state, SqliteWorkQueueRepository queue, SqliteOperationalReadModelRepository observability, ILocalSliceOutputExecutor executor, IClock clock, LocalWorkerOptions? options = null, ILocalWorkerProgressSink? progressSink = null)
        {
            this.catalog = catalog;
            this.state = state;
            this.queue = queue;
            this.observability = observability;
            this.executor = executor;
            this.clock = clock;
            this.options = options ?? new LocalWorkerOptions();
            this.progressSink = progressSink ?? NullLocalWorkerProgressSink.Instance;
        }

        public async Task<LocalWorkerRunResult> RunOnceAsync(CancellationToken cancellationToken = default, bool includeExpiredLeases = true)
        {
            var claimStartedAtUtc = clock.UtcNow;
            var item = includeExpiredLeases
                ? queue.Claim(options.QueueName, options.WorkerId, options.EffectiveVisibilityTimeout, claimStartedAtUtc, options.EnforceJobParallelism, options.EffectiveOrphanReclaimGrace)
                : queue.ClaimQueued(options.QueueName, options.WorkerId, options.EffectiveVisibilityTimeout, claimStartedAtUtc, options.EnforceJobParallelism);
            if (item is null) return new LocalWorkerRunResult(false, null, false, false, false, null);

            var jobRecord = catalog.Get(item.JobId);
            if (jobRecord is null)
            {
                queue.DeadLetter(item.QueueItemId, options.WorkerId);
                return new LocalWorkerRunResult(true, item.QueueItemId, false, false, true, "Job definition missing.");
            }

            if (!jobRecord.IsEnabled)
            {
                var deferred = queue.DeferWithoutAttempt(item.QueueItemId, options.WorkerId, clock.UtcNow);
                observability.RecordLog(
                    deferred ? "Information" : "Warning",
                    deferred ? "Slice deferred because the job is paused." : "Slice pause defer lost queue lease.",
                    "worker",
                    item.JobId,
                    item.SliceStartUtc,
                    item.SliceEndUtc);
                return new LocalWorkerRunResult(true, item.QueueItemId, false, false, false, deferred ? "Job is paused." : "Queue lease lost before pause defer.");
            }

            var slice = new SliceRange(item.JobId, item.SliceStartUtc, item.SliceEndUtc);
            var leaseStartedAtUtc = clock.UtcNow;
            var leaseDuration = options.EffectiveLeaseDuration(jobRecord.Definition);
            var leaseExpiresAtUtc = LeaseExpiresAt(leaseStartedAtUtc, leaseDuration);
            if (!queue.ExtendLease(item.QueueItemId, options.WorkerId, leaseExpiresAtUtc, leaseStartedAtUtc))
            {
                observability.RecordLog("Warning", "Queue lease lost before execution.", "worker", item.JobId, item.SliceStartUtc, item.SliceEndUtc);
                return new LocalWorkerRunResult(true, item.QueueItemId, false, false, false, "Queue lease lost before execution.");
            }

            var leaseOperation = $"dispatch|{item.QueueItemId}|{item.Attempts}";
            var lease = state.AcquireLease(leaseOperation, item.JobId, item.SliceStartUtc, item.SliceEndUtc, options.WorkerId, leaseDuration, leaseStartedAtUtc);
            if (lease is null)
            {
                var current = state.Get(item.JobId, item.SliceStartUtc, item.SliceEndUtc);
                if (current.Status == DurableSliceStatus.Completed) queue.Complete(item.QueueItemId, options.WorkerId); else queue.Abandon(item.QueueItemId, options.WorkerId, clock.UtcNow + options.EffectiveInitialRetryDelay);
                return new LocalWorkerRunResult(true, item.QueueItemId, false, current.Status == DurableSliceStatus.Completed, false, "Slice lease unavailable.");
            }

            var startedAtUtc = clock.UtcNow;
            var progress = new LocalWorkerProgressEvent(item.JobId, item.QueueItemId, item.SliceStartUtc, item.SliceEndUtc, item.Attempts, options.WorkerId, LocalWorkerProgressStatus.Started, startedAtUtc, ClusterUri: jobRecord.Definition.Target.ClusterUri);
            observability.RecordAttempt(AttemptId(item), item.JobId, item.SliceStartUtc, item.SliceEndUtc, item.Attempts, "Started", options.WorkerId, startedAtUtc, null);
            observability.RecordLog("Information", "Slice dispatched.", "worker", item.JobId, item.SliceStartUtc, item.SliceEndUtc);
            progressSink.RecordStarted(progress);

            var executionTimeout = options.EffectiveExecutionTimeout(jobRecord.Definition);
            LocalSliceOutputResult result;
            using (var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                if (executionTimeout < TimeSpan.MaxValue)
                {
                    executionCts.CancelAfter(executionTimeout);
                }

                try
                {
                    result = await executor.ExecuteAsync(jobRecord.Definition, slice, executionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Host shutdown / emergency cancellation: leave the lease in place and record no
                    // terminal state. Normal expired-lease recovery picks the slice back up later.
                    throw;
                }
                catch (Exception ex)
                {
                    // Any other fault (including our own execution-timeout cancellation) happens after
                    // the Kusto append may have already succeeded, but we cannot confirm it. Release the
                    // lease immediately and retry instead of leaving an orphaned Leased/Running row; the
                    // re-run is idempotent (ingest-by), so it never duplicates output.
                    var faultedAtUtc = clock.UtcNow;
                    var timedOut = executionCts.IsCancellationRequested;
                    var code = timedOut ? "WorkerExecutionTimeout" : "WorkerFaulted";
                    var message = timedOut
                        ? $"Slice execution exceeded the {executionTimeout} execution timeout."
                        : ex.Message;
                    observability.RecordLog(
                        "Warning",
                        timedOut ? "Slice execution timed out; releasing the lease for retry." : "Slice execution faulted; releasing the lease for retry.",
                        "worker",
                        item.JobId,
                        item.SliceStartUtc,
                        item.SliceEndUtc,
                        JsonSerializer.Serialize(new { code, message }),
                        ex.ToString());
                    return FailSlice(item, lease, progress, code, message, isRetryable: true, faultedAtUtc);
                }
            }

            if (result.Succeeded)
            {
                var payload = JsonSerializer.Serialize(new { outputReference = result.OutputReference });
                var completedAtUtc = clock.UtcNow;
                var completed = state.CompleteLease($"complete|{item.QueueItemId}|{item.Attempts}", item.JobId, item.SliceStartUtc, item.SliceEndUtc, options.WorkerId, lease.LeaseToken!, completedAtUtc, payload);
                if (completed) queue.Complete(item.QueueItemId, options.WorkerId);
                observability.RecordAttempt(AttemptId(item), item.JobId, item.SliceStartUtc, item.SliceEndUtc, item.Attempts, completed ? "Succeeded" : "LeaseLost", options.WorkerId, null, completedAtUtc);
                observability.RecordLog("Information", completed ? "Slice completed." : "Slice completion lost lease.", "worker", item.JobId, item.SliceStartUtc, item.SliceEndUtc);
                progressSink.RecordFinished(progress with
                {
                    Status = completed ? LocalWorkerProgressStatus.Succeeded : LocalWorkerProgressStatus.LeaseLost,
                    CompletedAtUtc = completedAtUtc,
                    ErrorCode = completed ? null : "LeaseLost",
                    ErrorMessage = completed ? null : "Slice lease lost before completion."
                });
                return new LocalWorkerRunResult(true, item.QueueItemId, true, completed, false, completed ? null : "Slice lease lost before completion.");
            }

            return FailSlice(item, lease, progress, result.ErrorCode, result.ErrorMessage, result.IsRetryable, clock.UtcNow);
        }

        // Records a non-success terminal outcome for the current attempt and releases the queue item.
        // Shared by the executor-reported failure path and the unhandled-fault/timeout catch so both
        // promptly Abandon (retry) or DeadLetter the slice instead of leaving the lease to expire.
        private LocalWorkerRunResult FailSlice(DurableWorkItem item, DurableSliceState lease, LocalWorkerProgressEvent progress, string? errorCode, string? errorMessage, bool isRetryable, DateTimeOffset failedAtUtc)
        {
            var reason = string.IsNullOrWhiteSpace(errorMessage) ? errorCode ?? "Execution failed." : errorMessage!;
            var shouldRetry = isRetryable && item.Attempts < Math.Max(1, options.MaxAttempts);
            var payloadJson = JsonSerializer.Serialize(new { ErrorCode = errorCode, ErrorMessage = errorMessage, IsRetryable = isRetryable });
            if (shouldRetry)
            {
                state.FailLease($"fail|{item.QueueItemId}|{item.Attempts}", item.JobId, item.SliceStartUtc, item.SliceEndUtc, options.WorkerId, lease.LeaseToken!, failedAtUtc, reason, payloadJson);
                queue.Abandon(item.QueueItemId, options.WorkerId, failedAtUtc + RetryDelay(item.Attempts));
                observability.RecordAttempt(AttemptId(item), item.JobId, item.SliceStartUtc, item.SliceEndUtc, item.Attempts, "FailedRetryable", options.WorkerId, null, failedAtUtc, errorCode, errorMessage);
                observability.RecordLog("Warning", "Slice failed and was scheduled for retry.", "worker", item.JobId, item.SliceStartUtc, item.SliceEndUtc, payloadJson);
                progressSink.RecordFinished(progress with
                {
                    Status = LocalWorkerProgressStatus.FailedRetryable,
                    CompletedAtUtc = failedAtUtc,
                    ErrorCode = errorCode,
                    ErrorMessage = errorMessage,
                    IsRetryable = isRetryable
                });
                return new LocalWorkerRunResult(true, item.QueueItemId, true, false, false, reason);
            }

            state.DeadLetterLease($"deadletter|{item.QueueItemId}|{item.Attempts}", item.JobId, item.SliceStartUtc, item.SliceEndUtc, options.WorkerId, lease.LeaseToken!, failedAtUtc, reason, payloadJson);
            queue.DeadLetter(item.QueueItemId, options.WorkerId);
            observability.RecordAttempt(AttemptId(item), item.JobId, item.SliceStartUtc, item.SliceEndUtc, item.Attempts, "DeadLettered", options.WorkerId, null, failedAtUtc, errorCode, errorMessage);
            observability.RecordLog("Error", "Slice dead-lettered.", "worker", item.JobId, item.SliceStartUtc, item.SliceEndUtc, payloadJson);
            progressSink.RecordFinished(progress with
            {
                Status = LocalWorkerProgressStatus.DeadLettered,
                CompletedAtUtc = failedAtUtc,
                ErrorCode = errorCode,
                ErrorMessage = errorMessage,
                IsRetryable = isRetryable,
                DeadLettered = true
            });
            return new LocalWorkerRunResult(true, item.QueueItemId, true, false, true, reason);
        }

        private TimeSpan RetryDelay(int attempt)
        {
            var multiplier = Math.Pow(options.BackoffFactor, Math.Max(0, attempt - 1));
            var ticks = (long)Math.Min(options.EffectiveMaxRetryDelay.Ticks, options.EffectiveInitialRetryDelay.Ticks * multiplier);
            return TimeSpan.FromTicks(ticks);
        }

        private static DateTimeOffset LeaseExpiresAt(DateTimeOffset leaseStartedAtUtc, TimeSpan leaseDuration) =>
            DateTimeOffset.MaxValue - leaseStartedAtUtc.ToUniversalTime() < leaseDuration
                ? DateTimeOffset.MaxValue
                : leaseStartedAtUtc.ToUniversalTime() + leaseDuration;

        private static string AttemptId(DurableWorkItem item) => $"{item.QueueItemId}:{item.Attempts}";
    }
}
