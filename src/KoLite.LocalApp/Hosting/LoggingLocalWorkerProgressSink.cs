using KoLite.Local.Core.Orchestration;
using KoLite.Local.Sqlite.Throttling;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp
{
    internal sealed class LoggingLocalWorkerProgressSink : ILocalWorkerProgressSink
    {
        private readonly ILogger<LoggingLocalWorkerProgressSink> logger;
        private readonly IngestionThrottleObserver throttleObserver;

        public LoggingLocalWorkerProgressSink(
            ILogger<LoggingLocalWorkerProgressSink> logger,
            IngestionThrottleObserver throttleObserver)
        {
            this.logger = logger;
            this.throttleObserver = throttleObserver;
        }

        public void RecordStarted(LocalWorkerProgressEvent progress)
        {
            logger.LogInformation(
                "Job slice started for job {JobId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}.",
                progress.JobId,
                progress.SliceStartUtc,
                progress.SliceEndUtc,
                progress.Attempt);
        }

        public void RecordFinished(LocalWorkerProgressEvent progress)
        {
            TryRecordIngestionThrottle(progress);

            if (progress.Status == LocalWorkerProgressStatus.Succeeded)
            {
                logger.LogInformation(
                    "Job slice finished for job {JobId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}.",
                    progress.JobId,
                    progress.SliceStartUtc,
                    progress.SliceEndUtc,
                    progress.Attempt,
                    progress.Status);
                return;
            }

            if (progress.Status == LocalWorkerProgressStatus.DeadLettered)
            {
                logger.LogError(
                    "Job slice finished for job {JobId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                    progress.JobId,
                    progress.SliceStartUtc,
                    progress.SliceEndUtc,
                    progress.Attempt,
                    progress.Status,
                    progress.ErrorCode,
                    progress.ErrorMessage);
                return;
            }

            logger.LogWarning(
                "Job slice finished for job {JobId}: slice {SliceStartUtc:O} to {SliceEndUtc:O}, attempt {Attempt}, status {CompletionStatus}, error {ErrorCode}: {ErrorMessage}.",
                progress.JobId,
                progress.SliceStartUtc,
                progress.SliceEndUtc,
                progress.Attempt,
                progress.Status,
                progress.ErrorCode,
                progress.ErrorMessage);
        }

        // Records an observation when a slice attempt failed specifically because of Kusto
        // ingestion-capacity throttling (429, CapacityPolicy/Ingestion). Best-effort and isolated:
        // a recording failure is logged but never propagated, so detection cannot destabilize the
        // worker. The observer ignores non-throttle outcomes and events without a resolved cluster.
        private void TryRecordIngestionThrottle(LocalWorkerProgressEvent progress)
        {
            try
            {
                throttleObserver.Observe(progress);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record ingestion throttle observation for job {JobId}.", progress.JobId);
            }
        }
    }
}
