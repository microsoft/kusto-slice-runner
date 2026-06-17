using System.Globalization;
using KoLite.Local.Core.Rerun;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Rerun;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class RerunModel : PageModel
    {
        private readonly SqliteRerunService reruns;
        private readonly SqliteJobCatalogRepository catalog;

        public RerunModel(SqliteRerunService reruns, SqliteJobCatalogRepository catalog)
        {
            this.reruns = reruns;
            this.catalog = catalog;
        }

        private static readonly string[] DateTimeLocalFormats =
        [
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
        ];

        [BindProperty(Name = "start")] public string StartInput { get; set; } = string.Empty;
        [BindProperty(Name = "end")] public string EndInput { get; set; } = string.Empty;
        [BindProperty(Name = "requestedBy")] public string RequestedBy { get; set; } = "local-web";
        [BindProperty(Name = "reason")] public string Reason { get; set; } = "Manual rerun";

        public string JobId { get; private set; } = string.Empty;
        public string ActivityId { get; private set; } = string.Empty;
        public RerunPlanResult? Plan { get; private set; }
        public string? ErrorMessage { get; private set; }

        public IActionResult OnGet(string jobId, string? start, string? end, string? reason, string? requestedBy)
        {
            JobId = jobId;
            ActivityId = jobId;
            var record = catalog.Get(jobId);
            if (record is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                ErrorMessage = $"Job '{jobId}' was not found.";
                return Page();
            }

            ActivityId = record.ActivityId;

            StartInput = start?.Trim() ?? string.Empty;
            EndInput = end?.Trim() ?? string.Empty;
            Reason = string.IsNullOrWhiteSpace(reason) ? "Manual rerun" : reason.Trim();
            RequestedBy = string.IsNullOrWhiteSpace(requestedBy) ? "local-web" : requestedBy.Trim();

            if (string.IsNullOrWhiteSpace(StartInput) && string.IsNullOrWhiteSpace(EndInput))
            {
                return Page();
            }

            if (!TryParseUtcInput(StartInput, "Start", out var startUtc, out var error) ||
                !TryParseUtcInput(EndInput, "End", out var endUtc, out error))
            {
                ErrorMessage = error;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            StartInput = AppFormatting.DateTimeInputUtc(startUtc!.Value);
            EndInput = AppFormatting.DateTimeInputUtc(endUtc!.Value);
            try
            {
                Plan = reruns.Plan(new RerunPlanRequest(jobId, startUtc.Value, endUtc.Value, RequestedBy, Reason));
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                Response.StatusCode = StatusCodes.Status400BadRequest;
            }

            return Page();
        }

        public IActionResult OnPost(string jobId)
        {
            JobId = jobId;
            ActivityId = catalog.Get(jobId)?.ActivityId ?? jobId;
            if (!TryParseUtcInput(StartInput, "Start", out var startUtc, out var error) ||
                !TryParseUtcInput(EndInput, "End", out var endUtc, out error))
            {
                ErrorMessage = error;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }

            try
            {
                var plan = reruns.CreatePlan(new RerunPlanRequest(
                    jobId,
                    startUtc!.Value,
                    endUtc!.Value,
                    string.IsNullOrWhiteSpace(RequestedBy) ? "local-web" : RequestedBy.Trim(),
                    string.IsNullOrWhiteSpace(Reason) ? "Manual rerun" : Reason.Trim()));
                return Redirect($"/reruns/{Uri.EscapeDataString(plan.RerunBatchId)}");
            }
            catch (InvalidOperationException ex)
            {
                ErrorMessage = ex.Message;
                Response.StatusCode = StatusCodes.Status400BadRequest;
                return Page();
            }
        }

        private static bool TryParseUtcInput(string? value, string fieldName, out DateTimeOffset? parsed, out string? error)
        {
            parsed = null;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                error = $"{fieldName} is required.";
                return false;
            }

            var trimmed = value.Trim();
            if (DateTime.TryParseExact(trimmed, DateTimeLocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localUtc))
            {
                parsed = new DateTimeOffset(DateTime.SpecifyKind(localUtc, DateTimeKind.Utc));
                return true;
            }

            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var offset))
            {
                parsed = offset.ToUniversalTime();
                return true;
            }

            error = $"{fieldName} must be a valid UTC date/time.";
            return false;
        }
    }
}
