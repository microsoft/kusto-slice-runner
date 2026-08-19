namespace KoLite.LocalApp.Http.AgentApi
{
    public sealed record HealthResponse(string Status);

    public sealed record SchedulerStatusResponse(
        bool Enabled,
        string TickInterval,
        int MaxDispatchStartsPerCycle,
        string MaxConcurrency,
        bool LogEveryPass);

    public sealed record DatabaseStatusResponse(string Status, string Path, int JobCount);

    public sealed record KustoStatusResponse(string Execution, string AuthMode);

    public sealed record UpdateStatusResponse(
        string Status,
        string Reason,
        bool Enabled,
        string Repository,
        string Channel,
        string Interval,
        string? BuiltSha,
        string? RemoteSha,
        string? LatestVersion,
        string? ReleaseUrl,
        int? CommitsBehind,
        int? CommitsAhead,
        DateTimeOffset? LastCheckedUtc,
        string? Error);

    public sealed record RetentionStatusResponse(
        bool Enabled,
        double WindowDays,
        string Interval,
        DateTimeOffset? LastRunUtc,
        int LastRunDeleted,
        int LogsDeleted,
        int AttemptsDeleted,
        int ScheduledSlicesDeleted,
        int IngestionThrottlesDeleted,
        int QueueRowsDeleted,
        string? Error);

    public sealed record WorkerPoolStatusResponse(
        string Mode,
        bool Enabled,
        string EnabledSource,
        int? MaxConcurrency,
        string MaxConcurrencySource,
        string MaxConcurrencyDisplay,
        string IdleDelay,
        string IdleDelaySource,
        int MaxDispatchStartsPerCycle,
        string MaxDispatchStartsPerCycleSource,
        int ActiveWorkerCount,
        int? AvailableSlots,
        int ClaimableBacklog,
        int ActiveQueueRows,
        int QueuedQueueRows,
        int LeasedQueueRows,
        int ExpiredLeaseRows,
        bool IsIdle,
        bool IsSaturated,
        long DispatchCycles,
        long IdleCycles,
        long SaturatedCycles,
        long Starts,
        long Succeeded,
        long RetryableFailures,
        long DeadLettered,
        long Faulted,
        IReadOnlyList<string> ActiveWorkerIds,
        DateTimeOffset? LastUpdatedAtUtc);

    public sealed record ShutdownStatusResponse(
        string Mode,
        bool IsDrainRequested,
        int ActiveWorkerCount,
        DateTimeOffset? RequestedAtUtc,
        string? Reason,
        DateTimeOffset? LastActiveWorkerTransitionUtc);

    public sealed record SystemStatusResponse(
        string Status,
        IReadOnlyList<string> SupportedApiVersions,
        DatabaseStatusResponse Database,
        KustoStatusResponse Kusto,
        SchedulerStatusResponse Scheduler,
        WorkerPoolStatusResponse WorkerPool,
        UpdateStatusResponse Update,
        RetentionStatusResponse Retention,
        ShutdownStatusResponse Shutdown);

    public sealed record ShutdownDrainRequest(string? Reason);
}
