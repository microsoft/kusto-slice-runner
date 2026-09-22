// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.Local.Core.Performance
{
    public static class PerformanceOutcomes
    {
        public static bool IsCompleted(string? status) =>
            status is "Succeeded" or "FailedRetryable" or "Failed" or "DeadLettered" or "LeaseLost";
    }

    public sealed record PerformanceAttemptCapture(
        string ClusterUri,
        string Database,
        long CatalogVersion,
        string ClientRequestId,
        bool? DuplicateSuppressed = null);

    public sealed record PerformancePercentiles(long SampleCount, double? P50, double? P90, double? P95)
    {
        public static PerformancePercentiles Empty { get; } = new(0, null, null, null);
    }

    public sealed record PerformanceAggregateRow(
        string JobId,
        int? ChunkId,
        bool IsJobTotal,
        long CompletedAttempts,
        long SucceededAttempts,
        PerformancePercentiles CpuSeconds,
        PerformancePercentiles DurationSeconds,
        PerformancePercentiles MemoryGiB)
    {
        public PerformanceCoverageCounts Coverage { get; init; } = PerformanceCoverageCounts.Empty;
        public double? SuccessPercent => CompletedAttempts == 0 ? null : SucceededAttempts * 100d / CompletedAttempts;
    }

    public sealed record PerformanceCollectionReadout(
        bool HistoryInitialized,
        DateTimeOffset? LastHistorySyncUtc,
        DateTimeOffset? LastCollectionUtc,
        string? LastError,
        long PendingSamples,
        long AvailableSamples);

    public sealed record PerformancePendingAttempt(
        string AttemptId,
        string JobId,
        string ClusterUri,
        string Database,
        string ClientRequestId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        bool LegacyCorrelation,
        int LookupCount);

    public interface IPerformanceReportRepository
    {
        IReadOnlyList<PerformanceAggregateRow> GetAggregates(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            IReadOnlyCollection<string>? jobIds = null,
            DateTimeOffset? coverageCutoffUtc = null);

        PerformanceCollectionReadout GetCollectionStatus();
    }

    public interface IPerformanceCollectionStore
    {
        void BeginHistoryReconciliation(DateTimeOffset nowUtc);
        bool BackfillBatch(DateTimeOffset nowUtc, int batchSize = 500);
        IReadOnlyList<PerformancePendingAttempt> GetPendingAttempts(DateTimeOffset nowUtc, int take = 200, bool oldestFirst = false);
        void ApplyStatistics(IReadOnlyList<PerformancePendingAttempt> attempts, IReadOnlyList<KustoCommandStatistics> statistics, DateTimeOffset nowUtc);
        void RecordCollectionFailure(IReadOnlyList<PerformancePendingAttempt> attempts, DateTimeOffset nowUtc, string message);
        void RecordPassSuccess(DateTimeOffset nowUtc);
    }

    public sealed record KustoCommandStatisticsQuery(
        Uri ClusterUri,
        string Database,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        IReadOnlyList<string> ClientRequestIds,
        int MaxResults = 1000);

    public sealed record KustoCommandStatistics(
        string ClientRequestId,
        Guid ServerActivityId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        string State,
        double? CpuSeconds,
        double? DurationSeconds,
        long? MemoryPeakBytes,
        string? ValidationError = null);

    public interface IKustoCommandStatisticsReader
    {
        Task<IReadOnlyList<KustoCommandStatistics>> ReadAsync(
            KustoCommandStatisticsQuery query,
            CancellationToken cancellationToken = default);
    }

    public sealed class CommandStatisticsResultTooLargeException : Exception
    {
        public CommandStatisticsResultTooLargeException(string message) : base(message)
        {
        }
    }
}
