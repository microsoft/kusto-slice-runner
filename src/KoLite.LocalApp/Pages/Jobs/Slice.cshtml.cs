// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Sqlite.Repair;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class SliceModel : PageModel
    {
        private readonly JobDetailsPageQuery query;
        private readonly SqliteRepairService repair;

        public SliceModel(JobDetailsPageQuery query, SqliteRepairService repair)
        {
            this.query = query;
            this.repair = repair;
        }

        public SliceDetailsPageData? Data { get; private set; }
        public string? StatusMessage { get; private set; }
        public string? ErrorMessage { get; private set; }

        public IActionResult OnGet(string jobId, DateTimeOffset start, DateTimeOffset end, string? repairBatchId)
        {
            Data = query.GetSlice(jobId, start, end);
            if (Data is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
            }
            else if (!string.IsNullOrWhiteSpace(repairBatchId))
            {
                var repairedChunks = repair.GetRepairChunkExecutions(repairBatchId);
                StatusMessage = repairedChunks.Count == 0
                    ? $"Repair batch {repairBatchId} completed without queueing chunk work."
                    : $"Repair batch {repairBatchId} queued chunk ID(s) {string.Join(", ", repairedChunks.Select(chunk => chunk.ChunkId))}.";
            }

            return Page();
        }

        public IActionResult OnPostRecover(string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            try
            {
                StatusMessage = repair.RecoverOrphanedSlice(jobId, start, end, "local-web", "Manual orphaned-lease recovery")
                    ? "Orphaned slice re-queued for re-execution. The re-run is idempotent and will not duplicate Kusto output."
                    : null;
                if (StatusMessage is null)
                {
                    ErrorMessage = "This slice no longer has an orphaned lease to recover.";
                }
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                Response.StatusCode = StatusCodes.Status400BadRequest;
            }

            Data = query.GetSlice(jobId, start, end);
            if (Data is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
            }

            return Page();
        }
    }
}
