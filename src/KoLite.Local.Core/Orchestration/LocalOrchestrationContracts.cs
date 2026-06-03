using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Core.Orchestration
{
    public sealed record LocalSliceOutputResult(bool Succeeded, string? OutputReference, string? ErrorCode, string? ErrorMessage, bool IsRetryable)
    {
        public static LocalSliceOutputResult Success(string? outputReference = null) => new(true, outputReference, null, null, false);
        public static LocalSliceOutputResult Failure(string code, string message, bool isRetryable = true) => new(false, null, code, message, isRetryable);
    }

    public interface ILocalSliceOutputExecutor
    {
        Task<LocalSliceOutputResult> ExecuteAsync(JobDefinition job, SliceRange slice, CancellationToken cancellationToken = default);
    }

    public sealed record LocalSchedulerOptions(string QueueName = "default", int MaxSlicesPerTick = 100);

    public sealed record LocalSchedulerTickResult(int Enqueued, int DependencyBlocked, int SkippedCompleted, int SkippedMaxParallelism);

    public sealed record LocalWorkerOptions(
        string QueueName = "default",
        string WorkerId = "local-worker",
        TimeSpan? VisibilityTimeout = null,
        int MaxAttempts = 3,
        TimeSpan? InitialRetryDelay = null,
        TimeSpan? MaxRetryDelay = null,
        double BackoffFactor = 2.0)
    {
        public static TimeSpan QueryTimeoutLeaseBuffer { get; } = TimeSpan.FromMinutes(2);
        public TimeSpan EffectiveVisibilityTimeout => VisibilityTimeout ?? TimeSpan.FromMinutes(5);
        public TimeSpan EffectiveInitialRetryDelay => InitialRetryDelay ?? TimeSpan.FromSeconds(1);
        public TimeSpan EffectiveMaxRetryDelay => MaxRetryDelay ?? TimeSpan.FromMinutes(5);

        public TimeSpan EffectiveLeaseDuration(JobDefinition job)
        {
            ArgumentNullException.ThrowIfNull(job);
            var bufferedQueryTimeout = job.QueryTimeout > TimeSpan.MaxValue - QueryTimeoutLeaseBuffer
                ? TimeSpan.MaxValue
                : job.QueryTimeout + QueryTimeoutLeaseBuffer;
            return bufferedQueryTimeout > EffectiveVisibilityTimeout ? bufferedQueryTimeout : EffectiveVisibilityTimeout;
        }
    }

    public sealed record LocalWorkerRunResult(bool ClaimedWork, string? QueueItemId, bool Executed, bool Succeeded, bool DeadLettered, string? Reason);

    public enum LocalWorkerProgressStatus { Started, Succeeded, FailedRetryable, DeadLettered, LeaseLost }

    public sealed record LocalWorkerProgressEvent(
        string ActivityId,
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
        bool DeadLettered = false);

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
