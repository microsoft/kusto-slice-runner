using KoLite.Local.Sqlite.Catalog;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Catalog
{
    public sealed class EditModel : PageModel
    {
        private readonly SqliteJobCatalogRepository catalog;

        public EditModel(SqliteJobCatalogRepository catalog)
        {
            this.catalog = catalog;
        }

        public string JobId { get; private set; } = string.Empty;
        public long ExpectedVersion { get; private set; }
        public ScheduleEditorViewModel? Editor { get; private set; }

        public IActionResult OnGet(string jobId)
        {
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
    }
}
