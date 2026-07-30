using KoLite.Local.Core.Scheduling;

namespace KoLite.Local.Core.Repair
{
    public enum RepairBatchStatus { Planned, Queued, Running, Completed, Cancelled, Failed }
    public enum RepairSliceStatus { Planned, Queued, Completed, Failed, Skipped, Blocked }

    // ExecuteNoCleanup simply re-runs the slice; output is written with an ingest-by tag plus
    // ingestIfNotExists, so a re-run never duplicates rows already in Kusto.
    //
    // CleanSliceOutputThenExecute is NOT IMPLEMENTED. The strategy is written into the work-queue
    // payload but nothing ever reads it back, so a repair requested with this value silently behaves
    // exactly like ExecuteNoCleanup - it does not clean anything. Overwriting existing output is the
    // rerun flow's job (plan, run the suggested Kusto cleanup by hand, then acknowledge). The local
    // repair API therefore refuses this value rather than appearing to honor it.
    //
    // MarkCompletedOnly records slices as Completed WITHOUT executing them, so it asserts completeness
    // that was never produced. It requires an auditable reason and is likewise not exposed over the API.
    public enum RepairOutputStrategy { ExecuteNoCleanup, CleanSliceOutputThenExecute, MarkCompletedOnly }

    public sealed record RepairBatch(string BatchId, string JobId, DateTimeOffset CreatedAtUtc, RepairBatchStatus Status, IReadOnlyList<RepairSlice> Slices, string? Reason = null);
    public sealed record RepairSlice(string BatchId, SliceKey SliceKey, SliceRange Slice, RepairSliceStatus Status, string? WorkItemId = null, string? FailureReason = null);
}
