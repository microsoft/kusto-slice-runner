// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.LocalApp.Http.AgentApi
{
    public sealed record QueueItemResponse(
        string QueueItemId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string QueueName,
        int Priority,
        string State,
        DateTimeOffset AvailableAtUtc,
        string? LockedBy,
        DateTimeOffset? LockedUntilUtc,
        int Attempts,
        int MaxAttempts,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        int? ChunkId,
        int? TotalChunks);

    public sealed record SliceResponse(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string State,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        bool LeaseExpired,
        string? LastErrorCode,
        string? LastErrorMessage,
        DateTimeOffset UpdatedAtUtc);

    public sealed record ChunkResponse(
        int ChunkId,
        int TotalChunks,
        string Status,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        string? LastErrorCode,
        string? LastErrorMessage,
        DateTimeOffset UpdatedAtUtc);

    public sealed record ChunkEventResponse(
        string EventId,
        int ChunkId,
        int TotalChunks,
        string Status,
        string? Reason,
        int Attempt,
        string? Actor,
        DateTimeOffset RecordedAtUtc);

    public sealed record ChunkCollectionResponse(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        IReadOnlyList<ChunkResponse> Chunks,
        IReadOnlyList<ChunkEventResponse> Events);

    public sealed record AttemptResponse(
        string AttemptId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        string Status,
        string? WorkerId,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        string? ErrorCode,
        string? ErrorMessage,
        int? ChunkId,
        int? TotalChunks);

    public sealed record SliceEventResponse(
        string EventId,
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string EventType,
        string? State,
        int? Attempt,
        string? Reason,
        string? Actor,
        DateTimeOffset RecordedAtUtc);

    public sealed record OperationalLogResponse(
        string LogId,
        string? JobId,
        DateTimeOffset? SliceStartUtc,
        DateTimeOffset? SliceEndUtc,
        string Level,
        string Message,
        string? Category,
        string? Exception,
        DateTimeOffset RecordedAtUtc,
        int? ChunkId,
        int? TotalChunks);

    public sealed record ThroughputBucketResponse(
        string? JobId,
        DateTimeOffset BucketStartUtc,
        int SucceededCount);

    public sealed record ThroughputResponse(
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        int BucketSeconds,
        bool GroupByJob,
        IReadOnlyList<ThroughputBucketResponse> Buckets);

    public sealed record RecentFailureResponse(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string Status,
        int Attempt,
        string? Reason,
        DateTimeOffset UpdatedAtUtc,
        int FailedChunkCount,
        IReadOnlyList<int> FailedChunkIds);

    public sealed record FailureSummaryResponse(
        string RunId,
        string? JobId,
        string SummaryKind,
        string? FailureCode,
        int FailureCount,
        DateTimeOffset? FirstSeenUtc,
        DateTimeOffset? LastSeenUtc,
        DateTimeOffset CreatedAtUtc);

    public sealed record FailuresResponse(
        IReadOnlyList<RecentFailureResponse> RecentFailures,
        IReadOnlyList<FailureSummaryResponse> Summaries);

    public sealed record FailuresPageResponse(
        IReadOnlyList<RecentFailureResponse> Items,
        IReadOnlyList<FailureSummaryResponse> Summaries,
        string? NextCursor);

    public sealed record AuditEventResponse(
        string AuditId,
        string? Actor,
        string Action,
        string SubjectType,
        string? SubjectId,
        string PayloadJson,
        DateTimeOffset RecordedAtUtc);

    public sealed record RerunBatchSummaryResponse(
        string RerunBatchId,
        string RootJobId,
        DateTimeOffset RootStartUtc,
        DateTimeOffset RootEndUtc,
        string? RequestedBy,
        string Reason,
        string Status,
        bool KustoCleanupAcknowledged,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? CompletedAtUtc);

    public sealed record RerunAffectedSliceResponse(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string Role,
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
        string Status);

    public sealed record RerunBatchDetailResponse(
        string RerunBatchId,
        string RootJobId,
        DateTimeOffset RootStartUtc,
        DateTimeOffset RootEndUtc,
        string? RequestedBy,
        string Reason,
        string Status,
        bool KustoCleanupAcknowledged,
        string KustoCleanupCommands,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        IReadOnlyList<RerunAffectedSliceResponse> Slices);

    public sealed record RepairBatchSummaryResponse(
        string RepairBatchId,
        string? JobId,
        string? RequestedBy,
        string Reason,
        string Status,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset? CompletedAtUtc);

    public sealed record RepairBatchDetailResponse(
        string RepairBatchId,
        IReadOnlyList<RepairSliceDetailResponse> Slices,
        IReadOnlyList<RepairChunkDetailResponse> Chunks);

    public sealed record RepairSliceDetailResponse(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string Status,
        string? WorkItemId);

    public sealed record RepairChunkDetailResponse(
        string JobId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int ChunkId,
        int TotalChunks,
        string PreviousStatus,
        int PreviousAttempt,
        string Status,
        string? WorkItemId);

    public sealed record RunningSliceResponse(
        string JobId,
        string ActivityId,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        int Attempt,
        string? LeaseOwner,
        DateTimeOffset? LeaseExpiresAtUtc,
        bool LeaseExpired,
        string? LastErrorCode,
        string? LastErrorMessage,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? StartedAtUtc,
        int? TotalChunks);
}
