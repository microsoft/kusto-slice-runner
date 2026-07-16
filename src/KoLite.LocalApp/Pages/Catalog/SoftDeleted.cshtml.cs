using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class SoftDeletedModel : PageModel
    {
        private readonly DashboardPageQuery query;

        public SoftDeletedModel(DashboardPageQuery query)
        {
            this.query = query;
        }

        public IReadOnlyList<JobListItem> Jobs { get; private set; } = Array.Empty<JobListItem>();

        public void OnGet()
        {
            Jobs = query.GetAllJobs()
                .Where(job => StringComparer.Ordinal.Equals(job.PrimaryState, "SoftDeleted"))
                .OrderBy(job => job.Record.ActivityId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(job => job.Record.JobId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
