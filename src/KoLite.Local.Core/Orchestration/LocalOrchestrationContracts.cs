using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Core.Orchestration
{
    // IsPermanent, FailureCode and FailureSubCode are diagnostic detail carried from the Kusto
    // SDK so operators can see why a slice was or was not retried. They are null when the
    // failure did not originate from a Kusto exception.
    public sealed record LocalSliceOutputResult(bool Succeeded, string? OutputReference, string? ErrorCode, string? ErrorMessage, bool IsRetryable, bool? IsPermanent = null, int? FailureCode = null, string? FailureSubCode = null, string? ClientRequestId = null, bool? DuplicateSuppressed = null)
    {
        public static LocalSliceOutputResult Success(string? outputReference = null) => new(true, outputReference, null, null, false);
        public static LocalSliceOutputResult Failure(string code, string message, bool isRetryable = true, bool? isPermanent = null, int? failureCode = null, string? failureSubCode = null) =>
            new(false, null, code, message, isRetryable, isPermanent, failureCode, failureSubCode);
    }

    public sealed record LocalSliceAttemptContext(string AttemptId, string ClientRequestId);

    public interface ILocalSliceOutputExecutor
    {
        Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default);

        Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceExecutionUnit execution, CancellationToken cancellationToken = default) =>
            ExecuteAsync(job, execution.Slice, cancellationToken);

        Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceExecutionUnit execution, LocalSliceAttemptContext attempt, CancellationToken cancellationToken = default) =>
            ExecuteAsync(job, execution, cancellationToken);
    }

    // MaxSlicesPerTick defaults to unbounded: a pass is already naturally bounded to the sum of each
    // job's MaxParallelism (the scheduler stops topping up a job once it reaches that). Per-job
    // MaxParallelism is the concurrency lever, so no global per-tick throttle is applied by default.
    public sealed record LocalSchedulerOptions(string QueueName = "default", int MaxSlicesPerTick = int.MaxValue);

    public sealed record LocalSchedulerTickResult(int Enqueued, int DependencyBlocked, int SkippedCompleted, int SkippedMaxParallelism);

    public sealed record LocalWorkerOptions(
        string QueueName = "default",
        string WorkerId = "local-worker",
        TimeSpan? VisibilityTimeout = null,
        int MaxAttempts = 3,
        TimeSpan? InitialRetryDelay = null,
        TimeSpan? MaxRetryDelay = null,
        double BackoffFactor = 2.0,
        bool EnforceJobParallelism = false,
        TimeSpan? ClientTimeoutBuffer = null,
        TimeSpan? OrphanReclaimGrace = null)
    {
        public static TimeSpan QueryTimeoutLeaseBuffer { get; } = TimeSpan.FromMinutes(2);
        public TimeSpan EffectiveVisibilityTimeout => VisibilityTimeout ?? TimeSpan.FromMinutes(5);
        public TimeSpan EffectiveInitialRetryDelay => InitialRetryDelay ?? TimeSpan.FromMinutes(1);
        public TimeSpan EffectiveMaxRetryDelay => MaxRetryDelay ?? TimeSpan.FromMinutes(5);

        // Extra time granted to the client-side execution deadline beyond the job's Kusto server
        // timeout, so the server's own timeout error can surface first. Kept below the lease buffer.
        public TimeSpan EffectiveClientTimeoutBuffer => ClientTimeoutBuffer ?? TimeSpan.FromSeconds(30);

        // A lease must be expired by at least this margin before another worker reclaims it, to
        // tolerate clock skew and a single missed dispatch cycle. Defense-in-depth for orphan recovery.
        public TimeSpan EffectiveOrphanReclaimGrace => OrphanReclaimGrace ?? TimeSpan.FromSeconds(30);

        public TimeSpan EffectiveLeaseDuration(JobDefinition job)
        {
            ArgumentNullException.ThrowIfNull(job);
            var bufferedQueryTimeout = job.QueryTimeout > TimeSpan.MaxValue - QueryTimeoutLeaseBuffer
                ? TimeSpan.MaxValue
                : job.QueryTimeout + QueryTimeoutLeaseBuffer;
            return bufferedQueryTimeout > EffectiveVisibilityTimeout ? bufferedQueryTimeout : EffectiveVisibilityTimeout;
        }

        // Client-side wall-clock deadline for a single execution attempt. A hung or excessively slow
        // Kusto call becomes a cancellation the worker can release and retry, instead of holding the
        // lease until it expires. Always kept strictly below the lease duration so a healthy attempt
        // can record its terminal state before the lease becomes reclaimable by another worker.
        public TimeSpan EffectiveExecutionTimeout(JobDefinition job)
        {
            ArgumentNullException.ThrowIfNull(job);
            var lease = EffectiveLeaseDuration(job);
            var buffer = EffectiveClientTimeoutBuffer;
            var candidate = job.QueryTimeout > TimeSpan.MaxValue - buffer
                ? TimeSpan.MaxValue
                : job.QueryTimeout + buffer;
            return candidate < lease ? candidate : lease;
        }
    }

    public sealed record LocalWorkerRunResult(bool ClaimedWork, string? QueueItemId, bool Executed, bool Succeeded, bool DeadLettered, string? Reason);

    public enum LocalWorkerProgressStatus { Started, Succeeded, FailedRetryable, DeadLettered, LeaseLost }

    public sealed record LocalWorkerProgressEvent(
        string JobId,
        string QueueItemId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        string WorkerId,
        LocalWorkerProgressStatus Status,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset? CompletedAtUtc = null,
        string? ErrorCode = null,
        string? ErrorMessage = null,
        bool IsRetryable = false,
        bool DeadLettered = false,
        // Human-facing job label (the job's ActivityId) resolved when the event is raised.
        // Used for readable log rendering; the opaque JobId stays the durable diagnostic key.
        string? DisplayName = null,
        int? ChunkId = null,
        int? TotalChunks = null);

    public interface ILocalWorkerProgressSink
    {
        void RecordStarted(LocalWorkerProgressEvent progress);
        void RecordFinished(LocalWorkerProgressEvent progress);
    }

    public sealed class NullLocalWorkerProgressSink : ILocalWorkerProgressSink
    {
        public static NullLocalWorkerProgressSink Instance { get; } = new();

        private NullLocalWorkerProgressSink()
        {
        }

        public void RecordStarted(LocalWorkerProgressEvent progress)
        {
        }

        public void RecordFinished(LocalWorkerProgressEvent progress)
        {
        }
    }
}
