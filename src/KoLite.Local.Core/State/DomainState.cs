// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using KoLite.Local.Core.Schedules;

namespace KoLite.Local.Core.State
{
    public enum JobLifecycleAction { Created, Updated, Paused, Resumed, Retired, Deleted, Restored }
    public sealed record JobLifecycleEvent(string EventId, string OperationId, string ActivityId, long DefinitionVersion, JobLifecycleAction Action, DateTimeOffset EventTimestampUtc, JobDefinition Definition, string SubmittedBy);

    public enum WorkItemStatus { Queued, Leased, Completed, Failed, DeadLettered }
    public sealed record WorkItem(string WorkItemId, string ActivityId, KoLite.Local.Core.Scheduling.SliceKey SliceKey, KoLite.Local.Core.Scheduling.SliceRange Slice, int Attempt, WorkItemStatus Status, DateTimeOffset AvailableAtUtc, DateTimeOffset? LeaseExpiresAtUtc = null);

    public enum SliceStateEventType { Queued, Dispatched, Completed, Failed, DeadLettered, DependencyBlocked }
    public sealed record SliceStateEvent(KoLite.Local.Core.Scheduling.SliceKey SliceKey, long EventIndex, string OperationId, SliceStateEventType EventType, DateTimeOffset EventTimestampUtc, string? WorkerId = null, string? OutputReference = null, string? Reason = null);
    public sealed record CurrentSliceState(KoLite.Local.Core.Scheduling.SliceKey SliceKey, long CurrentVersion, SliceStateEventType Status, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, DateTimeOffset LastUpdatedAtUtc, string? WorkerId = null, string? OutputReference = null, string? FailureReason = null, string? DeadLetterReason = null);

    public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaxDelay, double BackoffFactor)
    {
        public static RetryPolicy None { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, 1);
    }
}
