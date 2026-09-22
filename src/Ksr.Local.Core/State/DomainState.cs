// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Ksr.Local.Core.Schedules;

namespace Ksr.Local.Core.State
{
    public enum JobLifecycleAction { Created, Updated, Paused, Resumed, Retired, Deleted, Restored }
    public sealed record JobLifecycleEvent(string EventId, string OperationId, string ActivityId, long DefinitionVersion, JobLifecycleAction Action, DateTimeOffset EventTimestampUtc, JobDefinition Definition, string SubmittedBy);

    public enum WorkItemStatus { Queued, Leased, Completed, Failed, DeadLettered }
    public sealed record WorkItem(string WorkItemId, string ActivityId, Ksr.Local.Core.Scheduling.SliceKey SliceKey, Ksr.Local.Core.Scheduling.SliceRange Slice, int Attempt, WorkItemStatus Status, DateTimeOffset AvailableAtUtc, DateTimeOffset? LeaseExpiresAtUtc = null);

    public enum SliceStateEventType { Queued, Dispatched, Completed, Failed, DeadLettered, DependencyBlocked }
    public sealed record SliceStateEvent(Ksr.Local.Core.Scheduling.SliceKey SliceKey, long EventIndex, string OperationId, SliceStateEventType EventType, DateTimeOffset EventTimestampUtc, string? WorkerId = null, string? OutputReference = null, string? Reason = null);
    public sealed record CurrentSliceState(Ksr.Local.Core.Scheduling.SliceKey SliceKey, long CurrentVersion, SliceStateEventType Status, DateTimeOffset SliceStartUtc, DateTimeOffset SliceEndUtc, DateTimeOffset LastUpdatedAtUtc, string? WorkerId = null, string? OutputReference = null, string? FailureReason = null, string? DeadLetterReason = null);

    public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaxDelay, double BackoffFactor)
    {
        public static RetryPolicy None { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, 1);
    }
}
