using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class EnableModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly DashboardPageQuery dashboard;

        public EnableModel(SqliteJobCatalogRepository catalog, DashboardPageQuery dashboard)
        {
            this.catalog = catalog;
            this.dashboard = dashboard;
        }

        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);
        public IActionResult OnPost(string jobId) =>
            CatalogEnabledToggle.Execute(this, catalog, dashboard, jobId, enabled: true, ExpectedVersion);
    }
}
