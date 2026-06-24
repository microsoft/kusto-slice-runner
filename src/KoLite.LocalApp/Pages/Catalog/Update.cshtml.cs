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
        public ScheduleEditorViewModel? Editor { get; private set; }

        public IActionResult OnGet(string jobId)
        {
            // /catalog/{jobId}/update is POST-only; the GET entry point is the /catalog/{jobId}/edit alias.
            if (!IsEditRoute())
            {
                return StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            var record = catalog.Get(jobId);
            if (record is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return Page();
            }

            JobId = record.JobId;
            ExpectedVersion = record.CatalogVersion;
            Editor = new ScheduleEditorViewModel(
                $"/catalog/{Uri.EscapeDataString(record.JobId)}/update",
                ScheduleFormInput.FromDefinition(record.Definition),
                AppFormatting.PrettyJson(record.ScheduleJson),
                record.CatalogVersion,
                true,
                "Save job",
                catalog.HasStarted(record.JobId));
            return Page();
        }

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
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

                Editor = new ScheduleEditorViewModel($"/catalog/{Uri.EscapeDataString(JobId)}/update", Input, ScheduleJson, ExpectedVersion, true, "Save job", catalog.HasStarted(JobId));
                return Page();
            }
        }

        private bool IsEditRoute()
        {
            var path = Request.Path.Value;
            return path is not null && path.TrimEnd('/').EndsWith("/edit", StringComparison.OrdinalIgnoreCase);
        }
    }
}
