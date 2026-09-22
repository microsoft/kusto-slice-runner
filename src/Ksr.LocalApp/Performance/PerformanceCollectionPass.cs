// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.FailureSummaries;

namespace Ksr.LocalApp.Performance
{
    public sealed record PerformanceCollectionSchedule
    {
        public TimeSpan PassInterval { get; init; } = TimeSpan.FromMinutes(1);
        public TimeSpan BackfillInterval { get; init; } = TimeSpan.FromMilliseconds(100);
        public int LocalBatchesPerPass { get; init; } = 20;
        public int RemoteRequestsPerPass { get; init; } = 8;

        internal void Validate()
        {
            if (PassInterval <= TimeSpan.Zero || BackfillInterval <= TimeSpan.Zero
                || LocalBatchesPerPass < 1 || RemoteRequestsPerPass < 1)
            {
                throw new ArgumentException("Performance collection intervals and batch limits must be positive.");
            }
        }
    }

    public interface IPerformanceCollectionPass
    {
        Task<bool> RunAsync(bool beginReconciliation, bool oldestFirst, CancellationToken cancellationToken);
    }

    public sealed class PerformanceCollectionPass : IPerformanceCollectionPass
    {
        private readonly IPerformanceCollectionStore repository;
        private readonly IKustoCommandStatisticsReader reader;
        private readonly IClock clock;
        private readonly LocalShutdownDrainCoordinator shutdownDrain;
        private readonly PerformanceCollectionSchedule schedule;
        private readonly ILogger<PerformanceCollectionPass> logger;

        public PerformanceCollectionPass(
            IPerformanceCollectionStore repository,
            IKustoCommandStatisticsReader reader,
            IClock clock,
            LocalShutdownDrainCoordinator shutdownDrain,
            PerformanceCollectionSchedule schedule,
            ILogger<PerformanceCollectionPass> logger)
        {
            ArgumentNullException.ThrowIfNull(schedule);
            schedule.Validate();
            this.repository = repository;
            this.reader = reader;
            this.clock = clock;
            this.shutdownDrain = shutdownDrain;
            this.schedule = schedule;
            this.logger = logger;
        }

        public async Task<bool> RunAsync(bool beginReconciliation, bool oldestFirst, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shutdownDrain.IsDrainRequested)
            {
                return true;
            }

            if (beginReconciliation)
            {
                repository.BeginHistoryReconciliation(clock.UtcNow);
            }

            var historyComplete = false;
            for (var batch = 0; batch < schedule.LocalBatchesPerPass && !shutdownDrain.IsDrainRequested; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (repository.BackfillBatch(clock.UtcNow))
                {
                    historyComplete = true;
                    break;
                }

                await Task.Yield();
            }

            if (!historyComplete || shutdownDrain.IsDrainRequested)
            {
                return historyComplete;
            }

            var pending = repository.GetPendingAttempts(clock.UtcNow, take: 200, oldestFirst);
            var work = new Queue<IReadOnlyList<PerformancePendingAttempt>>(
                pending.GroupBy(item => (item.ClusterUri, item.Database))
                    .Select(group => (IReadOnlyList<PerformancePendingAttempt>)group.ToArray()));
            var requests = 0;
            var successfulRead = false;
            var failedRead = false;
            while (work.TryDequeue(out var attempts) && !shutdownDrain.IsDrainRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (requests >= schedule.RemoteRequestsPerPass)
                {
                    repository.RecordCollectionFailure(attempts, clock.UtcNow, "Command statistics exceeded this pass's request budget; collection will retry.");
                    failedRead = true;
                    continue;
                }

                requests++;
                try
                {
                    var now = clock.UtcNow;
                    var oldestStart = attempts.Min(item => item.StartedAtUtc);
                    var from = oldestStart > DateTimeOffset.MinValue.AddMinutes(1)
                        ? oldestStart - TimeSpan.FromMinutes(1)
                        : DateTimeOffset.MinValue;
                    if (from < now - TimeSpan.FromDays(30))
                    {
                        from = now - TimeSpan.FromDays(30);
                    }

                    var query = new KustoCommandStatisticsQuery(
                        new Uri(attempts[0].ClusterUri, UriKind.Absolute),
                        attempts[0].Database,
                        from,
                        now,
                        attempts.Select(item => item.ClientRequestId).Distinct(StringComparer.Ordinal).ToArray());
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    var statistics = await reader.ReadAsync(query, deadline.Token).ConfigureAwait(false);
                    repository.ApplyStatistics(attempts, statistics, clock.UtcNow);
                    successfulRead = true;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (CommandStatisticsResultTooLargeException) when (attempts.Count > 1)
                {
                    var split = attempts.Count / 2;
                    work.Enqueue(attempts.Take(split).ToArray());
                    work.Enqueue(attempts.Skip(split).ToArray());
                }
                catch (Exception ex)
                {
                    var message = SanitizeError(ex);
                    logger.LogWarning("Performance statistics collection failed for {Cluster}/{Database}: {Error}",
                        attempts[0].ClusterUri, attempts[0].Database, message);
                    repository.RecordCollectionFailure(attempts, clock.UtcNow, message);
                    failedRead = true;
                }
            }

            if (successfulRead && !failedRead)
            {
                repository.RecordPassSuccess(clock.UtcNow);
            }

            return true;
        }

        internal static string SanitizeError(Exception exception)
        {
            var message = $"{exception.GetType().Name}: {SqliteFailureSummaryService.Sanitize(exception.Message)}";
            return message.Length <= 1000 ? message : message[..1000] + "...";
        }
    }
}
