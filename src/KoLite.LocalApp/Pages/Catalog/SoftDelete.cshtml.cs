using KoLite.Local.Sqlite.Lifecycle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class SoftDeleteModel : PageModel
    {
        private readonly SqliteJobLifecycleService lifecycle;

        public SoftDeleteModel(SqliteJobLifecycleService lifecycle)
        {
            this.lifecycle = lifecycle;
        }

        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        [BindProperty(Name = "reason")] public string Reason { get; set; } = "Soft deleted from web UI";

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost(string jobId)
        {
            lifecycle.SoftDelete(jobId, ExpectedVersion, actor: "local-web", reason: Reason);
            return Redirect("/");
        }
    }
}
