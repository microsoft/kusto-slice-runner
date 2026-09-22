// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using Ksr.LocalApp.Application;
using Ksr.LocalApp.Application.Jobs;
using Ksr.LocalApp.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ksr.LocalApp.Http.AgentApi
{
    public static class JobsEndpoints
    {
        private const string Actor = "local-api";

        public static void Map(RouteGroupBuilder api)
        {
            var jobs = api.MapGroup("/jobs").WithTags("Jobs");

            jobs.MapGet("", List)
                .WithName("ListJobs")
                .WithSummary("Lists jobs, optionally filtered by exact activityId.");

            jobs.MapPost("", Create)
                .WithName("CreateJob")
                .WithSummary("Creates one validated job.");

            jobs.MapGet("/{jobId:guid}", Get)
                .WithName("GetJob")
                .WithSummary("Gets one job by permanent GUID.");

            jobs.MapPut("/{jobId:guid}", Replace)
                .WithName("ReplaceJob")
                .WithSummary("Replaces one job definition using If-Match.");

            jobs.MapPost("/{jobId:guid}/actions/pause", Pause)
                .WithName("PauseJob")
                .WithSummary("Pauses one job using If-Match.");

            jobs.MapPost("/{jobId:guid}/actions/resume", Resume)
                .WithName("ResumeJob")
                .WithSummary("Resumes one job using If-Match.");

            jobs.MapPost("/{jobId:guid}/actions/soft-delete", SoftDelete)
                .WithName("SoftDeleteJob")
                .WithSummary("Soft-deletes one job using If-Match.");

            jobs.MapPost("/{jobId:guid}/actions/restore", Restore)
                .WithName("RestoreJob")
                .WithSummary("Restores one soft-deleted job using If-Match.");

            jobs.MapPost("/import", Import)
                .WithName("ImportJobs")
                .WithSummary("Imports one or more schedules additively.");

            jobs.MapGet("/export", Export)
                .WithName("ExportJobs")
                .WithSummary("Exports active and paused jobs as an import-compatible JSON array.")
                .Produces(StatusCodes.Status200OK, contentType: "application/json");

            jobs.MapGet("/{jobId:guid}/status", GetStatus)
                .WithName("GetJobStatus")
                .WithSummary("Gets one job's catalog, queue, and slice-state status.");

            jobs.MapGet("/{jobId:guid}/catalog-revisions", GetCatalogRevisions)
                .WithName("ListJobCatalogRevisions")
                .WithSummary("Lists catalog definition revisions for one job.");

            jobs.MapGet("/{jobId:guid}/dependencies", GetDependencies)
                .WithName("GetJobDependencies")
                .WithSummary("Gets declared dependencies and blocked-slice evidence.");
        }

        private static Ok<PageEnvelope<JobSummaryResponse>> List(
            string? activityId,
            JobApplicationService jobs)
        {
            var items = jobs.List(string.IsNullOrWhiteSpace(activityId) ? null : activityId)
                .Select(AgentContractMapper.Job)
                .ToArray();
            return TypedResults.Ok(new PageEnvelope<JobSummaryResponse>(items, null));
        }

        private static Results<Created<JobDetailResponse>, ProblemHttpResult> Create(
            JobWriteRequest request,
            HttpContext http,
            JobApplicationService jobs)
        {
            try
            {
                var schedule = RequiredSchedule(request);
                var created = jobs.Create(schedule, Actor);
                CatalogEtag.Write(http.Response, created.Record.CatalogVersion);
                return TypedResults.Created(
                    $"/api/v1/jobs/{Guid.ParseExact(created.Record.JobId, "N"):D}",
                    AgentContractMapper.JobDetail(created));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> Get(
            Guid jobId,
            HttpContext http,
            JobApplicationService jobs)
        {
            try
            {
                var job = jobs.Get(Id(jobId));
                CatalogEtag.Write(http.Response, job.Record.CatalogVersion);
                return TypedResults.Ok(AgentContractMapper.JobDetail(job));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> Replace(
            Guid jobId,
            JobWriteRequest request,
            HttpContext http,
            JobApplicationService jobs)
        {
            try
            {
                var id = Id(jobId);
                var current = jobs.Get(id);
                var expectedVersion = CatalogEtag.RequireMatch(http.Request, current.Record.CatalogVersion);
                var updated = jobs.Replace(id, RequiredSchedule(request), expectedVersion, Actor);
                CatalogEtag.Write(http.Response, updated.Record.CatalogVersion);
                return TypedResults.Ok(AgentContractMapper.JobDetail(updated));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> Pause(
            Guid jobId,
            JobActionRequest? request,
            HttpContext http,
            JobApplicationService jobs)
        {
            return ChangeLifecycle(
                jobId,
                http,
                jobs,
                (id, version) => jobs.Pause(
                    id,
                    version,
                    Actor,
                    CleanReason(request?.Reason, "Paused via local API")));
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> Resume(
            Guid jobId,
            JobActionRequest? request,
            HttpContext http,
            JobApplicationService jobs)
        {
            return ChangeLifecycle(
                jobId,
                http,
                jobs,
                (id, version) => jobs.Resume(
                    id,
                    version,
                    Actor,
                    CleanReason(request?.Reason, "Resumed via local API")));
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> SoftDelete(
            Guid jobId,
            SoftDeleteJobRequest? request,
            HttpContext http,
            JobApplicationService jobs)
        {
            return ChangeLifecycle(
                jobId,
                http,
                jobs,
                (id, version) => jobs.SoftDelete(
                    id,
                    version,
                    Actor,
                    CleanReason(request?.Reason, "Soft-deleted via local API"),
                    request?.Force ?? false));
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> Restore(
            Guid jobId,
            JobActionRequest? request,
            HttpContext http,
            JobApplicationService jobs)
        {
            return ChangeLifecycle(
                jobId,
                http,
                jobs,
                (id, version) => jobs.Restore(
                    id,
                    version,
                    Actor,
                    CleanReason(request?.Reason, "Restored via local API")));
        }

        private static Results<Ok<JobImportResponse>, ProblemHttpResult> Import(
            JobImportRequest request,
            JobApplicationService jobs)
        {
            try
            {
                if (request.Schedules is null || request.Schedules.Length == 0)
                {
                    throw InvalidBody("The request must include at least one schedule.");
                }

                var result = jobs.Import(JsonSerializer.Serialize(request.Schedules), Actor);
                return TypedResults.Ok(new JobImportResponse(
                    result.Created,
                    result.Updated,
                    result.Total,
                    result.Items.Select(item => new JobImportItemResponse(
                        item.JobId,
                        item.Action,
                        item.CatalogVersion)).ToArray()));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<ContentHttpResult, ProblemHttpResult> Export(JobApplicationService jobs)
        {
            try
            {
                return TypedResults.Content(jobs.ExportAll(), "application/json");
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<JobStatusResponse>, ProblemHttpResult> GetStatus(
            Guid jobId,
            JobProjectionApplicationService projections)
        {
            try
            {
                var model = projections.GetStatus(Id(jobId));
                var definition = model.Job.Record.Definition;
                var summary = model.SliceStates;
                return TypedResults.Ok(new JobStatusResponse(
                    new JobReferenceResponse(model.Job.Record.JobId, model.Job.Record.ActivityId),
                    AgentContractMapper.Lifecycle(model.Job.LifecycleState),
                    model.Job.HasStarted,
                    definition.MaxParallelism,
                    model.Job.Record.CatalogVersion,
                    new JobTargetResponse(definition.Target.ClusterUri, definition.Target.Database),
                    new JobSliceStateCountsResponse(
                        summary?.MissingCount ?? 0,
                        summary?.QueuedCount ?? 0,
                        summary?.RunningCount ?? 0,
                        summary?.CompletedCount ?? 0,
                        summary?.FailedCount ?? 0,
                        summary?.DeadLetteredCount ?? 0,
                        summary?.DependencyBlockedCount ?? 0,
                        summary?.LastUpdatedAtUtc),
                    new JobQueueCountsResponse(model.Queued, model.Leased, model.Queued + model.Leased)));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<PageEnvelope<CatalogRevisionResponse>>, ProblemHttpResult> GetCatalogRevisions(
            Guid jobId,
            JobProjectionApplicationService projections)
        {
            try
            {
                var revisions = projections.GetCatalogRevisions(Id(jobId))
                    .Select(item =>
                    {
                        using var schedule = JsonDocument.Parse(item.ScheduleJson);
                        return new CatalogRevisionResponse(
                            item.EventId,
                            item.CatalogVersion,
                            item.EventType,
                            item.Actor,
                            item.RecordedAtUtc,
                            schedule.RootElement.Clone());
                    })
                    .ToArray();
                return TypedResults.Ok(new PageEnvelope<CatalogRevisionResponse>(revisions, null));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<JobDependenciesResponse>, ProblemHttpResult> GetDependencies(
            Guid jobId,
            JobProjectionApplicationService projections)
        {
            try
            {
                var model = projections.GetDependencies(Id(jobId));
                return TypedResults.Ok(new JobDependenciesResponse(
                    new JobReferenceResponse(model.Job.Record.JobId, model.Job.Record.ActivityId),
                    model.Dependencies.Select(item => new DependencyReferenceResponse(
                        item.JobId,
                        item.ActivityId,
                        item.Exists)).ToArray(),
                    model.BlockedSliceCount,
                    model.BlockedSamples.Select(item => new BlockedSliceResponse(
                        item.SliceStartUtc,
                        item.SliceEndUtc,
                        item.IsReady,
                        item.Missing.Select(missing => new MissingUpstreamSliceResponse(
                            missing.Reference,
                            missing.ActivityId,
                            missing.StartUtc,
                            missing.EndUtc)).ToArray())).ToArray()));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static Results<Ok<JobDetailResponse>, ProblemHttpResult> ChangeLifecycle(
            Guid jobId,
            HttpContext http,
            JobApplicationService jobs,
            Func<string, long, JobApplicationModel> change)
        {
            try
            {
                var id = Id(jobId);
                var current = jobs.Get(id);
                var expectedVersion = CatalogEtag.RequireMatch(http.Request, current.Record.CatalogVersion);
                var updated = change(id, expectedVersion);
                CatalogEtag.Write(http.Response, updated.Record.CatalogVersion);
                return TypedResults.Ok(AgentContractMapper.JobDetail(updated));
            }
            catch (ApplicationProblemException ex)
            {
                return HttpProblem.From(ex);
            }
        }

        private static string RequiredSchedule(JobWriteRequest request)
        {
            if (request.Schedule is not { } schedule || schedule.ValueKind != JsonValueKind.Object)
            {
                throw InvalidBody("'schedule' must be a JSON object.");
            }

            return schedule.GetRawText();
        }

        private static ApplicationProblemException InvalidBody(string detail)
        {
            return new ApplicationProblemException(
                StatusCodes.Status400BadRequest,
                "invalid-request-body",
                "The request body is invalid.",
                detail);
        }

        private static string CleanReason(string? reason, string fallback)
        {
            return string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();
        }

        private static string Id(Guid jobId)
        {
            return jobId.ToString("N");
        }
    }
}
