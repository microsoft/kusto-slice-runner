using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class CreateModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public CreateModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        [BindProperty] public ScheduleFormInput Input { get; set; } = ScheduleFormInput.Default();
        [BindProperty(Name = "scheduleJson")] public string ScheduleJson { get; set; } = SampleScheduleFactory.CreateJson();
        [BindProperty] public string FormMode { get; set; } = "fields";
        public string? ErrorMessage { get; private set; }
        public ScheduleEditorViewModel Editor => new("/catalog/create", Input, ScheduleJson, null, false, "Create job");

        public IActionResult OnGet()
        {
            // /catalog/create is POST-only; the GET entry point is the /catalog/new alias.
            if (!IsNewEntry())
            {
                return StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            ScheduleJson = AppFormatting.PrettyJson(SampleScheduleFactory.CreateJson());
            return Page();
        }

        public IActionResult OnPost()
        {
            if (IsNewEntry())
            {
                return StatusCode(StatusCodes.Status405MethodNotAllowed);
            }

            var useRawJson = string.Equals(FormMode, "json", StringComparison.OrdinalIgnoreCase)
                || (Request.Form.ContainsKey("scheduleJson") && !Request.Form.ContainsKey("Input.ActivityId"));
            var scheduleJson = useRawJson
                ? ScheduleJson
                : Input.ToScheduleJson();

            try
            {
                var record = catalog.Create(scheduleJson, actor: "local-web");
                return Redirect($"/jobs/{Uri.EscapeDataString(record.JobId)}");
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

        private bool IsNewEntry() =>
            string.Equals(Request.Path.Value?.TrimEnd('/'), "/catalog/new", StringComparison.OrdinalIgnoreCase);
    }
}
