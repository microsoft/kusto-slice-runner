using System.Globalization;
using System.Text.RegularExpressions;

namespace KoLite.Local.Core.Throttling
{
    // Result of inspecting a failed slice's error for a Kusto ingestion-capacity throttle.
    // Only ingestion-capacity throttling (the CapacityPolicy/Ingestion origin that governs
    // .set-or-append / TableSetOrAppend) is actionable by reducing per-job parallelism; other
    // 429s (query/export capacity, or a workload group's request-rate-limit policy) are not and
    // must classify as IsIngestionCapacityThrottle = false.
    public sealed record IngestionThrottleClassification(bool IsIngestionCapacityThrottle, int? ReportedCapacity)
    {
        public static IngestionThrottleClassification NotThrottled { get; } = new(false, null);
    }

    public static partial class IngestionThrottleClassifier
    {
        // The authoritative, specific marker for ingestion-capacity throttling. Kusto reports it as
        // Origin: 'CapacityPolicy/Ingestion' in the error message (and again in the embedded @message
        // JSON). Matching this substring distinguishes ingestion throttling from every other 429.
        private const string IngestionOriginMarker = "CapacityPolicy/Ingestion";

        public static IngestionThrottleClassification Classify(string? errorCode, string? errorMessage)
        {
            // The origin marker can appear in either field depending on how the error surfaced, so
            // both are inspected. The code alone (e.g. KustoRequestThrottledException) is not enough:
            // it is shared by non-ingestion throttles.
            var haystack = string.Concat(errorCode ?? string.Empty, "\n", errorMessage ?? string.Empty);
            if (!haystack.Contains(IngestionOriginMarker, StringComparison.OrdinalIgnoreCase))
            {
                return IngestionThrottleClassification.NotThrottled;
            }

            return new IngestionThrottleClassification(true, ParseReportedCapacity(haystack));
        }

        // Extracts the capacity Kusto reported (e.g. "Capacity: 18") so the operator can see the
        // current cluster-wide ingestion concurrency limit. Best-effort: null when not present.
        private static int? ParseReportedCapacity(string text)
        {
            var match = CapacityRegex().Match(text);
            return match.Success
                && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity)
                ? capacity
                : null;
        }

        [GeneratedRegex("Capacity[\"']?\\s*[:=]\\s*[\"']?\\s*(\\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
        private static partial Regex CapacityRegex();
    }
}
