// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using KoLite.Local.Core.Rerun;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Repair;
using KoLite.Local.Sqlite.Rerun;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Repair;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class RerunModel : PageModel
    {
        private readonly SqliteRerunService reruns;
        private readonly SqliteRepairService repairs;
        private readonly SqliteChunkStateRepository chunkState;
        private readonly RepairApprovalCoordinator approvals;
        private readonly SqliteJobCatalogRepository catalog;

        public RerunModel(SqliteRerunService reruns, SqliteRepairService repairs, SqliteChunkStateRepository chunkState, RepairApprovalCoordinator approvals, SqliteJobCatalogRepository catalog)
        {
            this.reruns = reruns;
            this.repairs = repairs;
            this.chunkState = chunkState;
            this.approvals = approvals;
            this.catalog = catalog;
        }

        private static readonly string[] DateTimeLocalFormats =
        [
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
        ];

        [BindProperty(Name = "start")] public string StartInput { get; set; } = string.Empty;
        [BindProperty(Name = "end")] public string EndInput { get; set; } = string.Empty;
        [BindProperty(Name = "requestedBy")] public string RequestedBy { get; set; } = "local-web";
        [BindProperty(Name = "reason")] public string Reason { get; set; } = "Manual rerun";
        [BindProperty(Name = "mode")] public string Mode { get; set; } = "rerun";
        [BindProperty(Name = "expectedSliceCount")] public int ExpectedSliceCount { get; set; }
        [BindProperty(Name = "expectedExecutionCount")] public int? ExpectedExecutionCount { get; set; }
        [BindProperty(Name = "previewToken")] public string? PreviewToken { get; set; }

        public string JobId { get; private set; } = string.Empty;
        public string ActivityId { get; private set; } = string.Empty;
        public RerunPlanResult? Plan { get; private set; }
        public RepairPreviewResult? RepairPreview { get; private set; }
        public IReadOnlyList<DurableChunkState> RepairChunks { get; private set; } = Array.Empty<DurableChunkState>();
        public string? ErrorMessage { get; private set; }
        public bool IsRepairMode => string.Equals(Mode, "repair", StringComparison.OrdinalIgnoreCase);

        public IActionResult OnGet(string jobId, string? start, string? end, string? reason, string? requestedBy, string? mode)
        {
            JobId = jobId;
            ActivityId = jobId;
            var record = catalog.Get(jobId);
            if (record is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                ErrorMessage = $"Job '{jobId}' was not found.";
                return Page();
            }

            ActivityId = record.ActivityId;

            StartInput = start?.Trim() ?? string.Empty;
            EndInput = end?.Trim() ?? string.Empty;
            RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "local-web" : requestedBy.Trim();

            if (string.IsNullOrWhiteSpace(StartInput) && string.IsNullOrWhiteSpace(EndInput))
            {
                return Page();
            }

            if (!TryParseUtcInput(StartInput, "Start", out var startUtc, out var error) ||
                !TryParseUtcInput(EndInput, "End", out var endUtc, out error))
            {
                ErrorMessage = error;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            StartInput = AppFormatting.DateTimeInputUtc(startUtc!.Value);
            EndInput = AppFormatting.DateTimeInputUtc(endUtc!.Value);
            try
            {
                var repairRequest = BuildRepairRequest(jobId, startUtc.Value, endUtc.Value);
                var repairPreview = repairs.Preview(repairRequest);
                Mode = string.IsNullOrWhiteSpace(mode)
                    ? repairPreview.RepairableExecutions > 0 ? "repair" : "rerun"
                    : NormalizeMode(mode);
                Reason = string.IsNullOrWhiteSpace(reason)
                    ? IsRepairMode ? "Repair failed chunks" : "Manual rerun"
                    : reason.Trim();
                if (IsRepairMode)
                {
                    RepairPreview = repairs.Preview(BuildRepairRequest(jobId, startUtc.Value, endUtc.Value));
                    RepairChunks = LoadRepairChunks(jobId, RepairPreview);
                }
                else
                {
                    Plan = reruns.Plan(new RerunPlanRequest(jobId, startUtc.Value, endUtc.Value, RequestedBy, Reason));
                }
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                Response.StatusCode = StatusCodes.Status400BadRequest;
            }

            return Page();
        }

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
            ActivityId = catalog.Get(jobId)?.ActivityId ?? jobId;
            if (!TryParseUtcInput(StartInput, "Start", out var startUtc, out var error) ||
                !TryParseUtcInput(EndInput, "End", out var endUtc, out error))
            {
                ErrorMessage = error;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            try
            {
                Mode = NormalizeMode(Mode);
                if (IsRepairMode)
                {
                    var request = BuildRepairRequest(jobId, startUtc!.Value, endUtc!.Value);
                    var approved = approvals.Enqueue(request, ExpectedSliceCount, ExpectedExecutionCount, PreviewToken);
                    return Redirect(
                        $"/jobs/{Uri.EscapeDataString(jobId)}/slices" +
                        $"?start={Uri.EscapeDataString(AppFormatting.Iso(startUtc.Value))}" +
                        $"&end={Uri.EscapeDataString(AppFormatting.Iso(endUtc.Value))}" +
                        $"&repairBatchId={Uri.EscapeDataString(approved.Result.RepairBatchId)}");
                }

                var plan = reruns.CreatePlan(new RerunPlanRequest(
                    jobId,
                    startUtc!.Value,
                    endUtc!.Value,
                    string.IsNullOrWhiteSpace(RequestedBy) ? "local-web" : RequestedBy.Trim(),
                    string.IsNullOrWhiteSpace(Reason) ? "Manual rerun" : Reason.Trim()));
                return Redirect($"/reruns/{Uri.EscapeDataString(plan.RerunBatchId)}");
            }
            catch (RepairApprovalConflictException ex)
            {
                ErrorMessage = ex.Message;
                Response.StatusCode = StatusCodes.Status409Conflict;
                return Page();
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }
        }

        private RepairPlanRequest BuildRepairRequest(string jobId, DateTimeOffset startUtc, DateTimeOffset endUtc) =>
            new(
                jobId,
                startUtc,
                endUtc,
                string.IsNullOrWhiteSpace(RequestedBy) ? "local-web" : RequestedBy.Trim(),
                string.IsNullOrWhiteSpace(Reason) ? "Repair failed chunks" : Reason.Trim(),
                Scope: RepairSliceScope.FailedAndDeadLetteredOnly);

        private IReadOnlyList<DurableChunkState> LoadRepairChunks(string jobId, RepairPreviewResult preview)
        {
            var results = new List<DurableChunkState>();
            foreach (var slice in preview.Slices.Where(item => item.Outcome == RepairSliceOutcome.Repairable && item.ChunkIds is { Count: > 0 }))
            {
                var ids = slice.ChunkIds!.ToHashSet();
                results.AddRange(chunkState.List(new KoLite.Local.Core.Scheduling.SliceRange(jobId, slice.StartUtc, slice.EndUtc))
                    .Where(chunk => ids.Contains(chunk.ChunkId)));
            }

            return results
                .OrderBy(chunk => chunk.SliceStartUtc)
                .ThenBy(chunk => chunk.ChunkId)
                .ToArray();
        }

        private static string NormalizeMode(string? mode) =>
            string.Equals(mode, "repair", StringComparison.OrdinalIgnoreCase) ? "repair" : "rerun";

        private static bool TryParseUtcInput(string? value, string fieldName, out DateTimeOffset? parsed, out string? error)
        {
            parsed = null;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{fieldName} is required.";
                return false;
            }

            var trimmed = value.Trim();
            if (DateTime.TryParseExact(trimmed, DateTimeLocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localUtc))
            {
                parsed = new DateTimeOffset(DateTime.SpecifyKind(localUtc, DateTimeKind.Utc));
                return true;
            }

            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var offset))
            {
                parsed = offset.ToUniversalTime();
                return true;
            }

            error = $"{fieldName} must be a valid UTC date/time.";
            return false;
        }
    }
}
