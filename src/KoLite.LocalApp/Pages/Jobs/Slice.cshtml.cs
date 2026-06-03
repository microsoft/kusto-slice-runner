using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class SliceModel : PageModel
    {
        private readonly JobDetailsPageQuery query;

        public SliceModel(JobDetailsPageQuery query)
        {
            this.query = query;
        }

        public SliceDetailsPageData? Data { get; private set; }

        public IActionResult OnGet(string jobId, DateTimeOffset start, DateTimeOffset end)
        {
            Data = query.GetSlice(jobId, start, end);
            if (Data is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
            }

            return Page();
        }
    }
}
