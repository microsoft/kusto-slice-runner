using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class UpdateModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public UpdateModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        [BindProperty] public ScheduleFormInput Input { get; set; } = ScheduleFormInput.Default();
        [BindProperty(Name = "scheduleJson")] public string ScheduleJson { get; set; } = SampleScheduleFactory.CreateJson();
        [BindProperty] public string FormMode { get; set; } = "fields";
        [BindProperty(Name = "expectedVersion")] public long ExpectedVersion { get; set; }
        public string JobId { get; private set; } = string.Empty;
        public string? ErrorMessage { get; private set; }
        public ScheduleEditorViewModel Editor => new($"/catalog/{Uri.EscapeDataString(JobId)}/update", Input, ScheduleJson, ExpectedVersion, true, "Save job", catalog.HasStarted(JobId));

        public IActionResult OnGet() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
            Input.ActivityId = jobId;
            var useRawJson = string.Equals(FormMode, "json", StringComparison.OrdinalIgnoreCase)
                || (Request.Form.ContainsKey("scheduleJson") && !Request.Form.ContainsKey("Input.ActivityId"));
            var scheduleJson = useRawJson
                ? ScheduleJson
                : Input.ToScheduleJson();

            try
            {
                catalog.Update(jobId, scheduleJson, ExpectedVersion, actor: "local-web");
                return Redirect($"/jobs/{Uri.EscapeDataString(jobId)}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
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
