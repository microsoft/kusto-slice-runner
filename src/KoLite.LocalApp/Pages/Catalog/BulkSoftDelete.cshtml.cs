using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class BulkSoftDeleteModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteJobLifecycleService lifecycle;
        private readonly LifecycleReadModel lifecycleReadModel;

        public BulkSoftDeleteModel(
            SqliteJobCatalogRepository catalog,
            SqliteJobLifecycleService lifecycle,
            LifecycleReadModel lifecycleReadModel)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
            this.lifecycleReadModel = lifecycleReadModel;
        }

        [BindProperty(Name = "jobIds")] public string[] JobIds { get; set; } = Array.Empty<string>();
        [BindProperty(Name = "expectedVersions")] public long[] ExpectedVersions { get; set; } = Array.Empty<long>();

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost()
        {
            var result = CatalogBulkOperations.SoftDelete(catalog, lifecycle, lifecycleReadModel, JobIds, ExpectedVersions);
            TempData[CatalogBulkOperations.TempDataKey] = result.ToMessage("Soft-deleted");
            return Redirect("/");
        }
    }
}
