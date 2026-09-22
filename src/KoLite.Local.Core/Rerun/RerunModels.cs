// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Core.Rerun
{
    public enum RerunBatchStatus { Planned, Blocked, Completed, Failed }
    public enum RerunSliceRole { Root, Downstream }
    public enum RerunSliceStatus { Planned, Blocked, Reset }

    public sealed record RerunPlanRequest(
        string JobId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string RequestedBy,
        string Reason);

    public sealed record RerunExecuteRequest(
        string RerunBatchId,
        string RequestedBy,
        bool KustoCleanupAcknowledged);

    public sealed record RerunAffectedSlice(
        string JobId,
        SliceRange Slice,
        RerunSliceRole Role,
        string OutputTable,
        string ClusterUri,
        string Database,
        string? PreviousState,
        int? PreviousAttempt,
        int QueueRows,
        int AttemptRows,
        int EventRows,
        int LogRows,
        int ScheduledRows,
        string? BlockerReason,
        RerunSliceStatus Status = RerunSliceStatus.Planned,
        string SnapshotJson = "{}");

    public sealed record RerunPlanResult(
        string RerunBatchId,
        RerunBatchStatus Status,
        IReadOnlyList<RerunAffectedSlice> Slices,
        string KustoCleanupCommands,
        bool CanExecute)
    {
        public IReadOnlyList<RerunAffectedSlice> BlockedSlices { get; } = Slices.Where(s => s.BlockerReason is not null).ToArray();
    }

    public sealed record RerunBatchReadout(
        string RerunBatchId,
        string RootJobId,
        DateTimeOffset RootStartUtc,
        DateTimeOffset RootEndUtc,
        string? RequestedBy,
        string Reason,
        RerunBatchStatus Status,
        bool KustoCleanupAcknowledged,
        string KustoCleanupCommands,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        IReadOnlyList<RerunAffectedSlice> Slices);

    public sealed record RerunExecuteResult(
        string RerunBatchId,
        RerunBatchStatus Status,
        int ResetSlices);
}
