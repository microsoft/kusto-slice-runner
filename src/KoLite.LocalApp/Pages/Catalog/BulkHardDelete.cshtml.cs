using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed record BulkHardDeleteReviewJob(string JobId, string DisplayName, long CatalogVersion);

    public sealed class BulkHardDeleteModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteJobLifecycleService lifecycle;
        private readonly LifecycleReadModel lifecycleReadModel;

        public BulkHardDeleteModel(
            SqliteJobCatalogRepository catalog,
            SqliteJobLifecycleService lifecycle,
            LifecycleReadModel lifecycleReadModel)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
            this.lifecycleReadModel = lifecycleReadModel;
        }

        [BindProperty(Name = "jobIds")]
        public string[] JobIds { get; set; } = Array.Empty<string>();

        [BindProperty(Name = "expectedVersions")]
        public long[] ExpectedVersions { get; set; } = Array.Empty<long>();

        [BindProperty(Name = "confirmation")]
        public string Confirmation { get; set; } = string.Empty;

        [BindProperty(Name = "reason")]
        public string Reason { get; set; } = "Hard deleted from web UI (bulk)";

        [BindProperty(Name = "execute")]
        public bool Execute { get; set; }

        public IReadOnlyList<BulkHardDeleteReviewJob> ReviewJobs { get; private set; } = Array.Empty<BulkHardDeleteReviewJob>();
        public IReadOnlyList<HardDeleteBatchIssue> Issues { get; private set; } = Array.Empty<HardDeleteBatchIssue>();
        public string? ErrorMessage { get; private set; }
        public HardDeleteBatchResult? Result { get; private set; }
        public string ConfirmationPhrase => SqliteJobLifecycleService.BatchConfirmation(ReviewJobs.Count);

        public IActionResult OnGet()
        {
            TempData[CatalogBulkOperations.TempDataKey] = "Select one or more soft-deleted jobs before choosing Hard delete.";
            return Redirect("/");
        }

        public IActionResult OnPost()
        {
            if (!LoadReview())
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            if (!Execute)
            {
                return Page();
            }

            try
            {
                Result = lifecycle.HardDeleteBatch(
                    ReviewJobs.Select(job => new HardDeleteBatchItem(job.JobId, job.CatalogVersion)).ToArray(),
                    Confirmation,
                    actor: "local-web",
                    reason: Reason);
                return Page();
            }
            catch (HardDeleteBatchValidationException ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                Issues = ex.Issues;
                ErrorMessage = "No jobs were deleted. Review the blockers, then return to the dashboard and select the jobs again.";
                return Page();
            }
            catch (InvalidOperationException ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                ErrorMessage = ex.Message;
                return Page();
            }
        }

        private bool LoadReview()
        {
            IReadOnlyList<(string JobId, long ExpectedVersion)> pairs;
            try
            {
                pairs = CatalogBulkOperations.Pair(JobIds, ExpectedVersions);
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                return false;
            }

            if (pairs.Count == 0)
            {
                ErrorMessage = "Select at least one soft-deleted job.";
                return false;
            }

            var latestStates = lifecycleReadModel.GetLatestStates();
            var reviewJobs = new List<BulkHardDeleteReviewJob>(pairs.Count);
            var issues = new List<HardDeleteBatchIssue>();
            foreach (var (jobId, expectedVersion) in pairs)
            {
                var current = catalog.Get(jobId);
                if (current is null)
                {
                    issues.Add(new HardDeleteBatchIssue(jobId, jobId, "The job no longer exists."));
                    continue;
                }

                reviewJobs.Add(new BulkHardDeleteReviewJob(current.JobId, current.DisplayName, expectedVersion));
                if (current.CatalogVersion != expectedVersion)
                {
                    issues.Add(new HardDeleteBatchIssue(
                        current.JobId,
                        current.DisplayName,
                        $"The job changed since it was selected (expected catalog version {expectedVersion}, found {current.CatalogVersion})."));
                }

                if (!latestStates.TryGetValue(jobId, out var state) || !state.IsSoftDeleted)
                {
                    issues.Add(new HardDeleteBatchIssue(current.JobId, current.DisplayName, "The job is no longer soft-deleted."));
                }
            }

            ReviewJobs = reviewJobs
                .OrderBy(job => job.DisplayName, StringComparer.Ordinal)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .ToArray();
            Issues = issues;
            if (issues.Count > 0)
            {
                ErrorMessage = "No jobs were deleted. Return to the dashboard and review the refreshed soft-deleted jobs.";
                return false;
            }

            return true;
        }
    }
}
