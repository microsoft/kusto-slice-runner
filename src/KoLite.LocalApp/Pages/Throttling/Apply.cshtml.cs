using System.Text.Json.Nodes;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Throttling;
using KoLite.LocalApp.Pages.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Throttling
{
    // Applies a single operator-chosen parallelism reduction from the advisory page. Read-modifies the
    // job's stored schedule JSON (only maxParallelism) through the validated catalog update path, with
    // a server-side keep-up-floor guardrail so an apply can never starve a job below real-time needs.
    public sealed class ApplyModel : PageModel
    {
        public const string StatusMessageKey = "ThrottleApplyStatus";
        public const string StatusIsErrorKey = "ThrottleApplyIsError";

        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteThrottleAdvisorReadModel advisor;

        public ApplyModel(SqliteJobCatalogRepository catalog, SqliteThrottleAdvisorReadModel advisor)
        {
            this.catalog = catalog;
            this.advisor = advisor;
        }

        [BindProperty(Name = "jobId")] public string JobId { get; set; } = string.Empty;
        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        [BindProperty(Name = "newMaxParallelism")] public int NewMaxParallelism { get; set; }

        public IActionResult OnGet() => Redirect("/throttling");

        public IActionResult OnPost()
        {
            var record = catalog.Get(JobId);
            if (record is null)
            {
                return Fail($"Job '{JobId}' no longer exists; refresh the recommendations.");
            }

            var current = record.Definition.MaxParallelism;
            if (NewMaxParallelism < 1)
            {
                return Fail("maxParallelism must be at least 1.");
            }

            if (NewMaxParallelism >= current)
            {
                return Fail($"Skipped '{record.ActivityId}': {NewMaxParallelism} is not below the current maxParallelism of {current}.");
            }

            // Server-side keep-up guardrail: never reduce below the floor, and refuse when the floor
            // cannot be verified from recent successful slices.
            var floor = advisor.EstimateKeepUpFloor(JobId);
            if (floor is null)
            {
                return Fail($"Not enough recent successful slices to verify the keep-up floor for '{record.ActivityId}'; not reducing.");
            }

            if (NewMaxParallelism < floor)
            {
                return Fail($"Rejected: {NewMaxParallelism} is below the keep-up floor of {floor} for '{record.ActivityId}'.");
            }

            try
            {
                var node = JsonNode.Parse(record.ScheduleJson)!.AsObject();
                node["maxParallelism"] = NewMaxParallelism;
                catalog.Update(JobId, node.ToJsonString(), ExpectedVersion, actor: "throttle-advisor");
            }
            catch (CatalogVersionConflictException)
            {
                return Fail(CatalogConflictFeedback.Message);
            }
            catch (InvalidOperationException ex)
            {
                // Optimistic-concurrency conflict or a mutation-policy violation: the job changed since
                // the recommendation was rendered. Surface it and let the operator re-read.
                return Fail($"Could not update '{record.ActivityId}': {ex.Message}");
            }

            return Succeed($"Reduced '{record.ActivityId}' maxParallelism from {current} to {NewMaxParallelism}.");
        }

        private IActionResult Succeed(string message)
        {
            TempData[StatusMessageKey] = message;
            TempData[StatusIsErrorKey] = false;
            return Redirect("/throttling");
        }

        private IActionResult Fail(string message)
        {
            TempData[StatusMessageKey] = message;
            TempData[StatusIsErrorKey] = true;
            return Redirect("/throttling");
        }
    }
}
