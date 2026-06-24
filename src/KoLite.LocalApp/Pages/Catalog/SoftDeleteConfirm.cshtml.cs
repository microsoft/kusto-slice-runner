using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    // Confirmation page shown when a soft-delete is blocked because the job still has active downstream
    // dependents. It lists those dependents and offers an explicit force ("Soft delete anyway") POST
    // back to /catalog/{jobId}/soft-delete. Mirrors the HardDelete page's DI, route, and markup style.
    public sealed class SoftDeleteConfirmModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteJobLifecycleService lifecycle;

        public SoftDeleteConfirmModel(SqliteJobCatalogRepository catalog, SqliteJobLifecycleService lifecycle)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
        }

        public string JobId { get; private set; } = string.Empty;
        public string ActivityId { get; private set; } = string.Empty;
        public long CatalogVersion { get; private set; }
        public IReadOnlyList<(string JobId, string ActivityId)> Dependents { get; private set; } = Array.Empty<(string JobId, string ActivityId)>();

        public IActionResult OnGet(string jobId)
        {
            var record = catalog.Get(jobId);
            if (record is null)
            {
                return NotFound();
            }

            JobId = record.JobId;
            ActivityId = record.ActivityId;
            CatalogVersion = record.CatalogVersion;

            // Recomputed fresh on GET: if the dependents were removed since the blocked POST (a race),
            // the list is empty and the page renders a plain confirm instead.
            Dependents = lifecycle.GetActiveDependents(record.JobId);
            return Page();
        }
    }
}
