using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace KoLite.LocalApp.Http.AgentApi
{
    public sealed record JobTargetResponse(string ClusterUri, string Database);

    public sealed record JobSummaryResponse(
        string JobId,
        string ActivityId,
        string LifecycleState,
        bool HasStarted,
        IReadOnlyList<string> Tags,
        JobTargetResponse Target,
        long CatalogVersion,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    public sealed record JobDetailResponse(JobSummaryResponse Job, JsonElement Schedule);

    public sealed record JobWriteRequest([Required] JsonElement? Schedule);

    public sealed record JobImportRequest([Required, MinLength(1)] JsonElement[]? Schedules);

    public sealed record JobImportItemResponse(string JobId, string Action, long CatalogVersion);

    public sealed record JobImportResponse(
        int Created,
        int Updated,
        int Total,
        IReadOnlyList<JobImportItemResponse> Items);

    public sealed record JobActionRequest(string? Reason);

    public sealed record SoftDeleteJobRequest(string? Reason, bool Force = false);

    public sealed record JobReferenceResponse(string JobId, string ActivityId);

    public sealed record JobSliceStateCountsResponse(
        int Missing,
        int Queued,
        int Running,
        int Completed,
        int Failed,
        int DeadLettered,
        int DependencyBlocked,
        DateTimeOffset? LastUpdatedAtUtc);

    public sealed record JobQueueCountsResponse(int Queued, int Leased, int Total);

    public sealed record JobStatusResponse(
        JobReferenceResponse Job,
        string LifecycleState,
        bool HasStarted,
        int MaxParallelism,
        long CatalogVersion,
        JobTargetResponse Target,
        JobSliceStateCountsResponse SliceStates,
        JobQueueCountsResponse Queue);

    public sealed record CatalogRevisionResponse(
        string EventId,
        long CatalogVersion,
        string EventType,
        string? Actor,
        DateTimeOffset RecordedAtUtc,
        JsonElement Schedule);

    public sealed record DependencyReferenceResponse(
        string? JobId,
        string? ActivityId,
        bool Exists);

    public sealed record MissingUpstreamSliceResponse(
        string Reference,
        string? ActivityId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc);

    public sealed record BlockedSliceResponse(
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        bool IsReady,
        IReadOnlyList<MissingUpstreamSliceResponse> Missing);

    public sealed record JobDependenciesResponse(
        JobReferenceResponse Job,
        IReadOnlyList<DependencyReferenceResponse> Dependencies,
        int BlockedSliceCount,
        IReadOnlyList<BlockedSliceResponse> BlockedSamples);

    public sealed record RepairPreviewRequest(
        [Required] string? From,
        [Required] string? To,
        string? Reason);

    public sealed record RepairCreateRequest(
        [Required] string? From,
        [Required] string? To,
        [Required, MinLength(1)] string? Reason,
        [Range(0, int.MaxValue)] int? ExpectedSliceCount,
        [Range(0, int.MaxValue)] int? ExpectedExecutionCount,
        string? PreviewToken);

    public sealed record RepairPreviewSliceResponse(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string CurrentState,
        string Outcome,
        string? Detail,
        IReadOnlyList<int> ChunkIds);

    public sealed record RepairPreviewResponse(
        JobReferenceResponse Job,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        int RepairableSliceCount,
        int RepairableExecutionCount,
        string? PreviewToken,
        int BlockedSliceCount,
        int SkippedSliceCount,
        IReadOnlyList<RepairPreviewSliceResponse> Slices);

    public sealed record RepairSliceResponse(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string Status,
        string? QueueItemId);

    public sealed record RepairChunkResponse(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        int ChunkId,
        int TotalChunks,
        string PreviousState,
        int PreviousAttempt,
        string Status,
        string? QueueItemId);

    public sealed record RepairBatchResponse(
        string RepairBatchId,
        JobReferenceResponse Job,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        string Reason,
        int Queued,
        int Blocked,
        int Skipped,
        IReadOnlyList<RepairSliceResponse> Slices,
        IReadOnlyList<RepairChunkResponse> Chunks);

    public sealed record KustoLineageRequest([Required, MinLength(1)] string[]? JobIds);

    public sealed record KustoLineageCountsResponse(
        int Total,
        int Missing,
        int Queued,
        int Running,
        int Completed,
        int Failed,
        int DeadLettered,
        int DependencyBlocked);

    public sealed record KustoLineageNodeResponse(
        string Id,
        string Label,
        string Status,
        string StatusKey,
        string StatusText,
        string Kind,
        bool Resolved,
        bool Focal,
        string? Href,
        KustoLineageCountsResponse? Counts);

    public sealed record KustoLineageEdgeResponse(string From, string To, bool Implicit);

    public sealed record KustoLineageLegendResponse(string Status, string StatusKey, string Label);

    public sealed record KustoLineageResponse(
        IReadOnlyList<KustoLineageNodeResponse> Nodes,
        IReadOnlyList<KustoLineageEdgeResponse> Edges,
        IReadOnlyList<KustoLineageLegendResponse> Legend);
}
