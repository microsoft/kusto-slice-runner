using System.Globalization;
using System.Text.Json;
using KoLite.Local.Core.Repair;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Repair;
using KoLite.LocalApp.Repair;

namespace KoLite.LocalApp.Api
{
    // Localhost-only repair surface: the one write path an agent has into slice execution.
    //
    // It is deliberately narrow. It re-runs slices that are already recorded as Failed or DeadLettered
    // - exactly the set GET /api/diagnostics/failures reports - and nothing else. It does not expose
    // RepairOutputStrategy: repairs always use ExecuteNoCleanup ("just run it again"), because
    // CleanSliceOutputThenExecute is unimplemented (it would silently do nothing) and MarkCompletedOnly
    // asserts completeness without executing. Overwriting existing Kusto output remains the rerun
    // flow's job, which stays dashboard-only.
    //
    // Re-running a slice cannot duplicate output: KustoExecution writes with an ingest-by tag plus
    // ingestIfNotExists, so Kusto dedupes a repeat ingestion. That is why there is no Kusto cleanup
    // acknowledgement here, unlike rerun.
    //
    // The flow is two-step by design: preview reports exactly which slices would run and how many, then
    // the enqueue call must echo that count. A mismatch means state moved underneath the caller, so the
    // request is rejected rather than silently repairing a different set.
    public static class LocalRepairApi
    {
        private const string Actor = "local-api";

        public static void Map(IEndpointRouteBuilder api)
        {
            api.MapPost("/jobs/{jobId}/repair/preview", async (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteRepairService repair) =>
            {
                var (record, request, failure) = await ResolveRequest(jobId, http, catalog, requireEnqueueFields: false);
                if (failure is not null)
                {
                    return failure;
                }

                try
                {
                    var preview = repair.Preview(BuildPlanRequest(record!, request!));
                    return Results.Json(BuildPreviewResponse(record!, preview));
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(ex.Message);
                }
            });

            api.MapPost("/jobs/{jobId}/repair", async (
                string jobId,
                HttpContext http,
                SqliteJobCatalogRepository catalog,
                SqliteRepairService repair,
                RepairApprovalCoordinator approvals) =>
            {
                var (record, request, failure) = await ResolveRequest(jobId, http, catalog, requireEnqueueFields: true);
                if (failure is not null)
                {
                    return failure;
                }

                var planRequest = BuildPlanRequest(record!, request!);
                ApprovedRepair approved;
                try
                {
                    approved = approvals.Enqueue(
                        planRequest,
                        request!.ExpectedSliceCount,
                        request.ExpectedExecutionCount,
                        request.PreviewToken);
                }
                catch (RepairApprovalConflictException ex)
                {
                    return Results.Json(
                        new
                        {
                            error = ex.Message,
                            expectedSliceCount = ex.ExpectedSliceCount,
                            actualSliceCount = ex.Preview?.Repairable,
                            expectedExecutionCount = ex.ExpectedExecutionCount,
                            actualExecutionCount = ex.Preview?.RepairableExecutions,
                            expectedPreviewToken = ex.ExpectedPreviewToken,
                            actualPreviewToken = ex.Preview?.PreviewToken,
                        },
                        statusCode: StatusCodes.Status409Conflict);
                }
                catch (InvalidOperationException ex)
                {
                    return BadRequest(ex.Message);
                }

                var preview = approved.Preview;
                var result = approved.Result;
                return Results.Json(new
                {
                    repairBatchId = result.RepairBatchId,
                    job = JobRefDto.From(record!),
                    fromUtc = preview.StartUtc,
                    toUtc = preview.EndUtc,
                    reason = planRequest.Reason,
                    queued = result.Queued,
                    blocked = result.Blocked,
                    skipped = result.Skipped,
                    slices = repair.GetRepairSlices(result.RepairBatchId).Select(slice => new
                    {
                        startUtc = slice.Slice.StartUtc,
                        endUtc = slice.Slice.EndUtc,
                        status = slice.Status.ToString(),
                        queueItemId = slice.WorkItemId,
                    }),
                    chunks = repair.GetRepairChunkExecutions(result.RepairBatchId).Select(chunk => new
                    {
                        startUtc = chunk.Slice.StartUtc,
                        endUtc = chunk.Slice.EndUtc,
                        chunk.ChunkId,
                        chunk.TotalChunks,
                        previousState = chunk.PreviousStatus.ToString(),
                        chunk.PreviousAttempt,
                        status = chunk.Status.ToString(),
                        queueItemId = chunk.WorkItemId,
                    }),
                });
            });
        }

        // Repairs only ever run the slice again. The strategy knob is intentionally not caller-controlled.
        private static RepairPlanRequest BuildPlanRequest(JobCatalogRecord record, RepairRequest request) =>
            new(
                record.JobId,
                request.From,
                request.To,
                RequestedBy: Actor,
                Reason: request.Reason ?? "Repair preview",
                OutputStrategy: RepairOutputStrategy.ExecuteNoCleanup,
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);

        private static object BuildPreviewResponse(JobCatalogRecord record, RepairPreviewResult preview) => new
        {
            job = JobRefDto.From(record),
            fromUtc = preview.StartUtc,
            toUtc = preview.EndUtc,
            repairableSliceCount = preview.Repairable,
            repairableExecutionCount = preview.RepairableExecutions,
            previewToken = preview.PreviewToken,
            blockedSliceCount = preview.Blocked,
            skippedSliceCount = preview.Skipped,
            slices = preview.Slices
                .Where(slice => slice.Outcome != RepairSliceOutcome.Skipped)
                .Select(slice => new
                {
                    startUtc = slice.StartUtc,
                    endUtc = slice.EndUtc,
                    currentState = slice.CurrentStatus.ToString(),
                    outcome = slice.Outcome.ToString(),
                    detail = slice.Detail,
                    chunkIds = slice.ChunkIds,
                }),
        };

        private static async Task<(JobCatalogRecord? Record, RepairRequest? Request, IResult? Failure)> ResolveRequest(
            string jobId,
            HttpContext http,
            SqliteJobCatalogRepository catalog,
            bool requireEnqueueFields)
        {
            var record = catalog.Get(jobId) ?? catalog.GetByActivityId(jobId);
            if (record is null)
            {
                return (null, null, Results.Json(new { error = $"Job '{jobId}' does not exist." }, statusCode: StatusCodes.Status404NotFound));
            }

            // Pause and soft-delete both clear job_definitions.is_enabled, and the queue claim joins on
            // is_enabled = 1. Repair work for a disabled job would therefore never be claimed - it would
            // just accumulate - so refuse up front instead of silently queuing dead work.
            if (!record.IsEnabled)
            {
                return (null, null, Results.Json(
                    new { error = $"Job '{record.DisplayName}' is paused or soft-deleted, so repaired slices would never be claimed. Resume the job first." },
                    statusCode: StatusCodes.Status409Conflict));
            }

            var (request, error) = await ReadRequest(http, requireEnqueueFields);
            if (request is null)
            {
                return (null, null, BadRequest(error!));
            }

            try
            {
                SqliteRepairService.ValidateAlignedRange(record.Definition, request.From, request.To);
            }
            catch (InvalidOperationException ex)
            {
                return (null, null, BadRequest(ex.Message));
            }

            return (record, request, null);
        }

        // Parsed by hand (like /jobs/import and the lifecycle routes) so a bad body returns the same
        // { "error": ... } shape as everything else on this API rather than a framework 400.
        private static async Task<(RepairRequest? Request, string? Error)> ReadRequest(HttpContext http, bool requireEnqueueFields)
        {
            string body;
            using (var reader = new StreamReader(http.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, "Request body must be a JSON object with 'from' and 'to' (ISO-8601 UTC) slice bounds.");
            }

            RepairRequestDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<RepairRequestDto>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                return (null, $"Request body is not valid JSON: {ex.Message}");
            }

            // Parsed from the raw string rather than bound as DateTimeOffset: a bare timestamp like
            // "2026-01-01T00:00:00" would otherwise be read as server-local time and silently shifted to
            // a different UTC range, repairing slices the caller never saw in the preview. AssumeUniversal
            // takes an offset-less value at face value as UTC (same convention as the rerun planner page).
            if (!TryParseUtc(dto?.From, "from", out var from, out var fromError))
            {
                return (null, fromError);
            }

            if (!TryParseUtc(dto?.To, "to", out var to, out var toError))
            {
                return (null, toError);
            }

            if (!requireEnqueueFields)
            {
                return (new RepairRequest(from, to, dto!.Reason, 0, null, null), null);
            }

            if (string.IsNullOrWhiteSpace(dto!.Reason))
            {
                return (null, "Request body must include a non-empty 'reason'; it is recorded on the repair batch and in the audit trail.");
            }

            if (dto.ExpectedSliceCount is null)
            {
                return (null, "Request body must include 'expectedSliceCount' - the 'repairableSliceCount' returned by the preview call.");
            }

            if (dto.ExpectedSliceCount < 0)
            {
                return (null, "'expectedSliceCount' must not be negative.");
            }

            return (new RepairRequest(from, to, dto.Reason.Trim(), dto.ExpectedSliceCount.Value, dto.ExpectedExecutionCount, dto.PreviewToken), null);
        }

        private static bool TryParseUtc(string? value, string fieldName, out DateTimeOffset parsed, out string? error)
        {
            parsed = default;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "Request body must include 'from' and 'to' (ISO-8601 UTC) slice bounds.";
                return false;
            }

            if (!DateTimeOffset.TryParse(
                    value.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out parsed))
            {
                error = $"'{fieldName}' must be an ISO-8601 UTC timestamp, e.g. '2026-01-01T00:00:00Z'.";
                return false;
            }

            parsed = parsed.ToUniversalTime();
            return true;
        }

        private static IResult BadRequest(string error) =>
            Results.Json(new { error }, statusCode: StatusCodes.Status400BadRequest);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private sealed record RepairRequestDto(string? From, string? To, string? Reason, int? ExpectedSliceCount, int? ExpectedExecutionCount, string? PreviewToken);

        private sealed record RepairRequest(DateTimeOffset From, DateTimeOffset To, string? Reason, int ExpectedSliceCount, int? ExpectedExecutionCount, string? PreviewToken);
    }
}
