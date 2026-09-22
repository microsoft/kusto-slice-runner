// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Repair;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Repair;
using KoLite.LocalApp.Repair;

namespace KoLite.LocalApp.Application.Repair
{
    public sealed record RepairApplicationRequest(
        string JobId,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        string Reason);

    public sealed record RepairApplicationResult(
        JobCatalogRecord Job,
        RepairPreviewResult Preview,
        RepairPlanResult Result,
        IReadOnlyList<RepairSlice> Slices,
        IReadOnlyList<RepairChunkExecution> Chunks);

    public sealed class RepairApplicationService
    {
        private const string Actor = "local-api";
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteRepairService repairs;
        private readonly RepairApprovalCoordinator approvals;

        public RepairApplicationService(
            SqliteJobCatalogRepository catalog,
            SqliteRepairService repairs,
            RepairApprovalCoordinator approvals)
        {
            this.catalog = catalog;
            this.repairs = repairs;
            this.approvals = approvals;
        }

        public (JobCatalogRecord Job, RepairPreviewResult Preview) Preview(RepairApplicationRequest request)
        {
            var job = ResolveEnabledJob(request.JobId);
            var plan = BuildPlan(job, request);
            try
            {
                return (job, repairs.Preview(plan));
            }
            catch (InvalidOperationException ex)
            {
                throw InvalidRepair(ex);
            }
        }

        public RepairApplicationResult Enqueue(
            RepairApplicationRequest request,
            int expectedSliceCount,
            int? expectedExecutionCount,
            string? previewToken)
        {
            var job = ResolveEnabledJob(request.JobId);
            var plan = BuildPlan(job, request);
            ApprovedRepair approved;
            try
            {
                approved = approvals.Enqueue(plan, expectedSliceCount, expectedExecutionCount, previewToken);
            }
            catch (RepairApprovalConflictException ex)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status409Conflict,
                    "repair-preview-conflict",
                    "The repair preview is stale.",
                    ex.Message,
                    new Dictionary<string, object?>
                    {
                        ["expectedSliceCount"] = ex.ExpectedSliceCount,
                        ["actualSliceCount"] = ex.Preview?.Repairable,
                        ["expectedExecutionCount"] = ex.ExpectedExecutionCount,
                        ["actualExecutionCount"] = ex.Preview?.RepairableExecutions,
                        ["expectedPreviewToken"] = ex.ExpectedPreviewToken,
                        ["actualPreviewToken"] = ex.Preview?.PreviewToken
                    },
                    ex);
            }
            catch (InvalidOperationException ex)
            {
                throw InvalidRepair(ex);
            }

            return new RepairApplicationResult(
                job,
                approved.Preview,
                approved.Result,
                repairs.GetRepairSlices(approved.Result.RepairBatchId),
                repairs.GetRepairChunkExecutions(approved.Result.RepairBatchId));
        }

        private JobCatalogRecord ResolveEnabledJob(string jobId)
        {
            var job = catalog.Get(jobId)
                ?? throw new ApplicationProblemException(
                    StatusCodes.Status404NotFound,
                    "job-not-found",
                    "Job not found.",
                    $"Job '{jobId}' does not exist.");
            if (!job.IsEnabled)
            {
                throw new ApplicationProblemException(
                    StatusCodes.Status409Conflict,
                    "job-not-active",
                    "The job is not active.",
                    $"Job '{job.ActivityId}' is paused or soft-deleted. Resume or restore it before creating repair work.");
            }

            return job;
        }

        private static RepairPlanRequest BuildPlan(JobCatalogRecord job, RepairApplicationRequest request)
        {
            try
            {
                SqliteRepairService.ValidateAlignedRange(job.Definition, request.FromUtc, request.ToUtc);
            }
            catch (InvalidOperationException ex)
            {
                throw InvalidRepair(ex);
            }

            return new RepairPlanRequest(
                job.JobId,
                request.FromUtc,
                request.ToUtc,
                Actor,
                request.Reason,
                RepairOutputStrategy.ExecuteNoCleanup,
                RepairSliceScope.FailedAndDeadLetteredOnly);
        }

        private static ApplicationProblemException InvalidRepair(Exception ex)
        {
            return new ApplicationProblemException(
                StatusCodes.Status400BadRequest,
                "invalid-repair-request",
                "The repair request is invalid.",
                ex.Message,
                innerException: ex);
        }
    }
}
