using KoLite.Local.Sqlite.Lifecycle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class RestoreModel : PageModel
    {
        private readonly SqliteJobLifecycleService lifecycle;

        public RestoreModel(SqliteJobLifecycleService lifecycle)
        {
            this.lifecycle = lifecycle;
        }

        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        [BindProperty(Name = "reason")] public string Reason { get; set; } = "Restored from web UI";

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost(string jobId)
        {
            lifecycle.Restore(jobId, ExpectedVersion, actor: "local-web", reason: Reason);
            return Redirect($"/jobs/{Uri.EscapeDataString(jobId)}");
        }
    }
}
