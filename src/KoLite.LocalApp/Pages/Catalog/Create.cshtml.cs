using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Application;
using KoLite.LocalApp.Application.Jobs;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class CreateModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly JobApplicationService jobs;

        public CreateModel(SqliteJobCatalogRepository catalog, JobApplicationService jobs)
        {
            this.catalog = catalog;
            this.jobs = jobs;
        }

        [BindProperty] public ScheduleFormInput Input { get; set; } = ScheduleFormInput.Default();
        [BindProperty(Name = "scheduleJson")] public string ScheduleJson { get; set; } = SampleScheduleFactory.CreateJson();
        [BindProperty] public string FormMode { get; set; } = "fields";
        public string? ErrorMessage { get; private set; }
        public ScheduleEditorViewModel Editor => new("/jobs/new", Input, ScheduleJson, null, false, "Create job", false, ScheduleEditorViewModel.BuildOptions(catalog, null));

        public IActionResult OnGet()
        {
            ScheduleJson = AppFormatting.PrettyJson(SampleScheduleFactory.CreateJson());
            return Page();
        }

        public IActionResult OnPost()
        {
            var useRawJson = string.Equals(FormMode, "json", StringComparison.OrdinalIgnoreCase)
                || (Request.Form.ContainsKey("scheduleJson") && !Request.Form.ContainsKey("Input.ActivityId"));
            var scheduleJson = useRawJson
                ? ScheduleJson
                : Input.ToScheduleJson();

            try
            {
                var record = jobs.Create(scheduleJson, actor: "local-web");
                return Redirect($"/jobs/{Uri.EscapeDataString(record.Record.JobId)}");
            }
            catch (ApplicationProblemException ex)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                ErrorMessage = ex.Message;
                ScheduleJson = scheduleJson;
                if (useRawJson)
                {
                    Input = ScheduleFormInput.FromJson(scheduleJson);
                }

                return Page();
            }
        }

    }
}
