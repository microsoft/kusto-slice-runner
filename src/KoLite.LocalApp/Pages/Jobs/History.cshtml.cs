using System.Globalization;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace KoLite.LocalApp.Pages.Jobs
{
    public sealed class HistoryModel : PageModel
    {
        private readonly JobDetailsPageQuery query;

        public HistoryModel(JobDetailsPageQuery query)
        {
            this.query = query;
        }

        private static readonly string[] DateTimeLocalFormats =
        [
            "yyyy-MM-ddTHH:mm",
            "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss.FFFFFFF"
        ];

        public JobDetailsPageData? Data { get; private set; }
        public string FromInput { get; private set; } = string.Empty;
        public string ToInput { get; private set; } = string.Empty;
        public string? RangeError { get; private set; }

        public IActionResult OnGet(string jobId, string? from, string? to)
        {
            FromInput = from?.Trim() ?? string.Empty;
            ToInput = to?.Trim() ?? string.Empty;

            if (!TryParseUtcInput(from, "Start", out var fromUtc, out var rangeError) ||
                !TryParseUtcInput(to, "End", out var toUtc, out rangeError))
            {
                RangeError = rangeError;
                Data = query.Get(jobId, sliceHistoryCellLimit: JobDetailsPageQuery.FullSliceHistoryCellLimit);
                Response.StatusCode = Data is null ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                return Page();
            }

            if (fromUtc is { } start && toUtc is { } end && end <= start)
            {
                RangeError = "End must be after start.";
                Data = query.Get(jobId, sliceHistoryCellLimit: JobDetailsPageQuery.FullSliceHistoryCellLimit);
                Response.StatusCode = Data is null ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest;
                return Page();
            }

            FromInput = fromUtc is { } parsedFrom ? AppFormatting.DateTimeInputUtc(parsedFrom) : string.Empty;
            ToInput = toUtc is { } parsedTo ? AppFormatting.DateTimeInputUtc(parsedTo) : string.Empty;

            Data = query.Get(jobId, fromUtc, toUtc, JobDetailsPageQuery.FullSliceHistoryCellLimit);
            if (Data is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
            }

            return Page();
        }

        private static bool TryParseUtcInput(string? value, string fieldName, out DateTimeOffset? parsed, out string? error)
        {
            parsed = null;
            error = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
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
