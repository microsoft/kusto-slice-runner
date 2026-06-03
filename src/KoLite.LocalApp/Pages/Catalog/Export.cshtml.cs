using KoLite.Local.Sqlite.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class ExportModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public ExportModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        public IActionResult OnGet(string jobId)
        {
            var record = catalog.Get(jobId);
            if (record is null)
            {
                return NotFound();
            }

            return Content(record.ScheduleJson, "application/json");
        }
    }
}
