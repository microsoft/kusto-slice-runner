using System.Text.Json;
using KoLite.Local.Core.Repair;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;

namespace KoLite.Local.Sqlite.Repair
{
    public sealed record RepairPlanRequest(string JobId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string RequestedBy, string Reason, RepairOutputStrategy OutputStrategy = RepairOutputStrategy.ExecuteNoCleanup);
    public sealed record RepairPlanResult(string RepairBatchId, int Queued, int Blocked, int Skipped);

    public sealed class SqliteRepairService
    {
        private readonly IKoLiteSqliteConnectionFactory connectionFactory;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository state;
        private readonly SqliteWorkQueueRepository queue;
        private readonly IClock clock;

        public SqliteRepairService(IKoLiteSqliteConnectionFactory connectionFactory, SqliteJobCatalogRepository catalog, SqliteSliceStateRepository state, SqliteWorkQueueRepository queue, IClock clock)
        {
            this.connectionFactory = connectionFactory;
            this.catalog = catalog;
            this.state = state;
            this.queue = queue;
            this.clock = clock;
        }

        public RepairPlanResult PlanAndEnqueue(RepairPlanRequest request)
        {
            if (request.OutputStrategy == RepairOutputStrategy.MarkCompletedOnly && string.IsNullOrWhiteSpace(request.Reason))
            {
                throw new InvalidOperationException("MarkCompletedOnly repair requires an auditable reason.");
            }

            var job = catalog.Get(request.JobId) ?? throw new InvalidOperationException($"Job '{request.JobId}' does not exist.");
            var allJobs = catalog.List().ToDictionary(j => j.JobId, j => j.Definition, StringComparer.Ordinal);
            var batchId = StableId("repair-batch", request.JobId, SqliteStorage.Utc(request.StartUtc), SqliteStorage.Utc(request.EndUtc), request.OutputStrategy.ToString(), request.Reason);
            var queued = 0;
            var blocked = 0;
            var skipped = 0;

            InsertBatch(batchId, request);

            foreach (var slice in SliceEnumerator.Enumerate(request.JobId, request.StartUtc, request.EndUtc, job.Definition.QueryWindowSize))
            {
                var current = state.Get(request.JobId, slice.StartUtc, slice.EndUtc);
                if (current.Status is DurableSliceStatus.Queued or DurableSliceStatus.Running)
                {
                    skipped++;
                    continue;
                }

                if (current.Status is not (DurableSliceStatus.Missing or DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered or DurableSliceStatus.DependencyBlocked or DurableSliceStatus.Completed))
                {
                    skipped++;
                    UpsertRepairSlice(batchId, slice, RepairSliceStatus.Skipped, null, $"Current state is {current.Status}.");
                    continue;
                }

                var readiness = state.EvaluateDependencyReadiness(job.Definition, slice, allJobs);
                if (!readiness.IsReady)
                {
                    blocked++;
                    UpsertRepairSlice(batchId, slice, RepairSliceStatus.Blocked, null, string.Join(",", readiness.MissingSlices.Select(s => s.Value)));
                    continue;
                }

                if (request.OutputStrategy == RepairOutputStrategy.MarkCompletedOnly)
                {
                    state.Append($"repair-manual-complete|{batchId}|{slice.ToKey().Value}", request.JobId, slice.StartUtc, slice.EndUtc, DurableSliceStatus.Completed, current.Version, request.Reason, request.RequestedBy, payloadJson: JsonSerializer.Serialize(new { repairBatchId = batchId, request.OutputStrategy }));
                    UpsertRepairSlice(batchId, slice, RepairSliceStatus.Completed, null, null);
                    queued++;
                    continue;
                }

                if (current.Status is DurableSliceStatus.Missing or DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered or DurableSliceStatus.DependencyBlocked or DurableSliceStatus.Completed)
                {
                    state.Append($"repair-queue|{batchId}|{slice.ToKey().Value}", request.JobId, slice.StartUtc, slice.EndUtc, DurableSliceStatus.Queued, current.Version, request.Reason, request.RequestedBy, payloadJson: JsonSerializer.Serialize(new { repairBatchId = batchId, request.OutputStrategy }));
                }

                var work = queue.Enqueue(
                    request.JobId,
                    slice.StartUtc,
                    slice.EndUtc,
                    $"repair|{batchId}|{request.JobId}|{SqliteStorage.Utc(slice.StartUtc)}|{SqliteStorage.Utc(slice.EndUtc)}",
                    clock.UtcNow,
                    priority: 100,
                    payloadJson: JsonSerializer.Serialize(new { workKind = "Repair", repairBatchId = batchId, outputStrategy = request.OutputStrategy.ToString(), sliceKey = slice.ToKey().Value }));
                queued++;
                UpsertRepairSlice(batchId, slice, RepairSliceStatus.Queued, work.QueueItemId, null);
            }

            UpdateBatchStatus(batchId, queued > 0 ? RepairBatchStatus.Queued : RepairBatchStatus.Planned);
            return new RepairPlanResult(batchId, queued, blocked, skipped);
        }

        // Operator-driven single-slice recovery for an orphaned lease: a slice left Running/Leased after
        // its lease expired (the prior worker faulted or was killed without recording a terminal result).
        // Only enabled jobs are recoverable; paused/soft-deleted jobs must be resumed first so this never
        // behaves like a retry that pause is meant to suppress. Returns false when nothing was orphaned.
        // The re-run is idempotent (ingest-by), so it never duplicates output already in Kusto.
        public bool RecoverOrphanedSlice(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc, string requestedBy, string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new InvalidOperationException("Recovering an orphaned slice requires an auditable reason.");
            }

            var job = catalog.Get(jobId) ?? throw new InvalidOperationException($"Job '{jobId}' does not exist.");
            if (!job.IsEnabled)
            {
                throw new InvalidOperationException("Cannot recover an orphaned slice while the job is paused or deleted. Resume the job first; it will then recover automatically.");
            }

            var now = clock.UtcNow;
            var slice = new SliceRange(jobId, sliceStartUtc, sliceEndUtc);
            var current = state.Get(jobId, sliceStartUtc, sliceEndUtc);

            var requeued = queue.RequeueExpiredLease(jobId, sliceStartUtc, sliceEndUtc, now, now);
            var stateIsOrphaned = current.Status == DurableSliceStatus.Running
                && (current.LeaseExpiresAtUtc is null || current.LeaseExpiresAtUtc <= now.ToUniversalTime());

            if (!requeued && !stateIsOrphaned)
            {
                return false;
            }

            if (current.Status == DurableSliceStatus.Running)
            {
                state.Append(
                    $"recover-orphan|{Guid.NewGuid():N}",
                    jobId,
                    sliceStartUtc,
                    sliceEndUtc,
                    DurableSliceStatus.Queued,
                    current.Version,
                    reason,
                    requestedBy,
                    payloadJson: JsonSerializer.Serialize(new { workKind = "RecoverOrphan", recoveredBy = requestedBy, reason }));
            }

            if (!requeued)
            {
                queue.Enqueue(
                    jobId,
                    sliceStartUtc,
                    sliceEndUtc,
                    $"recover|{Guid.NewGuid():N}|{slice.ToKey().Value}",
                    now,
                    priority: 100,
                    payloadJson: JsonSerializer.Serialize(new { workKind = "RecoverOrphan", recoveredBy = requestedBy, sliceKey = slice.ToKey().Value }));
            }

            return true;
        }

        public IReadOnlyList<RepairSlice> GetRepairSlices(string repairBatchId)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "SELECT repair_batch_id,job_id,slice_start_utc,slice_end_utc,status,enqueued_queue_item_id FROM repair_slices WHERE repair_batch_id=$id ORDER BY slice_start_utc;");
            cmd.Add("$id", repairBatchId);
            using var r = cmd.ExecuteReader();
            var rows = new List<RepairSlice>();
            while (r.Read())
            {
                var slice = new SliceRange(r.GetString(1), SqliteStorage.ReadUtc(r, "slice_start_utc"), SqliteStorage.ReadUtc(r, "slice_end_utc"));
                rows.Add(new RepairSlice(r.GetString(0), slice.ToKey(), slice, Enum.Parse<RepairSliceStatus>(r.GetString(4)), r.IsDBNull(5) ? null : r.GetString(5)));
            }
            return rows;
        }

        private void InsertBatch(string batchId, RepairPlanRequest request)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                INSERT OR IGNORE INTO repair_batches (repair_batch_id, job_id, requested_by, reason, status, criteria_json, requested_at_utc)
                VALUES ($id,$job,$by,$reason,$status,$criteria,$now);
                """);
            cmd.Add("$id", batchId);
            cmd.Add("$job", request.JobId);
            cmd.Add("$by", request.RequestedBy);
            cmd.Add("$reason", request.Reason);
            cmd.Add("$status", RepairBatchStatus.Planned.ToString());
            cmd.Add("$criteria", JsonSerializer.Serialize(new { request.JobId, startUtc = request.StartUtc, endUtc = request.EndUtc, request.OutputStrategy }));
            cmd.Add("$now", SqliteStorage.Utc(clock.UtcNow));
            cmd.ExecuteNonQuery();
        }

        private void UpsertRepairSlice(string batchId, SliceRange slice, RepairSliceStatus status, string? workItemId, string? failureReason)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, """
                INSERT INTO repair_slices (repair_slice_id,repair_batch_id,job_id,slice_start_utc,slice_end_utc,status,enqueued_queue_item_id,created_at_utc,updated_at_utc)
                VALUES ($id,$batch,$job,$start,$end,$status,$work,$now,$now)
                ON CONFLICT(repair_slice_id) DO UPDATE SET status=excluded.status,enqueued_queue_item_id=excluded.enqueued_queue_item_id,updated_at_utc=excluded.updated_at_utc;
                """);
            cmd.Add("$id", StableId("repair-slice", batchId, slice.ToKey().Value));
            cmd.Add("$batch", batchId);
            cmd.Add("$job", slice.JobId);
            cmd.Add("$start", SqliteStorage.Utc(slice.StartUtc));
            cmd.Add("$end", SqliteStorage.Utc(slice.EndUtc));
            cmd.Add("$status", status.ToString());
            cmd.Add("$work", workItemId);
            cmd.Add("$now", SqliteStorage.Utc(clock.UtcNow));
            cmd.ExecuteNonQuery();
        }

        private void UpdateBatchStatus(string batchId, RepairBatchStatus status)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "UPDATE repair_batches SET status=$status WHERE repair_batch_id=$id;");
            cmd.Add("$status", status.ToString());
            cmd.Add("$id", batchId);
            cmd.ExecuteNonQuery();
        }

        private static string StableId(params string[] parts)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", parts)));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
