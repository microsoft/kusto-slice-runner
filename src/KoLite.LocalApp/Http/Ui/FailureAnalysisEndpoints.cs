using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.FailureAnalysis;
using KoLite.LocalApp.Http;

namespace KoLite.LocalApp.Http.Ui
{
    public sealed record FailureAnalysisResponse(
        string RunId,
        string JobId,
        string Status,
        string? Markdown,
        string? Error,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset UpdatedAtUtc);

    public static class FailureAnalysisEndpoints
    {
        public static void Map(WebApplication app)
        {
            var group = app.MapGroup("/ui-api/v1/jobs/{jobId:guid}/failure-analyses")
                .AddEndpointFilter<LocalRequestEndpointFilter>()
                .ExcludeFromDescription();

            group.MapPost("", Start)
                .AddEndpointFilter<UiAntiforgeryEndpointFilter>();
            group.MapGet("/{runId}", Get);
        }

        private static IResult Start(
            Guid jobId,
            SqliteJobCatalogRepository catalog,
            FailureAnalysisPromptBuilder promptBuilder,
            FailureAnalysisRunRegistry registry,
            FailureAnalysisOrchestrator orchestrator)
        {
            var id = jobId.ToString("N");
            var record = catalog.Get(id);
            if (record is null)
            {
                return HttpProblem.Create(
                    StatusCodes.Status404NotFound,
                    "job-not-found",
                    "Job not found.",
                    $"Job '{id}' does not exist.");
            }

            if (!registry.TryStart(id, out var run))
            {
                return TypedResults.Ok(Map(run));
            }

            try
            {
                var evidence = promptBuilder.Build(record);
                if (evidence.FailureCount == 0)
                {
                    var completed = registry.Complete(
                        run.RunId,
                        "**No recent failures found for this job.** There are no failed or dead-lettered " +
                        "slices and no recent failed attempts to analyze.");
                    return TypedResults.Ok(Map(completed ?? run));
                }

                orchestrator.Launch(run.RunId, evidence.Prompt);
                return TypedResults.Accepted(
                    $"/ui-api/v1/jobs/{jobId:D}/failure-analyses/{Uri.EscapeDataString(run.RunId)}",
                    Map(run));
            }
            catch (Exception)
            {
                var failed = registry.Fail(run.RunId, "Failed to gather failure evidence for this job.");
                return TypedResults.Ok(Map(failed ?? run));
            }
        }

        private static IResult Get(
            Guid jobId,
            string runId,
            SqliteJobCatalogRepository catalog,
            FailureAnalysisRunRegistry registry)
        {
            var id = jobId.ToString("N");
            if (catalog.Get(id) is null)
            {
                return HttpProblem.Create(
                    StatusCodes.Status404NotFound,
                    "job-not-found",
                    "Job not found.",
                    $"Job '{id}' does not exist.");
            }

            var run = registry.GetForJob(id, runId);
            return run is null
                ? HttpProblem.Create(
                    StatusCodes.Status404NotFound,
                    "failure-analysis-not-found",
                    "Failure analysis not found.",
                    $"Analysis run '{runId}' was not found or has expired.")
                : TypedResults.Ok(Map(run));
        }

        private static FailureAnalysisResponse Map(FailureAnalysisRun run)
        {
            return new FailureAnalysisResponse(
                run.RunId,
                run.JobId,
                run.Status.ToString(),
                run.Markdown,
                run.Error,
                run.StartedAtUtc,
                run.UpdatedAtUtc);
        }
    }
}
