using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Core.Repair
{
    public enum RepairBatchStatus { Planned, Queued, Running, Completed, Cancelled, Failed }
    public enum RepairSliceStatus { Planned, Queued, Completed, Failed, Skipped, Blocked }
    public enum RepairOutputStrategy { ExecuteNoCleanup, CleanSliceOutputThenExecute, MarkCompletedOnly }

    public sealed record RepairBatch(string BatchId, string ActivityId, DateTimeOffset CreatedAtUtc, RepairBatchStatus Status, IReadOnlyList<RepairSlice> Slices, string? Reason = null);
    public sealed record RepairSlice(string BatchId, SliceKey SliceKey, SliceRange Slice, RepairSliceStatus Status, string? WorkItemId = null, string? FailureReason = null);
}
