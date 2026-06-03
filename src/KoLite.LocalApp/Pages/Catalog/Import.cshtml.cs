using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class ImportModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public ImportModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        [BindProperty(Name = "scheduleJson")] public string ScheduleJson { get; set; } = AppFormatting.PrettyJson(SampleScheduleFactory.CreateJson());
        [BindProperty(Name = "importSource")] public string ImportSource { get; set; } = "paste";
        [BindProperty(Name = "scheduleFile")] public IFormFile? ScheduleFile { get; set; }
        public string? Message { get; private set; }
        public string? ErrorMessage { get; private set; }
        public JobCatalogImportResult? ImportResult { get; private set; }

        public void OnGet()
        {
        }

        public async Task<IActionResult> OnPost()
        {
            try
            {
                var importJson = await ReadImportJson();
                ScheduleJson = importJson;
                var result = catalog.Import(importJson, actor: "local-web-import");
                if (result.Total == 1)
                {
                    return Redirect($"/jobs/{Uri.EscapeDataString(result.Items[0].JobId)}");
                }

                ImportResult = result;
                Message = $"Imported {result.Total} jobs: {result.Created} created, {result.Updated} updated. No jobs were deleted.";
                return Page();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
            {
                Response.StatusCode = StatusCodes.Status400BadRequest;
                ErrorMessage = ex.Message;
                return Page();
            }
        }

        private async Task<string> ReadImportJson()
        {
            if (string.Equals(ImportSource, "file", StringComparison.OrdinalIgnoreCase))
            {
                if (ScheduleFile is null || ScheduleFile.Length == 0)
                {
                    throw new InvalidOperationException("Choose a JSON file to import.");
                }

                using var reader = new StreamReader(ScheduleFile.OpenReadStream());
                var fileText = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(fileText))
                {
                    throw new InvalidOperationException("The selected JSON file is empty.");
                }

                return fileText;
            }

            if (string.IsNullOrWhiteSpace(ScheduleJson))
            {
                throw new InvalidOperationException("Paste schedule JSON to import.");
            }

            return ScheduleJson;
        }
    }
}
