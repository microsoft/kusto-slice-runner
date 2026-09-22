// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Core.Rerun;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.Rerun;
using KoLite.Local.Sqlite.State;

namespace KoLite.LocalApp.Application.Operations
{
    public sealed record OperationsPage<T>(IReadOnlyList<T> Items, bool HasMore);

    public sealed record FailureApplicationItem(
        RecentFailure Failure,
        IReadOnlyList<int> FailedChunkIds);

    public sealed class OperationsApplicationService
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteDiagnosticsReadModelRepository diagnostics;
        private readonly SqliteOperationalReadModelRepository operational;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteChunkStateRepository chunks;
        private readonly SqliteRerunService reruns;
        private readonly SqliteRepairService repairs;
        private readonly IClock clock;

        public OperationsApplicationService(
            SqliteJobCatalogRepository catalog,
            SqliteDiagnosticsReadModelRepository diagnostics,
            SqliteOperationalReadModelRepository operational,
            SqliteWorkQueueRepository queue,
            SqliteChunkStateRepository chunks,
            SqliteRerunService reruns,
            SqliteRepairService repairs,
            IClock clock)
        {
            this.catalog = catalog;
            this.diagnostics = diagnostics;
            this.operational = operational;
            this.queue = queue;
            this.chunks = chunks;
            this.reruns = reruns;
            this.repairs = repairs;
            this.clock = clock;
        }

        public OperationsPage<DurableWorkItem> GetQueue(
            string? jobId,
            string? queueName,
            DurableWorkQueueState? state,
            DateTimeOffset? cursorCreatedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(queue.ListPage(jobId, queueName, state, cursorCreatedAtUtc, cursorId, limit + 1), limit);
        }

        public OperationsPage<SliceStateReadout> GetSlices(
            string? jobId,
            string? state,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            DateTimeOffset? cursorUpdatedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                diagnostics.GetSlicesPage(
                    jobId,
                    state,
                    fromUtc,
                    toUtc,
                    clock.UtcNow,
                    cursorUpdatedAtUtc,
                    cursorId,
                    limit + 1),
                limit);
        }

        public OperationsPage<SliceAttemptRow> GetAttempts(
            string? jobId,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            DateTimeOffset? cursorActivityAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                operational.GetSliceAttemptsPage(
                    jobId,
                    startUtc,
                    endUtc,
                    cursorActivityAtUtc,
                    cursorId,
                    limit + 1),
                limit);
        }

        public OperationsPage<SliceStateEventRow> GetEvents(
            string? jobId,
            DateTimeOffset? startUtc,
            DateTimeOffset? endUtc,
            DateTimeOffset? cursorRecordedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                operational.GetSliceStateEventsPage(
                    jobId,
                    startUtc,
                    endUtc,
                    cursorRecordedAtUtc,
                    cursorId,
                    limit + 1),
                limit);
        }

        public OperationsPage<DiagnosticsLogReadout> GetLogs(
            string? jobId,
            string? level,
            string? category,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            DateTimeOffset? cursorRecordedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                diagnostics.GetLogsPage(
                    jobId,
                    level,
                    category,
                    fromUtc,
                    toUtc,
                    cursorRecordedAtUtc,
                    cursorId,
                    limit + 1),
                limit);
        }

        public OperationsPage<AuditEventReadout> GetAuditEvents(
            string? subjectType,
            string? subjectId,
            string? action,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            DateTimeOffset? cursorRecordedAtUtc,
            string? cursorId,
            int limit)
        {
            return Page(
                diagnostics.GetAuditEventsPage(
                    subjectType,
                    subjectId,
                    action,
                    fromUtc,
                    toUtc,
                    cursorRecordedAtUtc,
                    cursorId,
                    limit + 1),
                limit);
        }

        public OperationsPage<RerunBatchSummary> GetReruns(
            string? jobId,
            DateTimeOffset? cursorRequestedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                diagnostics.ListRerunBatchesPage(jobId, cursorRequestedAtUtc, cursorId, limit + 1),
                limit);
        }

        public RerunBatchReadout GetRerun(string batchId)
        {
            return reruns.GetBatch(batchId)
                ?? throw new ApplicationProblemException(
                    StatusCodes.Status404NotFound,
                    "rerun-batch-not-found",
                    "Rerun batch not found.",
                    $"Rerun batch '{batchId}' does not exist.");
        }

        public OperationsPage<RepairBatchSummary> GetRepairs(
            string? jobId,
            DateTimeOffset? cursorRequestedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                diagnostics.ListRepairBatchesPage(jobId, cursorRequestedAtUtc, cursorId, limit + 1),
                limit);
        }

        public (IReadOnlyList<KoLite.Local.Core.Repair.RepairSlice> Slices, IReadOnlyList<RepairChunkExecution> Chunks) GetRepair(string batchId)
        {
            if (diagnostics.GetRepairBatch(batchId) is null)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status404NotFound,
                    "repair-batch-not-found",
                    "Repair batch not found.",
                    $"Repair batch '{batchId}' does not exist.");
            }

            var slices = repairs.GetRepairSlices(batchId);
            var chunkRows = repairs.GetRepairChunkExecutions(batchId);
            return (slices, chunkRows);
        }

        public (IReadOnlyList<FailureApplicationItem> Items, bool HasMore, IReadOnlyList<FailureSummaryReadout> Summaries) GetFailures(
            string? jobId,
            DateTimeOffset? cursorUpdatedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            var failures = Page(
                operational.GetRecentFailuresPage(limit + 1, jobId, cursorUpdatedAtUtc, cursorId),
                limit);
            var items = failures.Items.Select(failure =>
            {
                var definition = catalog.Get(failure.JobId)?.Definition;
                var failedChunkIds = definition?.Chunks is null
                    ? Array.Empty<int>()
                    : chunks.List(new SliceRange(failure.JobId, failure.SliceStartUtc, failure.SliceEndUtc))
                        .Where(chunk => chunk.Status is DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered)
                        .Select(chunk => chunk.ChunkId)
                        .Order()
                        .ToArray();
                return new FailureApplicationItem(failure, failedChunkIds);
            }).ToArray();
            return (items, failures.HasMore, diagnostics.ListFailureSummaries(jobId, limit));
        }

        public IReadOnlyList<ThroughputBucket> GetThroughput(
            string? jobId,
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            int bucketSeconds,
            bool groupByJob,
            int limit)
        {
            ValidateJob(jobId);
            return diagnostics.GetThroughputSeries(jobId, fromUtc, toUtc, bucketSeconds, groupByJob, limit);
        }

        public OperationsPage<RunningSliceReadout> GetRunningSlices(
            string? jobId,
            DateTimeOffset? cursorUpdatedAtUtc,
            string? cursorId,
            int limit)
        {
            ValidateJob(jobId);
            return Page(
                diagnostics.GetRunningSlices(
                    jobId,
                    clock.UtcNow,
                    cursorUpdatedAtUtc,
                    cursorId,
                    limit + 1),
                limit);
        }

        public (IReadOnlyList<DurableChunkState> Chunks, IReadOnlyList<ChunkStateEventReadout> Events) GetChunks(
            string jobId,
            DateTimeOffset startUtc,
            DateTimeOffset endUtc,
            int eventLimit)
        {
            ValidateJob(jobId);
            var slice = new SliceRange(jobId, startUtc, endUtc);
            return (chunks.List(slice), chunks.ListEvents(slice, eventLimit));
        }

        private void ValidateJob(string? jobId)
        {
            if (jobId is null)
            {
                return;
            }

            if (catalog.Get(jobId) is null)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status404NotFound,
                    "job-not-found",
                    "Job not found.",
                    $"Job '{jobId}' does not exist.");
            }
        }

        private static OperationsPage<T> Page<T>(IReadOnlyList<T> rows, int limit)
        {
            var hasMore = rows.Count > limit;
            return new OperationsPage<T>(hasMore ? rows.Take(limit).ToArray() : rows, hasMore);
        }
    }
}
