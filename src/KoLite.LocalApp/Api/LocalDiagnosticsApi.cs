using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Orchestration;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.Rerun;
using KoLite.Local.Sqlite.State;
using KoLite.Local.Core.Time;
using KoLite.LocalApp.Ui;

namespace KoLite.LocalApp.Api
{
    // Localhost-only, strictly read-only JSON diagnostics API. It surfaces the operational read models
    // that otherwise only render as dashboard HTML, plus a few cross-job/time-bucketed queries, so a
    // same-machine agent can reproduce an investigation (e.g. a stalled job pinned by hung leases)
    // entirely over HTTP. Every route is loopback-guarded and performs no writes, no Kusto, and no
    // scheduler/rerun/repair mutation - it only reads existing state.
    public static class LocalDiagnosticsApi
    {
        private static readonly TimeSpan DefaultLogLookback = TimeSpan.FromHours(24);
        private static readonly TimeSpan DefaultThroughputLookback = TimeSpan.FromHours(24);
        private const int DefaultThroughputBucketSeconds = 1800;
        private const int DependencySampleSize = 20;

        public static void Map(IEndpointRouteBuilder api)
        {
            // ---- Per-job diagnostics: /api/jobs/{jobId}/... ----

            api.MapGet("/jobs/{jobId}/status", (
                string jobId,
                SqliteJobCatalogRepository catalog,
                SqliteOperationalReadModelRepository readModels,
                SqliteWorkQueueRepository queue) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var summary = readModels.GetJobStatusSummaries().FirstOrDefault(s => s.JobId == record.JobId);
                var queueItems = queue.List(record.JobId);
                var queued = queueItems.Count(q => q.State == DurableWorkQueueState.Queued);
                var leased = queueItems.Count(q => q.State == DurableWorkQueueState.Leased);
                var definition = record.Definition;
                return Results.Json(new JobDiagnosticsStatusDto(
                    JobRefDto.From(record),
                    record.IsEnabled,
                    definition.IsPaused,
                    catalog.HasStarted(record.JobId),
                    definition.MaxParallelism,
                    record.CatalogVersion,
                    new JobTargetDto(definition.Target.ClusterUri, definition.Target.Database),
                    new JobSliceStateCountsDto(
                        summary?.MissingCount ?? 0,
                        summary?.QueuedCount ?? 0,
                        summary?.RunningCount ?? 0,
                        summary?.CompletedCount ?? 0,
                        summary?.FailedCount ?? 0,
                        summary?.DeadLetteredCount ?? 0,
                        summary?.DependencyBlockedCount ?? 0,
                        summary?.LastUpdatedAtUtc),
                    new JobQueueCountsDto(queued, leased, queued + leased)));
            });

            api.MapGet("/jobs/{jobId}/slices", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteDiagnosticsReadModelRepository diagnostics,
                IClock clock) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var slices = diagnostics.GetSlices(
                    record.JobId,
                    DiagnosticsQuery.Text(http.Request, "state"),
                    DiagnosticsQuery.Instant(http.Request, "from"),
                    DiagnosticsQuery.Instant(http.Request, "to"),
                    clock.UtcNow,
                    DiagnosticsQuery.Take(http.Request, 500));
                return Results.Json(new { jobId = record.JobId, slices });
            });

            api.MapGet("/jobs/{jobId}/attempts", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                OperationalDetailsReadModel operationalDetails) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var attempts = operationalDetails.GetAttempts(
                    record.JobId,
                    DiagnosticsQuery.Instant(http.Request, "start"),
                    DiagnosticsQuery.Instant(http.Request, "end"),
                    DiagnosticsQuery.Take(http.Request, 50));
                return Results.Json(new { jobId = record.JobId, attempts });
            });

            api.MapGet("/jobs/{jobId}/chunks", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteChunkStateRepository chunks) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var start = DiagnosticsQuery.Instant(http.Request, "start");
                var end = DiagnosticsQuery.Instant(http.Request, "end");
                if (start is null || end is null || end <= start)
                {
                    return Results.Json(
                        new { error = "Both 'start' and 'end' UTC query parameters are required, and end must be after start." },
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var slice = new SliceRange(record.JobId, start.Value, end.Value);
                return Results.Json(new
                {
                    jobId = record.JobId,
                    sliceStartUtc = start,
                    sliceEndUtc = end,
                    chunks = chunks.List(slice).Select(chunk => new
                    {
                        chunk.ChunkId,
                        chunk.TotalChunks,
                        status = chunk.Status.ToString(),
                        chunk.Attempt,
                        chunk.LeaseOwner,
                        chunk.LeaseExpiresAtUtc,
                        chunk.LastErrorCode,
                        chunk.LastErrorMessage,
                        chunk.UpdatedAtUtc,
                    }),
                    events = chunks.ListEvents(slice, DiagnosticsQuery.Take(http.Request, 100)).Select(evt => new
                    {
                        evt.EventId,
                        evt.ChunkId,
                        evt.TotalChunks,
                        status = evt.Status.ToString(),
                        evt.Reason,
                        evt.Attempt,
                        evt.Actor,
                        evt.RecordedAtUtc,
                    }),
                });
            });

            api.MapGet("/jobs/{jobId}/events", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                OperationalDetailsReadModel operationalDetails) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var events = operationalDetails.GetEvents(
                    record.JobId,
                    DiagnosticsQuery.Instant(http.Request, "start"),
                    DiagnosticsQuery.Instant(http.Request, "end"),
                    DiagnosticsQuery.Take(http.Request, 50));
                return Results.Json(new { jobId = record.JobId, events });
            });

            api.MapGet("/jobs/{jobId}/logs", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteDiagnosticsReadModelRepository diagnostics) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var logs = diagnostics.GetLogs(
                    record.JobId,
                    DiagnosticsQuery.Text(http.Request, "level"),
                    DiagnosticsQuery.Text(http.Request, "category"),
                    DiagnosticsQuery.Instant(http.Request, "from"),
                    DiagnosticsQuery.Instant(http.Request, "to"),
                    DiagnosticsQuery.Take(http.Request));
                return Results.Json(new { jobId = record.JobId, logs });
            });

            api.MapGet("/jobs/{jobId}/queue", (
                string jobId,
                SqliteJobCatalogRepository catalog,
                SqliteWorkQueueRepository queue) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                return Results.Json(new { jobId = record.JobId, queue = queue.List(record.JobId) });
            });

            api.MapGet("/jobs/{jobId}/history", (
                string jobId,
                SqliteJobCatalogRepository catalog) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var history = CatalogHistoryDiffBuilder.Build(catalog.History(record.JobId));
                return Results.Json(new { jobId = record.JobId, history });
            });

            api.MapGet("/jobs/{jobId}/throughput", (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteDiagnosticsReadModelRepository diagnostics,
                SqliteOperationalReadModelRepository readModels,
                IClock clock) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var (fromUtc, toUtc) = DiagnosticsQuery.Window(http.Request, clock.UtcNow, DefaultThroughputLookback);
                var bucketSeconds = DiagnosticsQuery.BucketSeconds(http.Request, DefaultThroughputBucketSeconds);
                var buckets = diagnostics.GetThroughputSeries(record.JobId, fromUtc, toUtc, bucketSeconds, groupByJob: false, DiagnosticsQuery.Take(http.Request, 500));
                var sample = readModels.GetRecentSucceededThroughput(record.JobId, fromUtc);
                return Results.Json(new
                {
                    jobId = record.JobId,
                    fromUtc,
                    toUtc,
                    bucketSeconds,
                    sample,
                    buckets,
                });
            });

            api.MapGet("/jobs/{jobId}/dependencies", (
                string jobId,
                SqliteJobCatalogRepository catalog,
                SqliteDiagnosticsReadModelRepository diagnostics,
                SqliteOperationalReadModelRepository readModels,
                SqliteSliceStateRepository state,
                IClock clock) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                return Results.Json(BuildDependencies(record, catalog, diagnostics, readModels, state, clock.UtcNow));
            });

            // ---- Cross-job / global diagnostics: /api/diagnostics/... ----

            api.MapGet("/diagnostics/worker-pool", (
                SqliteOperationalReadModelRepository readModels,
                SqliteWorkQueueRepository queue,
                LocalWorkerPoolRuntimeState workerPoolState,
                LocalBackgroundWorkerPoolOptions workerPoolOptions,
                LocalWorkerOptions localWorkerOptions,
                IClock clock) =>
            {
                var nowUtc = clock.UtcNow;
                var queueStatus = readModels.GetQueueStatus(localWorkerOptions.QueueName, nowUtc);
                var claimableBacklog = queue.CountClaimable(localWorkerOptions.QueueName, nowUtc, localWorkerOptions.EnforceJobParallelism, localWorkerOptions.EffectiveOrphanReclaimGrace);
                return Results.Json(workerPoolState.GetSnapshot(workerPoolOptions, queueStatus, claimableBacklog));
            });

            api.MapGet("/diagnostics/running-slices", (
                HttpContext http,
                SqliteDiagnosticsReadModelRepository diagnostics,
                IClock clock) =>
            {
                var runningSlices = diagnostics.GetRunningSlices(
                    DiagnosticsQuery.Text(http.Request, "jobId"),
                    clock.UtcNow,
                    DiagnosticsQuery.Take(http.Request));
                return Results.Json(new { runningSlices });
            });

            api.MapGet("/diagnostics/throughput", (
                HttpContext http,
                SqliteDiagnosticsReadModelRepository diagnostics,
                IClock clock) =>
            {
                var (fromUtc, toUtc) = DiagnosticsQuery.Window(http.Request, clock.UtcNow, DefaultThroughputLookback);
                var bucketSeconds = DiagnosticsQuery.BucketSeconds(http.Request, DefaultThroughputBucketSeconds);
                var groupByJob = DiagnosticsQuery.GroupByJob(http.Request);
                var buckets = diagnostics.GetThroughputSeries(
                    DiagnosticsQuery.Text(http.Request, "jobId"),
                    fromUtc,
                    toUtc,
                    bucketSeconds,
                    groupByJob,
                    DiagnosticsQuery.Take(http.Request, 1000));
                return Results.Json(new { fromUtc, toUtc, bucketSeconds, groupByJob, buckets });
            });

            api.MapGet("/diagnostics/queue", (
                SqliteOperationalReadModelRepository readModels,
                LocalWorkerOptions localWorkerOptions,
                IClock clock) =>
            {
                return Results.Json(readModels.GetQueueStatus(localWorkerOptions.QueueName, clock.UtcNow));
            });

            api.MapGet("/diagnostics/logs", (
                HttpContext http,
                SqliteDiagnosticsReadModelRepository diagnostics,
                IClock clock) =>
            {
                var (fromUtc, toUtc) = DiagnosticsQuery.Window(http.Request, clock.UtcNow, DefaultLogLookback);
                var logs = diagnostics.GetLogs(
                    DiagnosticsQuery.Text(http.Request, "jobId"),
                    DiagnosticsQuery.Text(http.Request, "level"),
                    DiagnosticsQuery.Text(http.Request, "category"),
                    fromUtc,
                    toUtc,
                    DiagnosticsQuery.Take(http.Request));
                return Results.Json(new { fromUtc, toUtc, logs });
            });

            api.MapGet("/diagnostics/failures", (
                HttpContext http,
                SqliteOperationalReadModelRepository readModels,
                SqliteDiagnosticsReadModelRepository diagnostics,
                SqliteJobCatalogRepository catalog,
                SqliteChunkStateRepository chunks) =>
            {
                var take = DiagnosticsQuery.Take(http.Request);
                var jobId = DiagnosticsQuery.Text(http.Request, "jobId");
                var recentFailures = readModels.GetRecentFailures(take, jobId)
                    .Select(failure =>
                    {
                        var definition = catalog.Get(failure.JobId)?.Definition;
                        var failedChunkIds = definition?.Chunks is null
                            ? Array.Empty<int>()
                            : chunks.List(new SliceRange(failure.JobId, failure.SliceStartUtc, failure.SliceEndUtc))
                                .Where(chunk => chunk.Status is DurableSliceStatus.Failed or DurableSliceStatus.DeadLettered)
                                .Select(chunk => chunk.ChunkId)
                                .Order()
                                .ToArray();
                        return new
                        {
                            failure.JobId,
                            failure.SliceStartUtc,
                            failure.SliceEndUtc,
                            failure.Status,
                            failure.Attempt,
                            failure.Reason,
                            failure.UpdatedAtUtc,
                            failedChunkCount = failedChunkIds.Length,
                            failedChunkIds,
                        };
                    });
                return Results.Json(new
                {
                    recentFailures,
                    summaries = diagnostics.ListFailureSummaries(jobId, take),
                });
            });

            api.MapGet("/diagnostics/audit", (
                HttpContext http,
                SqliteDiagnosticsReadModelRepository diagnostics,
                IClock clock) =>
            {
                var (fromUtc, toUtc) = DiagnosticsQuery.Window(http.Request, clock.UtcNow, DefaultLogLookback);
                var audit = diagnostics.GetAuditEvents(
                    DiagnosticsQuery.Text(http.Request, "subjectType"),
                    DiagnosticsQuery.Text(http.Request, "subjectId"),
                    DiagnosticsQuery.Text(http.Request, "action"),
                    fromUtc,
                    toUtc,
                    DiagnosticsQuery.Take(http.Request));
                return Results.Json(new { fromUtc, toUtc, audit });
            });

            api.MapGet("/diagnostics/reruns", (
                HttpContext http,
                SqliteDiagnosticsReadModelRepository diagnostics,
                SqliteRerunService rerun) =>
            {
                var batchId = DiagnosticsQuery.Text(http.Request, "batchId");
                if (batchId is not null)
                {
                    var batch = rerun.GetBatch(batchId);
                    return batch is null
                        ? Results.Json(new { error = $"Rerun batch '{batchId}' does not exist." }, statusCode: StatusCodes.Status404NotFound)
                        : Results.Json(new { batch });
                }

                var reruns = diagnostics.ListRerunBatches(DiagnosticsQuery.Text(http.Request, "jobId"), DiagnosticsQuery.Take(http.Request));
                return Results.Json(new { reruns });
            });

            api.MapGet("/diagnostics/repairs", (
                HttpContext http,
                SqliteDiagnosticsReadModelRepository diagnostics,
                SqliteRepairService repair) =>
            {
                var batchId = DiagnosticsQuery.Text(http.Request, "batchId");
                if (batchId is not null)
                {
                    return Results.Json(new
                    {
                        repairBatchId = batchId,
                        repairSlices = repair.GetRepairSlices(batchId),
                        repairChunks = repair.GetRepairChunkExecutions(batchId).Select(chunk => new
                        {
                            chunk.RepairBatchId,
                            chunk.JobId,
                            chunk.Slice,
                            chunk.ChunkId,
                            chunk.TotalChunks,
                            previousStatus = chunk.PreviousStatus.ToString(),
                            chunk.PreviousAttempt,
                            status = chunk.Status.ToString(),
                            chunk.WorkItemId,
                        }),
                    });
                }

                var repairs = diagnostics.ListRepairBatches(DiagnosticsQuery.Text(http.Request, "jobId"), DiagnosticsQuery.Take(http.Request));
                return Results.Json(new { repairs });
            });
        }

        private static IResult NotFound(string jobId) =>
            Results.Json(new { error = $"Job '{jobId}' does not exist." }, statusCode: StatusCodes.Status404NotFound);

        // Accept either the permanent GUID or the mutable activityId, mirroring the dashboard's
        // activityId -> GUID bookmark redirect, so an agent holding either identifier can query.
        private static JobCatalogRecord? ResolveJob(SqliteJobCatalogRepository catalog, string jobId) =>
            catalog.Get(jobId) ?? catalog.GetByActivityId(jobId);

        private static JobDependenciesDto BuildDependencies(
            JobCatalogRecord record,
            SqliteJobCatalogRepository catalog,
            SqliteDiagnosticsReadModelRepository diagnostics,
            SqliteOperationalReadModelRepository readModels,
            SqliteSliceStateRepository state,
            DateTimeOffset nowUtc)
        {
            var dependencies = record.Definition.DependsOn.Select(dependency =>
            {
                var upstream = dependency.Id is not null
                    ? catalog.Get(dependency.Id)
                    : dependency.ActivityId is not null ? catalog.GetByActivityId(dependency.ActivityId) : null;
                return new DependencyRefDto(
                    dependency.Id,
                    upstream?.Definition.ActivityId ?? dependency.ActivityId,
                    upstream?.DisplayName,
                    upstream is not null);
            }).ToList();

            var blockedCount = readModels.GetJobStatusSummaries()
                .FirstOrDefault(s => s.JobId == record.JobId)?.DependencyBlockedCount ?? 0;

            var jobsById = catalog.List()
                .Where(r => r.Definition.Id is not null)
                .ToDictionary(r => r.Definition.Id!, r => r.Definition, StringComparer.Ordinal);
            var completed = state.ListCompletedSliceKeys();

            var samples = new List<BlockedSliceDto>();
            foreach (var slice in diagnostics.GetSlices(record.JobId, "DependencyBlocked", null, null, nowUtc, DependencySampleSize))
            {
                DependencyReadiness readiness;
                try
                {
                    var range = new SliceRange(record.JobId, slice.SliceStartUtc, slice.SliceEndUtc);
                    readiness = DependencyReadinessEvaluator.Evaluate(record.Definition, range, jobsById, completed);
                }
                catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
                {
                    continue;
                }

                var missing = readiness.MissingSlices.Select(key =>
                {
                    if (SliceKey.TryParse(key.Value, out _, out var parsed))
                    {
                        var activityId = catalog.Get(parsed.JobId)?.Definition.ActivityId;
                        return new MissingUpstreamSliceDto(parsed.JobId, activityId, parsed.StartUtc, parsed.EndUtc);
                    }

                    return new MissingUpstreamSliceDto(key.Value, null, slice.SliceStartUtc, slice.SliceEndUtc);
                }).ToList();

                samples.Add(new BlockedSliceDto(slice.SliceStartUtc, slice.SliceEndUtc, readiness.IsReady, missing));
            }

            return new JobDependenciesDto(JobRefDto.From(record), dependencies, blockedCount, samples);
        }
    }
}
