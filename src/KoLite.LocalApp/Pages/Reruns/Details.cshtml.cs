using KoLite.Local.Core.Rerun;
using KoLite.Local.Sqlite.Rerun;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Reruns
{
    public sealed class DetailsModel : PageModel
    {
        private readonly SqliteRerunService reruns;

        public DetailsModel(SqliteRerunService reruns)
        {
            this.reruns = reruns;
        }

        public RerunBatchReadout? Batch { get; private set; }

        public IActionResult OnGet(string rerunBatchId)
        {
            Batch = reruns.GetBatch(rerunBatchId);
            if (Batch is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
            }

            return Page();
        }
    }
}
