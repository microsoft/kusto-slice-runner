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

        public static string Iso(DateTimeOffset? value) => value is null ? "-" : Iso(value.Value);

        public static string DateTimeInputUtc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

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

        // Coarse, human-friendly relative phrasing of a target time against now: "in ~10 min",
        // "~5 min ago", or "just now" when within a minute. Magnitude reuses ApproxDuration, so a
        // past ETA (a slice running longer than usual) simply reads as "~X ago".
        public static string RelativeToNow(DateTimeOffset target, DateTimeOffset now)
        {
            var delta = target - now;
            var magnitude = delta < TimeSpan.Zero ? -delta : delta;
            if (magnitude < TimeSpan.FromMinutes(1))
            {
                return "just now";
            }

            return delta < TimeSpan.Zero
                ? $"{ApproxDuration(magnitude)} ago"
                : $"in {ApproxDuration(magnitude)}";
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

        // Human label for a job's resolved primary (segment-1) status on the dashboard split pill.
        public static string PrimaryStatusLabel(string primaryState) => primaryState switch
        {
            "Attention" => "Attention",
            "Borderline" => "Warning",
            "Healthy" => "Healthy",
            "Paused" => "Paused",
            "Completed" => "Completed",
            "SoftDeleted" => "Soft deleted",
            "DependencyBlocked" => "Blocked (upstream)",
            "WaitingOnUpstream" => "Waiting on upstream",
            _ => primaryState
        };

        // Lowercased key used to build the segment CSS class (.status-seg.status-<key>) for the
        // split pill and, via the dependency graph, the node colors. Kept in sync with the
        // STATUS_COLORS map in site.js and the .status-<key> rules in site.css.
        public static string PrimaryStatusKey(string primaryState) => primaryState.ToLowerInvariant();

        // Badge-family class for the resolved primary status. Used where a single flat badge is
        // rendered (dependency graph legend, inline-toggle fallback) rather than the split pill.
        public static string PrimaryStatusBadgeCss(string primaryState) => primaryState switch
        {
            "Attention" => "badge-danger",
            "Borderline" => "badge-warning",
            "Healthy" => "badge-success",
            "Paused" => "badge-neutral",
            "Completed" => "badge-neutral",
            "SoftDeleted" => "badge-neutral",
            "DependencyBlocked" => "badge-warning",
            "WaitingOnUpstream" => "badge-success",
            _ => "badge-neutral"
        };

        // Label for the completeness (segment-2) chip: a subtle "Complete" when there are no
        // unaddressed gaps, otherwise a capped "N gaps" count.
        public static string CompletenessLabel(int gapCount) => gapCount switch
        {
            <= 0 => "Complete",
            1 => "1 gap",
            > 999 => "999+ gaps",
            _ => $"{gapCount} gaps"
        };

        // CSS class for the completeness chip.
        public static string CompletenessCss(int gapCount) => gapCount > 0 ? "status-gaps" : "status-complete";

        // Quotes a Kusto entity name using the bracket/quoted-string form (['name']) so names that
        // contain dots, spaces, or reserved words still resolve. Embedded single quotes and
        // backslashes are escaped per Kusto quoted-string rules.
        public static string KustoQuoteName(string name)
        {
            var escaped = (name ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);
            return $"['{escaped}']";
        }

        // Builds a Kusto.WebExplorer (Azure Data Explorer web UI) deep link that preselects the
        // cluster/database and prefills the given query or management command. Short text only needs
        // standard URI query encoding; base64+gzip is only required for very long queries. Regular
        // queries auto-run; management commands (starting with '.') are prefilled but require the
        // user to run them, which is a Kusto.WebExplorer security behavior.
        public static string KustoWebExplorerLink(string clusterUri, string database, string query)
        {
            var host = Uri.TryCreate(clusterUri, UriKind.Absolute, out var uri) ? uri.Host : clusterUri;
            return $"https://dataexplorer.azure.com/clusters/{Uri.EscapeDataString(host)}/databases/{Uri.EscapeDataString(database)}?query={Uri.EscapeDataString(query)}";
        }

        // Deep link that shows the definition of the job's Kusto function.
        public static string KustoShowFunctionLink(string clusterUri, string database, string functionName) =>
            KustoWebExplorerLink(clusterUri, database, $".show function {KustoQuoteName(functionName)}");

        // Deep link that previews the latest rows of the job's output table.
        public static string KustoTablePreviewLink(string clusterUri, string database, string outputTable) =>
            KustoWebExplorerLink(clusterUri, database, $"{KustoQuoteName(outputTable)} | take 10");

    }
}
