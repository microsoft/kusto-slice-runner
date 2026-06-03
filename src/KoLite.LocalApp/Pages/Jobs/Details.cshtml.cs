using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class DetailsModel : PageModel
    {
        private readonly JobDetailsPageQuery query;

        public DetailsModel(JobDetailsPageQuery query)
        {
            this.query = query;
        }

        public JobDetailsPageData? Data { get; private set; }
        public ScheduleEditorViewModel? Editor { get; private set; }

        public IActionResult OnGet(string jobId)
        {
            Data = query.Get(jobId);
            if (Data is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            Editor = new ScheduleEditorViewModel(
                $"/catalog/{Uri.EscapeDataString(Data.Job.JobId)}/update",
                ScheduleFormInput.FromDefinition(Data.Definition),
                AppFormatting.PrettyJson(Data.Job.ScheduleJson),
                Data.Job.CatalogVersion,
                true,
                "Save job",
                Data.HasStarted);
            return Page();
        }
    }
}
