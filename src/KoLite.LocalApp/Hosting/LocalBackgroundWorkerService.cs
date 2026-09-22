// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp
{
    internal sealed class LocalBackgroundWorkerService : BackgroundService
    {
        private readonly IServiceScopeFactory scopes;
        private readonly LocalBackgroundWorkerPoolOptions options;
        private readonly LocalWorkerPoolRuntimeState workerPoolState;
        private readonly LocalWorkerOptions localWorkerOptions;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly IClock clock;
        private readonly ILogger<LocalBackgroundWorkerService> logger;
        private readonly List<InFlightWorker> inFlightWorkers = [];

        public LocalBackgroundWorkerService(
            IServiceScopeFactory scopes,
            LocalBackgroundWorkerPoolOptions options,
            LocalWorkerPoolRuntimeState workerPoolState,
            LocalWorkerOptions localWorkerOptions,
            LocalShutdownDrainCoordinator shutdownDrain,
            IClock clock,
            ILogger<LocalBackgroundWorkerService> logger)
        {
            this.scopes = scopes;
            this.options = options;
            this.workerPoolState = workerPoolState;
            this.localWorkerOptions = localWorkerOptions;
            this.shutdownDrain = shutdownDrain;
            this.clock = clock;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Enabled)
            {
                shutdownDrain.NotifyWorkerDispatcherDrained(clock.UtcNow);
                return;
            }

            using var executionCancellation = new CancellationTokenSource();
            using var stopRegistration = stoppingToken.Register(() =>
            {
                if (!shutdownDrain.IsStoppingAfterDrain)
                {
                    executionCancellation.Cancel();
                }
            });
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await RemoveCompletedWorkersAsync(stoppingToken).ConfigureAwait(false);
                    if (shutdownDrain.IsDrainRequested)
                    {
                        if (inFlightWorkers.Count == 0)
                        {
                            shutdownDrain.NotifyWorkerDispatcherDrained(clock.UtcNow);
                            break;
                        }

                        await WaitForWorkerOrIdleDelayAsync(stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        await RunDispatchCycleAsync(executionCancellation.Token, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // Keep the dispatcher alive across a transient failure in the dispatch cycle
                        // (e.g. a SQLite read error while counting claimable work). Without this the
                        // default BackgroundServiceExceptionBehavior = StopHost would stop the whole
                        // host. Log, back off one idle delay, and continue.
                        logger.LogError(ex, "Local worker dispatch cycle failed; the dispatcher will continue on the next cycle.");
                        try
                        {
                            await Task.Delay(options.IdleDelay, stoppingToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            finally
            {
                await DrainInFlightWorkersAsync().ConfigureAwait(false);
            }
        }

        private async Task RunDispatchCycleAsync(CancellationToken executionToken, CancellationToken stoppingToken)
        {
            var availableSlots = Math.Max(0, options.MaxConcurrency - inFlightWorkers.Count);
            var started = 0;
            // Always include expired (orphaned) leases when claiming and counting, not only when
            // the pool is fully idle. Workstream A guarantees a healthy attempt finishes (or its
            // bounded execution times out and is released) before its lease can expire beyond the
            // reclaim grace, so an expired lease reliably means a crashed/abandoned owner. This
            // lets orphans recover on the next dispatch cycle instead of waiting for a fully-idle
            // pool, while the grace margin and the single-row claim guard prevent stealing a
            // healthy, still-held lease.
            const bool includeExpiredLeases = true;
            var queueSnapshot = ReadQueueSnapshot(includeExpiredLeases);
            if (availableSlots > 0 && queueSnapshot.ClaimableBacklog > 0)
            {
                var workersToStart = Math.Min(Math.Min(availableSlots, options.MaxDispatchStartsPerCycle), queueSnapshot.ClaimableBacklog);
                foreach (var slotNumber in AvailableWorkerSlots().Take(workersToStart))
                {
                    var workerId = WorkerIdForSlot(slotNumber);
                    inFlightWorkers.Add(new InFlightWorker(
                        slotNumber,
                        workerId,
                        RunTrackedWorkerOnceAsync(workerId, includeExpiredLeases, executionToken)));
                    started++;
                }

                if (started > 0)
                {
                    logger.LogDebug("Local worker dispatcher started {Started} workers with {InFlight} in flight.", started, inFlightWorkers.Count);
                }
            }

            var activeWorkerIds = inFlightWorkers.Select(worker => worker.WorkerId).ToArray();
            var isIdle = inFlightWorkers.Count == 0 && queueSnapshot.ClaimableBacklog == 0 && started == 0;
            var isSaturated = inFlightWorkers.Count >= options.MaxConcurrency && queueSnapshot.ClaimableBacklog > started;
            workerPoolState.RecordDispatchCycle(
                inFlightWorkers.Count,
                activeWorkerIds,
                started,
                isIdle,
                isSaturated,
                clock.UtcNow);
            if (options.LogEveryPass)
            {
                LogWorkerDispatch(queueSnapshot, started, inFlightWorkers.Count, includeExpiredLeases, isIdle, isSaturated, activeWorkerIds);
            }

            if (started == 0)
            {
                await WaitForWorkerOrIdleDelayAsync(stoppingToken).ConfigureAwait(false);
            }
        }

        private WorkerQueueSnapshot ReadQueueSnapshot(bool includeExpiredLeases)
        {
            using var scope = scopes.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<SqliteWorkQueueRepository>();
            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
            var scopedClock = scope.ServiceProvider.GetRequiredService<IClock>();
            var nowUtc = scopedClock.UtcNow;
            var claimable = includeExpiredLeases
                ? queue.CountClaimable(localWorkerOptions.QueueName, nowUtc, localWorkerOptions.EnforceJobParallelism, localWorkerOptions.EffectiveOrphanReclaimGrace)
                : queue.CountQueuedClaimable(localWorkerOptions.QueueName, nowUtc, localWorkerOptions.EnforceJobParallelism);
            var queueStatus = observability.GetQueueStatus(localWorkerOptions.QueueName, nowUtc);
            return new WorkerQueueSnapshot(
                claimable,
                queueStatus.QueuedCount + queueStatus.LeasedCount,
                queueStatus.QueuedCount,
                queueStatus.LeasedCount,
                queueStatus.ExpiredLeaseCount);
        }

        private async Task<WorkerPassResult> RunWorkerOnceAsync(string workerId, bool includeExpiredLeases, CancellationToken cancellationToken)
        {
            using var scope = scopes.CreateScope();
            var worker = scope.ServiceProvider.GetRequiredService<LocalWorkerFactory>().Create(workerId);
            var run = await worker.RunOnceAsync(cancellationToken, includeExpiredLeases).ConfigureAwait(false);
            return new WorkerPassResult(run.ClaimedWork, run.Succeeded, run.DeadLettered);
        }

        private async Task<WorkerPassResult> RunTrackedWorkerOnceAsync(string workerId, bool includeExpiredLeases, CancellationToken cancellationToken)
        {
            shutdownDrain.RecordWorkerStarted(clock.UtcNow);
            try
            {
                return await RunWorkerOnceAsync(workerId, includeExpiredLeases, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                shutdownDrain.RecordWorkerCompleted(clock.UtcNow);
            }
        }

        private async Task RemoveCompletedWorkersAsync(CancellationToken cancellationToken)
        {
            for (var i = inFlightWorkers.Count - 1; i >= 0; i--)
            {
                var worker = inFlightWorkers[i];
                if (!worker.Task.IsCompleted)
                {
                    continue;
                }

                inFlightWorkers.RemoveAt(i);
                await ObserveWorkerCompletionAsync(worker, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task WaitForWorkerOrIdleDelayAsync(CancellationToken cancellationToken)
        {
            var delay = Task.Delay(options.IdleDelay, cancellationToken);
            if (inFlightWorkers.Count == 0)
            {
                await delay.ConfigureAwait(false);
                return;
            }

            var completed = await Task.WhenAny(inFlightWorkers.Select(worker => (Task)worker.Task).Append(delay)).ConfigureAwait(false);
            if (ReferenceEquals(completed, delay))
            {
                await delay.ConfigureAwait(false);
                return;
            }

            await RemoveCompletedWorkersAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task DrainInFlightWorkersAsync()
        {
            if (inFlightWorkers.Count == 0)
            {
                return;
            }

            foreach (var worker in inFlightWorkers.ToArray())
            {
                await ObserveWorkerCompletionDuringDrainAsync(worker).ConfigureAwait(false);
            }

            inFlightWorkers.Clear();
            workerPoolState.RecordDispatchCycle(0, [], 0, isIdle: true, isSaturated: false, clock.UtcNow);
        }

        private async Task ObserveWorkerCompletionAsync(InFlightWorker worker, CancellationToken cancellationToken)
        {
            try
            {
                var result = await worker.Task.ConfigureAwait(false);
                workerPoolState.RecordWorkerResult(result, clock.UtcNow);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                workerPoolState.RecordWorkerFault(clock.UtcNow);
                logger.LogError(ex, "Local worker execution failed; the worker dispatcher will continue processing later work.");
            }
        }

        private async Task ObserveWorkerCompletionDuringDrainAsync(InFlightWorker worker)
        {
            try
            {
                var result = await worker.Task.ConfigureAwait(false);
                workerPoolState.RecordWorkerResult(result, clock.UtcNow);
            }
            catch (OperationCanceledException)
            {
                workerPoolState.RecordWorkerFault(clock.UtcNow);
                logger.LogDebug("Local worker dispatcher stopped while in-flight worker {WorkerId} was observing cancellation.", worker.WorkerId);
            }
            catch (Exception ex)
            {
                workerPoolState.RecordWorkerFault(clock.UtcNow);
                logger.LogError(ex, "Local worker dispatcher observed a failed in-flight worker {WorkerId} during shutdown.", worker.WorkerId);
            }
        }

        private IEnumerable<int> AvailableWorkerSlots()
        {
            var activeSlots = inFlightWorkers.Select(worker => worker.SlotNumber).ToHashSet();
            for (var slotNumber = 1; slotNumber <= options.MaxConcurrency; slotNumber++)
            {
                if (!activeSlots.Contains(slotNumber))
                {
                    yield return slotNumber;
                }
            }
        }

        private string WorkerIdForSlot(int slotNumber) => $"{localWorkerOptions.WorkerId}-{slotNumber.ToString(CultureInfo.InvariantCulture)}";

        private void LogWorkerDispatch(WorkerQueueSnapshot queueSnapshot, int started, int inFlight, bool includeExpiredLeases, bool isIdle, bool isSaturated, IReadOnlyList<string> activeWorkerIds)
        {
            using var scope = scopes.CreateScope();
            var observability = scope.ServiceProvider.GetRequiredService<SqliteOperationalReadModelRepository>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            var propertiesJson = JsonSerializer.Serialize(new
            {
                dispatchRecordedAtUtc = clock.UtcNow,
                mode = options.Mode,
                claimableBeforeDispatch = queueSnapshot.ClaimableBacklog,
                activeQueueRows = queueSnapshot.ActiveQueueRows,
                queuedQueueRows = queueSnapshot.QueuedQueueRows,
                leasedQueueRows = queueSnapshot.LeasedQueueRows,
                expiredLeaseRows = queueSnapshot.ExpiredLeaseRows,
                started,
                inFlight,
                availableSlots = options.MaxConcurrencyUnbounded ? (object)"Unbounded" : Math.Max(0, options.MaxConcurrency - inFlight),
                includeExpiredLeases,
                workerConcurrency = options.MaxConcurrencyDisplay,
                maxWorkerIterations = options.MaxDispatchStartsPerCycle,
                maxConcurrency = options.MaxConcurrencyDisplay,
                maxDispatchStartsPerCycle = options.MaxDispatchStartsPerCycle,
                idleDelay = options.IdleDelay.ToString(),
                isIdle,
                isSaturated,
                activeWorkerIds
            }, SchedulerPassLogJson.Options);

            observability.RecordLog(
                "Information",
                "Worker dispatch cycle started.",
                "worker-dispatch",
                propertiesJson: propertiesJson);
        }

        private sealed record InFlightWorker(int SlotNumber, string WorkerId, Task<WorkerPassResult> Task);

        private sealed record WorkerQueueSnapshot(
            int ClaimableBacklog,
            int ActiveQueueRows,
            int QueuedQueueRows,
            int LeasedQueueRows,
            int ExpiredLeaseRows);
    }
}
