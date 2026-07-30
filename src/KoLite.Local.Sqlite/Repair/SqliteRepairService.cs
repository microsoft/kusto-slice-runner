using System.Text.Json;
using KoLite.Local.Core.Repair;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Infrastructure;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;

namespace KoLite.Local.Sqlite.Repair
{
    // Which already-recorded slice states a repair is allowed to act on.
    //
    // AllRepairable is the original behavior and stays the default so existing callers are unchanged.
    // FailedAndDeadLetteredOnly is what the local API uses: it is exactly the state set that
    // GET /api/diagnostics/failures reports, and it deliberately excludes Missing. SliceEnumerator
    // steps from whatever start it is given rather than snapping to the job's slice grid, so an
    // unaligned range fabricates windows that all read back as Missing; excluding Missing means such a
    // window can never be enqueued as real work. Missing slices also need no repair - the scheduler
    // already enqueues missing eligible slices on its own.
    public enum RepairSliceScope { AllRepairable, FailedAndDeadLetteredOnly }

    public sealed record RepairPlanRequest(
        string JobId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string RequestedBy,
        string Reason,
        RepairOutputStrategy OutputStrategy = RepairOutputStrategy.ExecuteNoCleanup,
        RepairSliceScope Scope = RepairSliceScope.AllRepairable);

    public sealed record RepairPlanResult(string RepairBatchId, int Queued, int Blocked, int Skipped);

    public enum RepairSliceOutcome { Repairable, Blocked, Skipped }

    public sealed record RepairSlicePreview(
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        DurableSliceStatus CurrentStatus,
        RepairSliceOutcome Outcome,
        string? Detail);

    // Write-free projection of what PlanAndEnqueue would do for the same request.
    public sealed record RepairPreviewResult(
        string JobId,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        int Repairable,
        int Blocked,
        int Skipped,
        IReadOnlyList<RepairSlicePreview> Slices);

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
            var batchId = StableId("repair-batch", request.JobId, SqliteStorage.Utc(request.StartUtc), SqliteStorage.Utc(request.EndUtc), request.OutputStrategy.ToString(), request.Reason);
            var queued = 0;
            var blocked = 0;
            var skipped = 0;

            InsertBatch(batchId, request);

            foreach (var candidate in Classify(job, request))
            {
                var slice = candidate.Slice;
                if (candidate.Outcome == RepairSliceOutcome.Skipped)
                {
                    skipped++;
                    if (candidate.Persist)
                    {
                        UpsertRepairSlice(batchId, slice, RepairSliceStatus.Skipped, null, candidate.Detail);
                    }

                    continue;
                }

                if (candidate.Outcome == RepairSliceOutcome.Blocked)
                {
                    blocked++;
                    UpsertRepairSlice(batchId, slice, RepairSliceStatus.Blocked, null, candidate.Detail);
                    continue;
                }

                if (request.OutputStrategy == RepairOutputStrategy.MarkCompletedOnly)
                {
                    state.Append($"repair-manual-complete|{batchId}|{slice.ToKey().Value}", request.JobId, slice.StartUtc, slice.EndUtc, DurableSliceStatus.Completed, candidate.Current.Version, request.Reason, request.RequestedBy, payloadJson: JsonSerializer.Serialize(new { repairBatchId = batchId, request.OutputStrategy }));
                    UpsertRepairSlice(batchId, slice, RepairSliceStatus.Completed, null, null);
                    queued++;
                    continue;
                }

                // A worker can move this slice between classification and here (its state was read a moment
                // ago). state.Append then throws on the version mismatch. Skipping that one slice - rather
                // than letting the exception abort the loop - matters because earlier slices are already
                // committed: aborting would leave a partially enqueued batch behind an error response.
                try
                {
                    state.Append($"repair-queue|{batchId}|{slice.ToKey().Value}", request.JobId, slice.StartUtc, slice.EndUtc, DurableSliceStatus.Queued, candidate.Current.Version, request.Reason, request.RequestedBy, payloadJson: JsonSerializer.Serialize(new { repairBatchId = batchId, request.OutputStrategy }));
                }
                catch (InvalidOperationException)
                {
                    skipped++;
                    UpsertRepairSlice(batchId, slice, RepairSliceStatus.Skipped, null, "Slice state changed while the repair was being applied.");
                    continue;
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
            InsertAudit(request, batchId, queued, blocked, skipped);
            return new RepairPlanResult(batchId, queued, blocked, skipped);
        }

        // Write-free projection of exactly what PlanAndEnqueue would do for the same request. It shares
        // Classify with the enqueue path so the preview a caller approves cannot drift from what actually
        // runs, and it touches no table: no batch row, no slice state, no queue work.
        public RepairPreviewResult Preview(RepairPlanRequest request)
        {
            var job = catalog.Get(request.JobId) ?? throw new InvalidOperationException($"Job '{request.JobId}' does not exist.");
            var slices = Classify(job, request)
                .Select(candidate => new RepairSlicePreview(
                    candidate.Slice.StartUtc,
                    candidate.Slice.EndUtc,
                    candidate.Current.Status,
                    candidate.Outcome,
                    candidate.Detail))
                .ToArray();

            return new RepairPreviewResult(
                request.JobId,
                request.StartUtc.ToUniversalTime(),
                request.EndUtc.ToUniversalTime(),
                slices.Count(s => s.Outcome == RepairSliceOutcome.Repairable),
                slices.Count(s => s.Outcome == RepairSliceOutcome.Blocked),
                slices.Count(s => s.Outcome == RepairSliceOutcome.Skipped),
                slices);
        }

        // Single source of truth for repair eligibility, shared by Preview and PlanAndEnqueue.
        private IEnumerable<SliceClassification> Classify(JobCatalogRecord job, RepairPlanRequest request)
        {
            var allJobs = catalog.List().ToDictionary(j => j.JobId, j => j.Definition, StringComparer.Ordinal);
            foreach (var slice in SliceEnumerator.Enumerate(request.JobId, request.StartUtc, request.EndUtc, job.Definition.QueryWindowSize))
            {
                var current = state.Get(request.JobId, slice.StartUtc, slice.EndUtc);

                // Actively claimable or in-flight work is left strictly alone, and - unlike every other
                // outcome - records no repair_slices row, so a batch that touched nothing stays childless.
                if (current.Status is DurableSliceStatus.Queued or DurableSliceStatus.Running)
                {
                    yield return new SliceClassification(slice, current, RepairSliceOutcome.Skipped, Persist: false, $"Current state is {current.Status}.");
                    continue;
                }

                if (!IsInScope(current.Status, request.Scope))
                {
                    // Out-of-scope states are also not persisted: a narrow-scope repair over a wide range
                    // would otherwise write a Skipped row for every untouched slice in the range.
                    var persist = request.Scope == RepairSliceScope.AllRepairable;
                    yield return new SliceClassification(slice, current, RepairSliceOutcome.Skipped, persist, $"Current state is {current.Status}.");
                    continue;
                }

                var readiness = state.EvaluateDependencyReadiness(job.Definition, slice, allJobs);
                if (!readiness.IsReady)
                {
                    yield return new SliceClassification(slice, current, RepairSliceOutcome.Blocked, Persist: true, string.Join(",", readiness.MissingSlices.Select(s => s.Value)));
                    continue;
                }

                yield return new SliceClassification(slice, current, RepairSliceOutcome.Repairable, Persist: true, null);
            }
        }

        private static bool IsInScope(DurableSliceStatus status, RepairSliceScope scope) => scope switch
        {
            RepairSliceScope.FailedAndDeadLetteredOnly => status is DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered,
            _ => status is DurableSliceStatus.Missing or DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered or DurableSliceStatus.DependencyBlocked or DurableSliceStatus.Completed,
        };

        // Rejects a range that does not land on this job's slice boundaries. SliceEnumerator steps from
        // whatever start it is handed rather than snapping to the grid, so an unaligned range silently
        // enumerates windows that never existed. Callers get the nearest aligned bounds to retry with.
        // The range is also capped: classification reads slice state one window at a time, so an
        // unbounded range (a year of one-minute slices is over half a million lookups) would stall the
        // local app rather than fail cleanly.
        public const int MaxRepairSlices = 10_000;

        public static void ValidateAlignedRange(JobDefinition job, DateTimeOffset start, DateTimeOffset end)
        {
            start = start.ToUniversalTime();
            end = end.ToUniversalTime();
            if (end <= start)
            {
                throw new InvalidOperationException("Repair end must be after start.");
            }

            if (start < job.StartFrom.ToUniversalTime())
            {
                throw new InvalidOperationException($"Repair start must not be before job start {SqliteStorage.Utc(job.StartFrom)}.");
            }

            if (!IsAligned(start, job.StartFrom, job.QueryWindowSize) || !IsAligned(end, job.StartFrom, job.QueryWindowSize))
            {
                throw new InvalidOperationException(
                    "Repair start and end must align to this job's slice boundaries. " +
                    NearestAlignedRangeHint(job, start, end));
            }

            var sliceCount = (end - start).Ticks / job.QueryWindowSize.Ticks;
            if (sliceCount > MaxRepairSlices)
            {
                throw new InvalidOperationException(
                    $"Repair range covers {sliceCount} slices, above the {MaxRepairSlices} limit. Repair a narrower range.");
            }
        }

        private static string NearestAlignedRangeHint(JobDefinition job, DateTimeOffset start, DateTimeOffset end)
        {
            try
            {
                return $"Nearest aligned range is {SqliteStorage.Utc(AlignFloor(start, job.StartFrom, job.QueryWindowSize))} to " +
                       $"{SqliteStorage.Utc(AlignCeiling(end, job.StartFrom, job.QueryWindowSize))}.";
            }
            catch (ArgumentOutOfRangeException)
            {
                // An end near DateTimeOffset.MaxValue has no representable aligned ceiling; the range is
                // rejected either way, so report it plainly instead of failing while building the message.
                return "The supplied bounds are too close to the maximum representable date to align.";
            }
        }

        private static bool IsAligned(DateTimeOffset value, DateTimeOffset anchor, TimeSpan queryWindow) =>
            (value.ToUniversalTime().Ticks - anchor.ToUniversalTime().Ticks) % queryWindow.Ticks == 0;

        private static DateTimeOffset AlignFloor(DateTimeOffset value, DateTimeOffset anchor, TimeSpan queryWindow)
        {
            value = value.ToUniversalTime();
            anchor = anchor.ToUniversalTime();
            var quotient = Math.DivRem(value.Ticks - anchor.Ticks, queryWindow.Ticks, out var remainder);
            if (value.Ticks < anchor.Ticks && remainder != 0)
            {
                quotient--;
            }

            return anchor.AddTicks(quotient * queryWindow.Ticks);
        }

        private static DateTimeOffset AlignCeiling(DateTimeOffset value, DateTimeOffset anchor, TimeSpan queryWindow)
        {
            var floor = AlignFloor(value, anchor, queryWindow);
            return floor == value.ToUniversalTime() ? floor : floor + queryWindow;
        }

        private sealed record SliceClassification(
            SliceRange Slice,
            DurableSliceState Current,
            RepairSliceOutcome Outcome,
            bool Persist,
            string? Detail);

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

        // Repair batches previously recorded no audit row at all, unlike reruns. An agent-driven repair
        // must be visible in the system audit trail (GET /api/diagnostics/audit) or the required-reason
        // guard on the API is unverifiable after the fact.
        private void InsertAudit(RepairPlanRequest request, string batchId, int queued, int blocked, int skipped)
        {
            using var c = connectionFactory.OpenConnection();
            using var cmd = SqliteStorage.Command(c, null, "INSERT INTO system_audit (audit_id,actor,action,subject_type,subject_id,payload_json) VALUES ($id,$actor,$action,$type,$subject,$payload);");
            cmd.Add("$id", Guid.NewGuid().ToString("N"));
            cmd.Add("$actor", request.RequestedBy);
            cmd.Add("$action", "RepairEnqueued");
            cmd.Add("$type", "RepairBatch");
            cmd.Add("$subject", batchId);
            cmd.Add("$payload", JsonSerializer.Serialize(
                new
                {
                    jobId = request.JobId,
                    startUtc = request.StartUtc,
                    endUtc = request.EndUtc,
                    scope = request.Scope.ToString(),
                    outputStrategy = request.OutputStrategy.ToString(),
                    reason = request.Reason,
                    queued,
                    blocked,
                    skipped,
                },
                SqliteStorage.JsonOptions));
            cmd.ExecuteNonQuery();
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
