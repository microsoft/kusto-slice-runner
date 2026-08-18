using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Throttling;

namespace KoLite.Local.Sqlite.Throttling
{
    // Bridges a worker progress event to the throttle store: records an observation only when a slice
    // attempt failed because of Kusto ingestion-capacity throttling and the cluster is known. Pure
    // decision + single insert, kept public so the detection rules are unit-testable without the
    // (internal) progress sink. Callers own error isolation so a recording fault never breaks a worker.
    public sealed class IngestionThrottleObserver
    {
        private readonly SqliteIngestionThrottleRepository store;

        public IngestionThrottleObserver(SqliteIngestionThrottleRepository store)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // Returns true when an observation was recorded.
        public bool Observe(LocalWorkerProgressEvent progress)
        {
            ArgumentNullException.ThrowIfNull(progress);
            if (string.IsNullOrWhiteSpace(progress.ClusterUri))
            {
                return false;
            }

            var classification = IngestionThrottleClassifier.Classify(progress.ErrorCode, progress.ErrorMessage);
            if (!classification.IsIngestionCapacityThrottle)
            {
                return false;
            }

            store.Record(new IngestionThrottleObservation(
                progress.JobId,
                progress.ClusterUri!,
                progress.SliceStartUtc,
                progress.SliceEndUtc,
                progress.Attempt,
                classification.ReportedCapacity,
                progress.CompletedAtUtc ?? progress.StartedAtUtc,
                progress.Status == LocalWorkerProgressStatus.DeadLettered,
                progress.ChunkId,
                progress.TotalChunks));
            return true;
        }
    }
}
