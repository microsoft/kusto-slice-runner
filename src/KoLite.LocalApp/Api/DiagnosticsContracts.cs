using System.Globalization;
using KoLite.Local.Sqlite.Catalog;

namespace KoLite.LocalApp.Api
{
    // Stable wire shapes for the read-only diagnostics API. The flat read-model records returned by
    // SqliteDiagnosticsReadModelRepository and the operational read models are serialized directly as
    // list items (they are already purpose-built, in-our-control DTOs); the composite shapes below are
    // declared here so the per-job status and dependency payloads have an explicit contract.
    public sealed record JobRefDto(string JobId, string ActivityId, string DisplayName)
    {
        public static JobRefDto From(JobCatalogRecord record) =>
            new(record.JobId, record.Definition.ActivityId, record.DisplayName);
    }

    public sealed record JobSliceStateCountsDto(
        int Missing,
        int Queued,
        int Running,
        int Completed,
        int Failed,
        int DeadLettered,
        int DependencyBlocked,
        DateTimeOffset? LastUpdatedAtUtc);

    public sealed record JobQueueCountsDto(int Queued, int Leased, int Total);

    public sealed record JobDiagnosticsStatusDto(
        JobRefDto Job,
        bool IsEnabled,
        bool IsPaused,
        bool HasStarted,
        int MaxParallelism,
        long CatalogVersion,
        JobTargetDto Target,
        JobSliceStateCountsDto SliceStates,
        JobQueueCountsDto Queue);

    public sealed record DependencyRefDto(string? UpstreamId, string? ActivityId, string? DisplayName, bool Exists);

    public sealed record MissingUpstreamSliceDto(string Reference, string? ActivityId, DateTimeOffset StartUtc, DateTimeOffset EndUtc);

    public sealed record BlockedSliceDto(
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        bool IsReady,
        IReadOnlyList<MissingUpstreamSliceDto> Missing);

    public sealed record JobDependenciesDto(
        JobRefDto Job,
        IReadOnlyList<DependencyRefDto> Dependencies,
        int BlockedSliceCount,
        IReadOnlyList<BlockedSliceDto> BlockedSamples);

    // Shared, bounded query-string parsing for the diagnostics API. Every list/series route is
    // capped (take clamped to [1, max]) and time-windowed (defaults to a recent lookback) so a single
    // diagnostic call never scans the whole local store.
    internal static class DiagnosticsQuery
    {
        public const int DefaultTake = 100;
        public const int MaxTake = 1000;

        public static int Take(HttpRequest request, int defaultTake = DefaultTake, int maxTake = MaxTake)
        {
            if (request.Query.TryGetValue("take", out var raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return Math.Clamp(value, 1, maxTake);
            }

            return Math.Clamp(defaultTake, 1, maxTake);
        }

        public static DateTimeOffset? Instant(HttpRequest request, string name)
        {
            if (request.Query.TryGetValue(name, out var raw) &&
                DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            {
                return value.ToUniversalTime();
            }

            return null;
        }

        public static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) Window(HttpRequest request, DateTimeOffset nowUtc, TimeSpan defaultLookback)
        {
            var to = Instant(request, "to") ?? nowUtc;
            var from = Instant(request, "from") ?? to - defaultLookback;
            if (from > to)
            {
                from = to - defaultLookback;
            }

            return (from, to);
        }

        public static string? Text(HttpRequest request, string name) =>
            request.Query.TryGetValue(name, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.ToString() : null;

        public static bool GroupByJob(HttpRequest request) =>
            string.Equals(Text(request, "groupBy"), "job", StringComparison.OrdinalIgnoreCase);

        // Parse a bucket size like "5m", "30m", "1h", "90s", or a plain integer (seconds). Clamped to a
        // sane band so a tiny bucket over a wide window cannot explode the row count.
        public static int BucketSeconds(HttpRequest request, int defaultSeconds, int minSeconds = 60, int maxSeconds = 86_400)
        {
            var raw = Text(request, "bucket");
            var seconds = defaultSeconds;
            if (raw is not null)
            {
                var trimmed = raw.Trim();
                var unit = trimmed[^1];
                if (unit is 's' or 'm' or 'h' or 'd' &&
                    int.TryParse(trimmed[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    seconds = unit switch
                    {
                        's' => n,
                        'm' => n * 60,
                        'h' => n * 3600,
                        'd' => n * 86_400,
                        _ => defaultSeconds,
                    };
                }
                else if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain))
                {
                    seconds = plain;
                }
            }

            return Math.Clamp(seconds, minSeconds, maxSeconds);
        }
    }
}
