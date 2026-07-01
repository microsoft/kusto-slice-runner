using System.Globalization;
using System.Net;
using System.Text.Json;

namespace KoLite.LocalApp.Ui
{
    public static class AppFormatting
    {
        private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public static string PrettyJson(string json)
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
        }

        public static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

        public static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        public static string DateTimeInputUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

        public static string Local(DateTimeOffset? value) => value is null ? "-" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);

        public static string Duration(TimeSpan value) => value.ToString("c", CultureInfo.InvariantCulture);

        // Coarse, human-friendly approximation for projected/elapsed spans (e.g. "~45 min",
        // "~3.2 h", "~3.2 days"). Used by the catch-up estimate where exact precision is noise.
        public static string ApproxDuration(TimeSpan value)
        {
            if (value < TimeSpan.Zero)
            {
                value = TimeSpan.Zero;
            }

            var totalMinutes = value.TotalMinutes;
            if (totalMinutes < 1)
            {
                return "< 1 min";
            }

            if (totalMinutes < 90)
            {
                return $"~{Math.Round(totalMinutes).ToString("0", CultureInfo.InvariantCulture)} min";
            }

            if (value.TotalHours < 36)
            {
                return $"~{value.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
            }

            return $"~{value.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} days";
        }

        public static string Percent(int numerator, int denominator) => denominator <= 0 ? "n/a" : ((double)numerator / denominator).ToString("P1", CultureInfo.InvariantCulture);

        public static string StateCss(string state) => state switch
        {
            "Completed" => "completed",
            "CompletedAfterRetry" => "completed-after-retry",
            "Succeeded" => "completed",
            "Failed" => "failed",
            "DeadLettered" => "failed",
            "DependencyBlocked" => "waiting",
            "WaitingToSchedule" => "waiting-scheduled",
            "Running" => "running",
            "Queued" => "queued",
            "Leased" => "running",
            "Stalled" => "stalled",
            "Missing" => "missing",
            "NotEligible" => "missing",
            "NotYetEligible" => "missing",
            _ => "missing"
        };

        public static string StatusLabel(string state) => state switch
        {
            "Completed" => "Completed",
            "CompletedAfterRetry" => "Completed after retry",
            "Failed" => "Failed",
            "DeadLettered" => "Dead-lettered",
            "DependencyBlocked" => "Waiting on dependency",
            "WaitingToSchedule" => "Waiting to be scheduled",
            "Running" => "Running",
            "Queued" => "Queued",
            "Leased" => "Leased",
            "Stalled" => "Stalled (orphaned lease)",
            "Missing" => "Missing",
            "NotEligible" => "Not eligible",
            "NotYetEligible" => "Not yet eligible",
            _ => state
        };

        public static string BadgeCss(string state) => state switch
        {
            "Healthy" => "badge-success",
            "Paused" => "badge-warning",
            "Completed" => "badge-neutral",
            "SoftDeleted" => "badge-danger",
            "Failed" => "badge-danger",
            "DeadLettered" => "badge-danger",
            "DependencyBlocked" => "badge-warning",
            "WaitingOnUpstream" => "badge-success",
            "Running" => "badge-info",
            "Queued" => "badge-info",
            _ => "badge-neutral"
        };

    }
}
