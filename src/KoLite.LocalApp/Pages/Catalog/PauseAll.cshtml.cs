using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class PauseAllModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly LifecycleReadModel lifecycle;

        public PauseAllModel(SqliteJobCatalogRepository catalog, LifecycleReadModel lifecycle)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
        }

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);
        public IActionResult OnPost()
        {
            var deleted = lifecycle.GetLatestStates().Where(s => s.Value.IsSoftDeleted).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var job in catalog.List().Where(j => j.IsEnabled && !deleted.Contains(j.JobId)))
            {
                catalog.SetEnabled(job.JobId, false, job.CatalogVersion, actor: "local-web");
            }

            return Redirect("/");
        }
    }
}
