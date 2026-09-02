using KoLite.LocalApp.Application;
using KoLite.LocalApp.Application.Repair;
using KoLite.LocalApp.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace KoLite.LocalApp.Http.AgentApi
{
    public static class RepairEndpoints
    {
        public static void Map(RouteGroupBuilder api)
        {
            var jobs = api.MapGroup("/jobs").WithTags("Repair");

            jobs.MapPost("/{jobId:guid}/repair-previews", Preview)
                .WithName("CreateRepairPreview")
                .WithSummary("Previews terminal failed slice or chunk repair work.");

            jobs.MapPost("/{jobId:guid}/repairs", Create)
                .WithName("CreateRepair")
                .WithSummary("Queues exactly the repair work approved by a current preview.");
        }

        private static Results<Ok<RepairPreviewResponse>, ProblemHttpResult> Preview(
            Guid jobId,
            RepairPreviewRequest request,
            RepairApplicationService repairs)
        {
            try
            {
                var result = repairs.Preview(new RepairApplicationRequest(
                    jobId.ToString("N"),
                    UtcInput.Required(request.From, "from"),
                    UtcInput.Required(request.To, "to"),
                    string.IsNullOrWhiteSpace(request.Reason) ? "Repair preview" : request.Reason.Trim()));
                return TypedResults.Ok(MapPreview(result.Job, result.Preview));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Accepted<RepairBatchResponse>, ProblemHttpResult> Create(
            Guid jobId,
            RepairCreateRequest request,
            RepairApplicationService repairs)
        {
            try
            {
                if (request.ExpectedSliceCount is null)
                {
                    throw new ApplicationProblemException(
                        StatusCodes.Status400BadRequest,
                        "expected-slice-count-required",
                        "The repair approval is incomplete.",
                        "'expectedSliceCount' is required and must match the preview.");
                }

                if (string.IsNullOrWhiteSpace(request.PreviewToken))
                {
                    throw new ApplicationProblemException(
                        StatusCodes.Status400BadRequest,
                        "preview-token-required",
                        "The repair approval is incomplete.",
                        "'previewToken' is required and must match the preview.");
                }

                if (string.IsNullOrWhiteSpace(request.Reason))
                {
                    throw new ApplicationProblemException(
                        StatusCodes.Status400BadRequest,
                        "repair-reason-required",
                        "A repair reason is required.",
                        "'reason' must be non-empty and is recorded with the repair batch.");
                }

                var result = repairs.Enqueue(
                    new RepairApplicationRequest(
                        jobId.ToString("N"),
                        UtcInput.Required(request.From, "from"),
                        UtcInput.Required(request.To, "to"),
                        request.Reason.Trim()),
                    request.ExpectedSliceCount.Value,
                    request.ExpectedExecutionCount,
                    request.PreviewToken.Trim());
                var response = new RepairBatchResponse(
                    result.Result.RepairBatchId,
                    new JobReferenceResponse(result.Job.JobId, result.Job.ActivityId),
                    result.Preview.StartUtc,
                    result.Preview.EndUtc,
                    request.Reason.Trim(),
                    result.Result.Queued,
                    result.Result.Blocked,
                    result.Result.Skipped,
                    result.Slices.Select(slice => new RepairSliceResponse(
                        slice.Slice.StartUtc,
                        slice.Slice.EndUtc,
                        slice.Status.ToString(),
                        slice.WorkItemId)).ToArray(),
                    result.Chunks.Select(chunk => new RepairChunkResponse(
                        chunk.Slice.StartUtc,
                        chunk.Slice.EndUtc,
                        chunk.ChunkId,
                        chunk.TotalChunks,
                        chunk.PreviousStatus.ToString(),
                        chunk.PreviousAttempt,
                        chunk.Status.ToString(),
                        chunk.WorkItemId)).ToArray());
                return TypedResults.Accepted(
                    $"/api/v1/operations/repairs/{Uri.EscapeDataString(result.Result.RepairBatchId)}",
                    response);
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static RepairPreviewResponse MapPreview(
            KoLite.Local.Sqlite.Catalog.JobCatalogRecord job,
            KoLite.Local.Sqlite.Repair.RepairPreviewResult preview)
        {
            return new RepairPreviewResponse(
                new JobReferenceResponse(job.JobId, job.ActivityId),
                preview.StartUtc,
                preview.EndUtc,
                preview.Repairable,
                preview.RepairableExecutions,
                preview.PreviewToken,
                preview.Blocked,
                preview.Skipped,
                preview.Slices
                    .Where(slice => slice.Outcome != KoLite.Local.Sqlite.Repair.RepairSliceOutcome.Skipped)
                    .Select(slice => new RepairPreviewSliceResponse(
                        slice.StartUtc,
                        slice.EndUtc,
                        slice.CurrentStatus.ToString(),
                        slice.Outcome.ToString(),
                        slice.Detail,
                        slice.ChunkIds ?? Array.Empty<int>()))
                    .ToArray());
        }
    }
}
