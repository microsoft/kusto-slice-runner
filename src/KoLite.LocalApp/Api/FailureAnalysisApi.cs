using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.FailureAnalysis;

namespace KoLite.LocalApp.Api
{
    // The "Analyze failures with Copilot" surface for the job Operations tab. Two loopback-only routes
    // under the existing /api group:
    //   POST /api/jobs/{jobId}/analyze-failures        -> start an analysis, returns the run (202)
    //   GET  /api/jobs/{jobId}/analyze-failures/{runId} -> poll the run's status/result
    // The POST is antiforgery-validated (browser-triggered mutation of in-memory run state and an
    // outbound model call); the GET is a read. Results are ephemeral - held only in the in-memory
    // FailureAnalysisRunRegistry, never persisted to SQLite.
    public static class FailureAnalysisApi
    {
        public static void Map(IEndpointRouteBuilder api)
        {
            var group = api.MapGroup("/jobs/{jobId}/analyze-failures");

            group.MapPost("", (
                    string jobId,
                    SqliteJobCatalogRepository catalog,
                    FailureAnalysisPromptBuilder promptBuilder,
                    FailureAnalysisRunRegistry registry,
                    FailureAnalysisOrchestrator orchestrator) =>
                {
                    var record = ResolveJob(catalog, jobId);
                    if (record is null)
                    {
                        return NotFound(jobId);
                    }

                    // One analysis in flight per job: an existing Running run is returned as-is.
                    if (!registry.TryStart(record.JobId, out var run))
                    {
                        return RunResult(run);
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
                            return RunResult(completed ?? run);
                        }

                        orchestrator.Launch(run.RunId, evidence.Prompt);
                        return RunResult(run);
                    }
                    catch (Exception)
                    {
                        var failed = registry.Fail(run.RunId, "Failed to gather failure evidence for this job.");
                        return RunResult(failed ?? run);
                    }
                })
                .AddEndpointFilter<AntiforgeryEndpointFilter>();

            group.MapGet("/{runId}", (
                string jobId,
                string runId,
                SqliteJobCatalogRepository catalog,
                FailureAnalysisRunRegistry registry) =>
            {
                var record = ResolveJob(catalog, jobId);
                if (record is null)
                {
                    return NotFound(jobId);
                }

                var run = registry.GetForJob(record.JobId, runId);
                if (run is null)
                {
                    return Results.Json(
                        new { error = $"Analysis run '{runId}' was not found (it may have expired)." },
                        statusCode: StatusCodes.Status404NotFound);
                }

                return RunResult(run);
            });
        }

        private static IResult RunResult(FailureAnalysisRun run) =>
            Results.Json(new
            {
                runId = run.RunId,
                jobId = run.JobId,
                status = run.Status.ToString(),
                markdown = run.Markdown,
                error = run.Error,
                startedAtUtc = run.StartedAtUtc,
                updatedAtUtc = run.UpdatedAtUtc
            });

        private static IResult NotFound(string jobId) =>
            Results.Json(new { error = $"Job '{jobId}' does not exist." }, statusCode: StatusCodes.Status404NotFound);

        // Accept either the permanent GUID or the mutable activityId, matching the diagnostics API.
        private static JobCatalogRecord? ResolveJob(SqliteJobCatalogRepository catalog, string jobId) =>
            catalog.Get(jobId) ?? catalog.GetByActivityId(jobId);
    }
}
