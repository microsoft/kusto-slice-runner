using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class BulkPauseModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly LifecycleReadModel lifecycle;

        public BulkPauseModel(SqliteJobCatalogRepository catalog, LifecycleReadModel lifecycle)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
        }

        [BindProperty(Name = "jobIds")] public string[] JobIds { get; set; } = Array.Empty<string>();
        [BindProperty(Name = "expectedVersions")] public long[] ExpectedVersions { get; set; } = Array.Empty<long>();

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost()
        {
            var result = CatalogBulkOperations.SetEnabled(catalog, lifecycle, JobIds, ExpectedVersions, targetEnabled: false);
            TempData[CatalogBulkOperations.TempDataKey] = result.ToMessage("Paused");
            return Redirect("/");
        }
    }
}
