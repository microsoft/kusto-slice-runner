// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Repair;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Repair;

namespace KoLite.LocalApp.Repair
{
    public sealed record ApprovedRepair(RepairPreviewResult Preview, RepairPlanResult Result);

    public sealed class RepairApprovalConflictException : InvalidOperationException
    {
        public RepairApprovalConflictException(
            string message,
            RepairPreviewResult? preview = null,
            int? expectedSliceCount = null,
            int? expectedExecutionCount = null,
            string? expectedPreviewToken = null)
            : base(message)
        {
            Preview = preview;
            ExpectedSliceCount = expectedSliceCount;
            ExpectedExecutionCount = expectedExecutionCount;
            ExpectedPreviewToken = expectedPreviewToken;
        }

        public RepairPreviewResult? Preview { get; }
        public int? ExpectedSliceCount { get; }
        public int? ExpectedExecutionCount { get; }
        public string? ExpectedPreviewToken { get; }
    }

    public sealed class RepairApprovalCoordinator
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteRepairService repairs;

        public RepairApprovalCoordinator(SqliteJobCatalogRepository catalog, SqliteRepairService repairs)
        {
            this.catalog = catalog;
            this.repairs = repairs;
        }

        public ApprovedRepair Enqueue(
            RepairPlanRequest request,
            int expectedSliceCount,
            int? expectedExecutionCount,
            string? expectedPreviewToken)
        {
            var record = catalog.Get(request.JobId)
                ?? throw new InvalidOperationException($"Job '{request.JobId}' does not exist.");
            if (!record.IsEnabled)
            {
                throw new RepairApprovalConflictException(
                    $"Job '{record.DisplayName}' was paused or soft-deleted while the repair was being prepared. Resume it and retry.");
            }

            var preview = repairs.Preview(request);
            if (preview.Repairable != expectedSliceCount)
            {
                throw new RepairApprovalConflictException(
                    $"Expected {expectedSliceCount} repairable slice(s) but found {preview.Repairable}. Re-run the preview and retry.",
                    preview,
                    expectedSliceCount,
                    expectedExecutionCount,
                    expectedPreviewToken);
            }

            if (!StringComparer.Ordinal.Equals(preview.PreviewToken, expectedPreviewToken))
            {
                throw new RepairApprovalConflictException(
                    "The repairable slice set changed or was not acknowledged. Re-run the preview and retry.",
                    preview,
                    expectedSliceCount,
                    expectedExecutionCount,
                    expectedPreviewToken);
            }

            if (record.Definition.Chunks is not null
                && preview.RepairableExecutions != expectedExecutionCount)
            {
                throw new RepairApprovalConflictException(
                    "The repairable chunk set changed or was not acknowledged. Re-run the preview and retry.",
                    preview,
                    expectedSliceCount,
                    expectedExecutionCount,
                    expectedPreviewToken);
            }

            return new ApprovedRepair(preview, repairs.PlanAndEnqueue(request));
        }
    }
}
