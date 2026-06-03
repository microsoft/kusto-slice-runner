using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class ExportAllModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly LifecycleReadModel lifecycle;

        public ExportAllModel(SqliteJobCatalogRepository catalog, LifecycleReadModel lifecycle)
        {
            this.catalog = catalog;
            this.lifecycle = lifecycle;
        }

        public IActionResult OnGet()
        {
            var softDeleted = lifecycle.GetLatestStates()
                .Where(state => state.Value.IsSoftDeleted)
                .Select(state => state.Key)
                .ToHashSet(StringComparer.Ordinal);

            return Content(catalog.ExportAll(softDeleted), "application/json");
        }
    }
}
