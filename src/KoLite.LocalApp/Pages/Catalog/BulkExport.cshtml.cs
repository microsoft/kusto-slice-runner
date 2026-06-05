using System.Text;
using KoLite.Local.Sqlite.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class BulkExportModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public BulkExportModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        [BindProperty(Name = "jobIds")] public string[] JobIds { get; set; } = Array.Empty<string>();

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost()
        {
            var json = catalog.ExportSelected(JobIds);
            return File(Encoding.UTF8.GetBytes(json), "application/json", "ko-lite-jobs.json");
        }
    }
}
