using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Lifecycle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class HardDeleteModel : PageModel
    {
        private readonly SqliteJobLifecycleService lifecycle;
        private readonly SqliteJobCatalogRepository catalog;

        public HardDeleteModel(SqliteJobLifecycleService lifecycle, SqliteJobCatalogRepository catalog)
        {
            this.lifecycle = lifecycle;
            this.catalog = catalog;
        }

        [BindProperty(Name = "confirmation")] public string Confirmation { get; set; } = string.Empty;
        [BindProperty(Name = "reason")] public string Reason { get; set; } = "Hard deleted from web UI";
        public string JobId { get; private set; } = string.Empty;
        public string DisplayName { get; private set; } = string.Empty;
        public string? ErrorMessage { get; private set; }
        public HardDeleteResult? Result { get; private set; }

        public void OnGet(string jobId)
        {
            JobId = jobId;
            DisplayName = ResolveDisplayName(jobId);
        }

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
            DisplayName = ResolveDisplayName(jobId);
            try
            {
                Result = lifecycle.HardDelete(jobId, Confirmation, actor: "local-web", reason: Reason);
                return Page();
            }
            catch (InvalidOperationException ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                ErrorMessage = ex.Message;
                return Page();
            }
        }

        // The confirmation phrase and heading use the display name; fall back to the GUID when the
        // job is missing so a stale link still renders instead of showing an empty confirmation.
        private string ResolveDisplayName(string jobId) => catalog.Get(jobId)?.DisplayName ?? jobId;
    }
}
