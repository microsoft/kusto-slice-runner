// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Ksr.Local.Core.Orchestration;
using Ksr.Local.Sqlite.Queue;
using Ksr.Local.Sqlite.State;
using Ksr.LocalApp.Application;
using Ksr.LocalApp.Application.Operations;
using Ksr.LocalApp.Application.System;
using Ksr.LocalApp.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ksr.LocalApp.Http.AgentApi
{
    public static class OperationsEndpoints
    {
        private static readonly TimeSpan DefaultLookback = TimeSpan.FromHours(24);

        public static void Map(RouteGroupBuilder api)
        {
            var operations = api.MapGroup("/operations").WithTags("Operations");

            operations.MapGet("/worker-pool", GetWorkerPool)
                .WithName("GetWorkerPool")
                .WithSummary("Gets the current worker-pool snapshot.");

            operations.MapGet("/queue", GetQueue)
                .WithName("ListQueueItems")
                .WithSummary("Lists queue items with keyset pagination.");

            operations.MapGet("/slices", GetSlices)
                .WithName("ListSlices")
                .WithSummary("Lists logical slice states with keyset pagination.");

            operations.MapGet("/running-slices", GetRunningSlices)
                .WithName("ListRunningSlices")
                .WithSummary("Lists active logical slices with lease and timing evidence.");

            operations.MapGet("/chunks", GetChunks)
                .WithName("GetSliceChunks")
                .WithSummary("Gets the bounded child execution set for one logical slice.");

            operations.MapGet("/attempts", GetAttempts)
                .WithName("ListAttempts")
                .WithSummary("Lists execution attempts with keyset pagination.");

            operations.MapGet("/events", GetEvents)
                .WithName("ListSliceEvents")
                .WithSummary("Lists slice state events with keyset pagination.");

            operations.MapGet("/logs", GetLogs)
                .WithName("ListOperationalLogs")
                .WithSummary("Lists durable operational logs with keyset pagination.");

            operations.MapGet("/throughput", GetThroughput)
                .WithName("GetThroughput")
                .WithSummary("Gets bounded logical-window throughput buckets.");

            operations.MapGet("/failures", GetFailures)
                .WithName("ListFailures")
                .WithSummary("Lists terminal failure evidence with keyset pagination.");

            operations.MapGet("/audit-events", GetAuditEvents)
                .WithName("ListAuditEvents")
                .WithSummary("Lists durable audit events with keyset pagination.");

            operations.MapGet("/reruns", GetReruns)
                .WithName("ListRerunBatches")
                .WithSummary("Lists operator-created rerun batches.");

            operations.MapGet("/reruns/{batchId}", GetRerun)
                .WithName("GetRerunBatch")
                .WithSummary("Gets one rerun batch and its affected slices.");

            operations.MapGet("/repairs", GetRepairs)
                .WithName("ListRepairBatches")
                .WithSummary("Lists repair batches with keyset pagination.");

            operations.MapGet("/repairs/{batchId}", GetRepair)
                .WithName("GetRepairBatch")
                .WithSummary("Gets one repair batch's slice and chunk executions.");
        }

        private static Ok<WorkerPoolStatusResponse> GetWorkerPool(SystemStatusApplicationService status)
        {
            return TypedResults.Ok(MapWorkerPool(status.Get().WorkerPool));
        }

        private static Results<Ok<PageEnvelope<QueueItemResponse>>, ProblemHttpResult> GetQueue(
            string? jobId,
            string? queueName,
            string? state,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var parsedState = OptionalEnum<DurableWorkQueueState>(state, "state");
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(
                    ("jobId", normalizedJobId),
                    ("queueName", queueName),
                    ("state", parsedState?.ToString()));
                var anchor = Pagination.Decode(cursor, "operations.queue", fingerprint);
                var page = operations.GetQueue(
                    normalizedJobId,
                    Clean(queueName),
                    parsedState,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                var items = page.Items.Select(AgentContractMapper.Queue).ToArray();
                return TypedResults.Ok(new PageEnvelope<QueueItemResponse>(
                    items,
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.queue",
                            fingerprint,
                            new PageAnchor(page.Items[^1].CreatedAtUtc, page.Items[^1].QueueItemId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<SliceResponse>>, ProblemHttpResult> GetSlices(
            string? jobId,
            string? state,
            string? from,
            string? to,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var normalizedState = OptionalSliceState(state);
                var fromUtc = UtcInput.Optional(from, "from");
                var toUtc = UtcInput.Optional(to, "to");
                ValidateWindow(fromUtc, toUtc);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(
                    ("jobId", normalizedJobId),
                    ("state", normalizedState),
                    ("from", Format(fromUtc)),
                    ("to", Format(toUtc)));
                var anchor = Pagination.Decode(cursor, "operations.slices", fingerprint);
                var page = operations.GetSlices(
                    normalizedJobId,
                    normalizedState,
                    fromUtc,
                    toUtc,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<SliceResponse>(
                    page.Items.Select(AgentContractMapper.Slice).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.slices",
                            fingerprint,
                            new PageAnchor(page.Items[^1].UpdatedAtUtc, SliceId(page.Items[^1])))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<RunningSliceResponse>>, ProblemHttpResult> GetRunningSlices(
            string? jobId,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var pageLimit = Pagination.ParseLimit(limit, defaultLimit: 100, maximum: 500);
                var fingerprint = Pagination.Fingerprint(("jobId", normalizedJobId));
                var anchor = Pagination.Decode(cursor, "operations.running-slices", fingerprint);
                var page = operations.GetRunningSlices(
                    normalizedJobId,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<RunningSliceResponse>(
                    page.Items.Select(AgentContractMapper.RunningSlice).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.running-slices",
                            fingerprint,
                            new PageAnchor(
                                page.Items[^1].UpdatedAtUtc,
                                RunningSliceId(page.Items[^1])))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<ChunkCollectionResponse>, ProblemHttpResult> GetChunks(
            string? jobId,
            string? start,
            string? end,
            string? limit,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = RequiredJobId(jobId);
                var startUtc = UtcInput.Required(start, "start");
                var endUtc = UtcInput.Required(end, "end");
                ValidateWindow(startUtc, endUtc);
                var result = operations.GetChunks(
                    normalizedJobId,
                    startUtc,
                    endUtc,
                    Pagination.ParseLimit(limit, defaultLimit: 100, maximum: 1000));
                return TypedResults.Ok(new ChunkCollectionResponse(
                    normalizedJobId,
                    startUtc,
                    endUtc,
                    result.Chunks.Select(chunk => new ChunkResponse(
                        chunk.ChunkId,
                        chunk.TotalChunks,
                        chunk.Status.ToString(),
                        chunk.Attempt,
                        chunk.LeaseOwner,
                        chunk.LeaseExpiresAtUtc,
                        chunk.LastErrorCode,
                        chunk.LastErrorMessage,
                        chunk.UpdatedAtUtc)).ToArray(),
                    result.Events.Select(item => new ChunkEventResponse(
                        item.EventId,
                        item.ChunkId,
                        item.TotalChunks,
                        item.Status.ToString(),
                        item.Reason,
                        item.Attempt,
                        item.Actor,
                        item.RecordedAtUtc)).ToArray()));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<AttemptResponse>>, ProblemHttpResult> GetAttempts(
            string? jobId,
            string? start,
            string? end,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var startUtc = UtcInput.Optional(start, "start");
                var endUtc = UtcInput.Optional(end, "end");
                ValidatePairedSlice(startUtc, endUtc);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(
                    ("jobId", normalizedJobId),
                    ("start", Format(startUtc)),
                    ("end", Format(endUtc)));
                var anchor = Pagination.Decode(cursor, "operations.attempts", fingerprint);
                var page = operations.GetAttempts(
                    normalizedJobId,
                    startUtc,
                    endUtc,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<AttemptResponse>(
                    page.Items.Select(AgentContractMapper.Attempt).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.attempts",
                            fingerprint,
                            new PageAnchor(AttemptActivity(page.Items[^1]), page.Items[^1].AttemptId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<SliceEventResponse>>, ProblemHttpResult> GetEvents(
            string? jobId,
            string? start,
            string? end,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var startUtc = UtcInput.Optional(start, "start");
                var endUtc = UtcInput.Optional(end, "end");
                ValidatePairedSlice(startUtc, endUtc);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(
                    ("jobId", normalizedJobId),
                    ("start", Format(startUtc)),
                    ("end", Format(endUtc)));
                var anchor = Pagination.Decode(cursor, "operations.events", fingerprint);
                var page = operations.GetEvents(
                    normalizedJobId,
                    startUtc,
                    endUtc,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<SliceEventResponse>(
                    page.Items.Select(AgentContractMapper.Event).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.events",
                            fingerprint,
                            new PageAnchor(page.Items[^1].RecordedAtUtc, page.Items[^1].EventId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<OperationalLogResponse>>, ProblemHttpResult> GetLogs(
            string? jobId,
            string? level,
            string? category,
            string? from,
            string? to,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var fromUtc = UtcInput.Optional(from, "from");
                var toUtc = UtcInput.Optional(to, "to");
                ValidateWindow(fromUtc, toUtc);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(
                    ("jobId", normalizedJobId),
                    ("level", Clean(level)),
                    ("category", Clean(category)),
                    ("from", Format(fromUtc)),
                    ("to", Format(toUtc)));
                var anchor = Pagination.Decode(cursor, "operations.logs", fingerprint);
                var page = operations.GetLogs(
                    normalizedJobId,
                    Clean(level),
                    Clean(category),
                    fromUtc,
                    toUtc,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<OperationalLogResponse>(
                    page.Items.Select(AgentContractMapper.Log).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.logs",
                            fingerprint,
                            new PageAnchor(page.Items[^1].RecordedAtUtc, page.Items[^1].LogId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<ThroughputResponse>, ProblemHttpResult> GetThroughput(
            string? jobId,
            string? from,
            string? to,
            string? bucket,
            string? groupBy,
            string? limit,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var toUtc = UtcInput.Optional(to, "to") ?? DateTimeOffset.UtcNow;
                var fromUtc = UtcInput.Optional(from, "from") ?? toUtc - DefaultLookback;
                ValidateWindow(fromUtc, toUtc);
                var bucketSeconds = ParseBucket(bucket);
                var groupByJob = ParseGroupBy(groupBy);
                var rows = operations.GetThroughput(
                    normalizedJobId,
                    fromUtc,
                    toUtc,
                    bucketSeconds,
                    groupByJob,
                    Pagination.ParseLimit(limit, defaultLimit: 500));
                return TypedResults.Ok(new ThroughputResponse(
                    fromUtc,
                    toUtc,
                    bucketSeconds,
                    groupByJob,
                    rows.Select(item => new ThroughputBucketResponse(
                        item.JobId,
                        item.BucketStartUtc,
                        item.SucceededCount)).ToArray()));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<FailuresPageResponse>, ProblemHttpResult> GetFailures(
            string? jobId,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(("jobId", normalizedJobId));
                var anchor = Pagination.Decode(cursor, "operations.failures", fingerprint);
                var page = operations.GetFailures(
                    normalizedJobId,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                var responses = page.Items.Select(item => new RecentFailureResponse(
                    item.Failure.JobId,
                    item.Failure.SliceStartUtc,
                    item.Failure.SliceEndUtc,
                    item.Failure.Status,
                    item.Failure.Attempt,
                    item.Failure.Reason,
                    item.Failure.UpdatedAtUtc,
                    item.FailedChunkIds.Count,
                    item.FailedChunkIds)).ToArray();
                return TypedResults.Ok(new FailuresPageResponse(
                    responses,
                    page.Summaries.Select(summary => new FailureSummaryResponse(
                        summary.RunId,
                        summary.JobId,
                        summary.SummaryKind,
                        summary.FailureCode,
                        summary.FailureCount,
                        summary.FirstSeenUtc,
                        summary.LastSeenUtc,
                        summary.CreatedAtUtc)).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.failures",
                            fingerprint,
                            new PageAnchor(
                                page.Items[^1].Failure.UpdatedAtUtc,
                                FailureId(page.Items[^1].Failure)))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<AuditEventResponse>>, ProblemHttpResult> GetAuditEvents(
            string? subjectType,
            string? subjectId,
            string? action,
            string? from,
            string? to,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var fromUtc = UtcInput.Optional(from, "from");
                var toUtc = UtcInput.Optional(to, "to");
                ValidateWindow(fromUtc, toUtc);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(
                    ("subjectType", Clean(subjectType)),
                    ("subjectId", Clean(subjectId)),
                    ("action", Clean(action)),
                    ("from", Format(fromUtc)),
                    ("to", Format(toUtc)));
                var anchor = Pagination.Decode(cursor, "operations.audit-events", fingerprint);
                var page = operations.GetAuditEvents(
                    Clean(subjectType),
                    Clean(subjectId),
                    Clean(action),
                    fromUtc,
                    toUtc,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<AuditEventResponse>(
                    page.Items.Select(AgentContractMapper.Audit).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.audit-events",
                            fingerprint,
                            new PageAnchor(page.Items[^1].RecordedAtUtc, page.Items[^1].AuditId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<RerunBatchSummaryResponse>>, ProblemHttpResult> GetReruns(
            string? jobId,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(("jobId", normalizedJobId));
                var anchor = Pagination.Decode(cursor, "operations.reruns", fingerprint);
                var page = operations.GetReruns(
                    normalizedJobId,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<RerunBatchSummaryResponse>(
                    page.Items.Select(AgentContractMapper.Rerun).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.reruns",
                            fingerprint,
                            new PageAnchor(page.Items[^1].RequestedAtUtc, page.Items[^1].RerunBatchId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<RerunBatchDetailResponse>, ProblemHttpResult> GetRerun(
            string batchId,
            OperationsApplicationService operations)
        {
            try
            {
                return TypedResults.Ok(AgentContractMapper.RerunDetail(operations.GetRerun(batchId)));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<RepairBatchSummaryResponse>>, ProblemHttpResult> GetRepairs(
            string? jobId,
            string? limit,
            string? cursor,
            OperationsApplicationService operations)
        {
            try
            {
                var normalizedJobId = OptionalJobId(jobId);
                var pageLimit = Pagination.ParseLimit(limit);
                var fingerprint = Pagination.Fingerprint(("jobId", normalizedJobId));
                var anchor = Pagination.Decode(cursor, "operations.repairs", fingerprint);
                var page = operations.GetRepairs(
                    normalizedJobId,
                    anchor?.TimestampUtc,
                    anchor?.Id,
                    pageLimit);
                return TypedResults.Ok(new PageEnvelope<RepairBatchSummaryResponse>(
                    page.Items.Select(AgentContractMapper.Repair).ToArray(),
                    page.HasMore && page.Items.Count > 0
                        ? Pagination.Encode(
                            "operations.repairs",
                            fingerprint,
                            new PageAnchor(page.Items[^1].RequestedAtUtc, page.Items[^1].RepairBatchId))
                        : null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<RepairBatchDetailResponse>, ProblemHttpResult> GetRepair(
            string batchId,
            OperationsApplicationService operations)
        {
            try
            {
                var detail = operations.GetRepair(batchId);
                return TypedResults.Ok(new RepairBatchDetailResponse(
                    batchId,
                    detail.Slices.Select(slice => new RepairSliceDetailResponse(
                        slice.Slice.JobId,
                        slice.Slice.StartUtc,
                        slice.Slice.EndUtc,
                        slice.Status.ToString(),
                        slice.WorkItemId)).ToArray(),
                    detail.Chunks.Select(chunk => new RepairChunkDetailResponse(
                        chunk.JobId,
                        chunk.Slice.StartUtc,
                        chunk.Slice.EndUtc,
                        chunk.ChunkId,
                        chunk.TotalChunks,
                        chunk.PreviousStatus.ToString(),
                        chunk.PreviousAttempt,
                        chunk.Status.ToString(),
                        chunk.WorkItemId)).ToArray()));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static WorkerPoolStatusResponse MapWorkerPool(WorkerPoolSnapshot item)
        {
            return new WorkerPoolStatusResponse(
                item.Mode,
                item.Enabled,
                item.EnabledSource,
                item.MaxConcurrency,
                item.MaxConcurrencySource,
                item.MaxConcurrencyDisplay,
                item.IdleDelay,
                item.IdleDelaySource,
                item.MaxDispatchStartsPerCycle,
                item.MaxDispatchStartsPerCycleSource,
                item.ActiveWorkerCount,
                item.AvailableSlots,
                item.ClaimableBacklog,
                item.ActiveQueueRows,
                item.QueuedQueueRows,
                item.LeasedQueueRows,
                item.ExpiredLeaseRows,
                item.IsIdle,
                item.IsSaturated,
                item.DispatchCycles,
                item.IdleCycles,
                item.SaturatedCycles,
                item.Starts,
                item.Succeeded,
                item.RetryableFailures,
                item.DeadLettered,
                item.Faulted,
                item.ActiveWorkerIds,
                item.LastUpdatedAtUtc);
        }

        private static TEnum? OptionalEnum<TEnum>(string? raw, string name)
            where TEnum : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (!Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw InvalidFilter(name, $"'{name}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
            }

            return parsed;
        }

        private static string? OptionalSliceState(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (!Enum.TryParse<DurableSliceStatus>(raw, ignoreCase: true, out var parsed)
                || !Enum.IsDefined(parsed))
            {
                throw InvalidFilter("state", $"'state' must be one of: {string.Join(", ", Enum.GetNames<DurableSliceStatus>())}.");
            }

            return parsed.ToString();
        }

        private static string RequiredJobId(string? raw)
        {
            return OptionalJobId(raw)
                ?? throw InvalidFilter("jobId", "'jobId' is required and must be a GUID.");
        }

        private static string? OptionalJobId(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (!Guid.TryParse(raw, out var parsed))
            {
                throw InvalidFilter("jobId", "'jobId' must be a GUID.");
            }

            return parsed.ToString("N");
        }

        private static int ParseBucket(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 1800;
            }

            var value = raw.Trim();
            var unit = value[^1];
            long seconds;
            if (unit is 's' or 'm' or 'h' or 'd')
            {
                if (!long.TryParse(value[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    throw InvalidFilter("bucket", "'bucket' must be a positive duration such as 30m or 1h.");
                }

                seconds = unit switch
                {
                    's' => number,
                    'm' => number * 60,
                    'h' => number * 3600,
                    'd' => number * 86400,
                    _ => 0
                };
            }
            else if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
            {
                throw InvalidFilter("bucket", "'bucket' must be a positive duration such as 30m or 1h.");
            }

            if (seconds < 60 || seconds > 86400)
            {
                throw InvalidFilter("bucket", "'bucket' must be from 60 through 86400 seconds.");
            }

            return (int)seconds;
        }

        private static bool ParseGroupBy(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(raw, "job"))
            {
                throw InvalidFilter("groupBy", "'groupBy' must be 'job' when supplied.");
            }

            return true;
        }

        private static void ValidateWindow(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
        {
            if (fromUtc is not null && toUtc is not null && fromUtc >= toUtc)
            {
                throw InvalidFilter("from", "'from' must be earlier than 'to'.");
            }
        }

        private static void ValidatePairedSlice(DateTimeOffset? startUtc, DateTimeOffset? endUtc)
        {
            if ((startUtc is null) != (endUtc is null))
            {
                throw InvalidFilter("start", "'start' and 'end' must be supplied together.");
            }

            ValidateWindow(startUtc, endUtc);
        }

        private static ApplicationProblemException InvalidFilter(string name, string detail)
        {
            return new ApplicationProblemException(
                StatusCodes.Status400BadRequest,
                "invalid-filter",
                $"The '{name}' filter is invalid.",
                detail);
        }

        private static string? Clean(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string? Format(DateTimeOffset? value)
        {
            return value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        private static DateTimeOffset AttemptActivity(Ksr.Local.Sqlite.Observability.SliceAttemptRow item)
        {
            return item.CompletedAtUtc ?? item.StartedAtUtc ?? item.SliceStartUtc;
        }

        private static string SliceId(Ksr.Local.Sqlite.Observability.SliceStateReadout item)
        {
            return string.Join(
                "|",
                item.JobId,
                StorageUtc(item.SliceStartUtc),
                StorageUtc(item.SliceEndUtc));
        }

        private static string RunningSliceId(Ksr.Local.Sqlite.Observability.RunningSliceReadout item)
        {
            return string.Join(
                "|",
                item.JobId,
                StorageUtc(item.SliceStartUtc),
                StorageUtc(item.SliceEndUtc));
        }

        private static string FailureId(Ksr.Local.Sqlite.Observability.RecentFailure item)
        {
            return string.Join(
                "|",
                item.JobId,
                StorageUtc(item.SliceStartUtc),
                StorageUtc(item.SliceEndUtc));
        }

        private static string StorageUtc(DateTimeOffset value)
        {
            return value.ToUniversalTime().UtcDateTime.ToString(
                "yyyy-MM-ddTHH:mm:ss.fffffffZ",
                CultureInfo.InvariantCulture);
        }
    }
}
