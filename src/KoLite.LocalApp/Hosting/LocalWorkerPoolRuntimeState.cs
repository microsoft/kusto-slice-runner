// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp
{
    internal sealed class LocalWorkerPoolRuntimeState
    {
        private readonly object gate = new();
        private int activeWorkerCount;
        private string[] activeWorkerIds = [];
        private long dispatchCycles;
        private long idleCycles;
        private long saturatedCycles;
        private long starts;
        private long succeeded;
        private long retryableFailures;
        private long deadLettered;
        private long faulted;
        private DateTimeOffset? lastUpdatedAtUtc;

        public void RecordDispatchCycle(int activeWorkers, IReadOnlyList<string> workerIds, int started, bool isIdle, bool isSaturated, DateTimeOffset recordedAtUtc)
        {
            lock (gate)
            {
                activeWorkerCount = activeWorkers;
                activeWorkerIds = workerIds.Order(StringComparer.Ordinal).ToArray();
                starts += started;
                dispatchCycles++;
                if (isIdle) idleCycles++;
                if (isSaturated) saturatedCycles++;
                lastUpdatedAtUtc = recordedAtUtc;
            }
        }

        public void RecordWorkerResult(WorkerPassResult result, DateTimeOffset recordedAtUtc)
        {
            lock (gate)
            {
                if (result.ClaimedWork)
                {
                    if (result.Succeeded) succeeded++;
                    else if (result.DeadLettered) deadLettered++;
                    else retryableFailures++;
                }

                lastUpdatedAtUtc = recordedAtUtc;
            }
        }

        public void RecordWorkerFault(DateTimeOffset recordedAtUtc)
        {
            lock (gate)
            {
                faulted++;
                lastUpdatedAtUtc = recordedAtUtc;
            }
        }

        public WorkerPoolSnapshot GetSnapshot(LocalBackgroundWorkerPoolOptions options, QueueStatusSummary queueStatus, int claimableBacklog)
        {
            lock (gate)
            {
                var activeQueueRows = queueStatus.QueuedCount + queueStatus.LeasedCount;
                var unbounded = options.MaxConcurrencyUnbounded;
                int? maxConcurrency = unbounded ? null : options.MaxConcurrency;
                int? availableSlots = unbounded ? null : Math.Max(0, options.MaxConcurrency - activeWorkerCount);
                var isSaturated = !unbounded && activeWorkerCount >= options.MaxConcurrency && claimableBacklog > 0;
                return new WorkerPoolSnapshot(
                    options.Mode,
                    options.Enabled,
                    options.EnabledSource,
                    maxConcurrency,
                    options.MaxConcurrencySource,
                    options.MaxConcurrencyDisplay,
                    options.IdleDelay.ToString(),
                    options.IdleDelaySource,
                    options.MaxDispatchStartsPerCycle,
                    options.MaxDispatchStartsPerCycleSource,
                    activeWorkerCount,
                    availableSlots,
                    claimableBacklog,
                    activeQueueRows,
                    queueStatus.QueuedCount,
                    queueStatus.LeasedCount,
                    queueStatus.ExpiredLeaseCount,
                    activeWorkerCount == 0 && claimableBacklog == 0,
                    isSaturated,
                    dispatchCycles,
                    idleCycles,
                    saturatedCycles,
                    starts,
                    succeeded,
                    retryableFailures,
                    deadLettered,
                    faulted,
                    activeWorkerIds,
                    lastUpdatedAtUtc);
            }
        }
    }
}
