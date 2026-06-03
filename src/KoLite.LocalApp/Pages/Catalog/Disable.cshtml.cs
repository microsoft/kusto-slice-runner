using KoLite.Local.Sqlite.Catalog;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class DisableModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public DisableModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);
        public IActionResult OnPost(string jobId)
        {
            catalog.SetEnabled(jobId, false, ExpectedVersion, actor: "local-web");
            return Redirect($"/jobs/{Uri.EscapeDataString(jobId)}");
        }
    }
}
