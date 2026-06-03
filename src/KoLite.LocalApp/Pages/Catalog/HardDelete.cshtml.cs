using KoLite.Local.Sqlite.Lifecycle;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class HardDeleteModel : PageModel
    {
        private readonly SqliteJobLifecycleService lifecycle;

        public HardDeleteModel(SqliteJobLifecycleService lifecycle)
        {
            this.lifecycle = lifecycle;
        }

        [BindProperty(Name = "confirmation")] public string Confirmation { get; set; } = string.Empty;
        [BindProperty(Name = "reason")] public string Reason { get; set; } = "Hard deleted from web UI";
        public string JobId { get; private set; } = string.Empty;
        public string? ErrorMessage { get; private set; }
        public HardDeleteResult? Result { get; private set; }

        public void OnGet(string jobId) => JobId = jobId;

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
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
    }
}
