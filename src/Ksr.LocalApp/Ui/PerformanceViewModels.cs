// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Schedules;

namespace Ksr.LocalApp.Ui
{
    public static class PerformanceRangeOptions
    {
        public static IReadOnlyList<ChartRangeLink> Links { get; } =
        [
            new("1h", "1 hour"),
            new("1d", "24 hours"),
            new("7d", "7 days"),
            new("30d", "30 days")
        ];

        public static string Normalize(string? range) => range is "1h" or "1d" or "7d" or "30d" ? range : "7d";

        public static TimeSpan Parse(string? range) => ChartRangeOptions.Parse(Normalize(range));
    }

    public sealed record PerformanceMetricDefinition(string Key, string Label, string Unit)
    {
        public PerformancePercentiles Samples(PerformanceAggregateRow row) => Key switch
        {
            "cpu" => row.CpuSeconds,
            "duration" => row.DurationSeconds,
            "memory" => row.MemoryGiB,
            _ => throw new InvalidOperationException($"Unknown performance metric '{Key}'.")
        };

        public double? Value(PerformanceAggregateRow row, string percentile) => percentile switch
        {
            "p50" => Samples(row).P50,
            "p90" => Samples(row).P90,
            "p95" => Samples(row).P95,
            _ => throw new InvalidOperationException($"Unknown performance percentile '{percentile}'.")
        };
    }

    public static class PerformanceMetrics
    {
        public static IReadOnlyList<PerformanceMetricDefinition> All { get; } =
        [
            new("cpu", "CPU", "s"),
            new("duration", "Duration", "s"),
            new("memory", "Memory peak", "GiB")
        ];

        public static IReadOnlyList<string> Percentiles { get; } = ["p50", "p90", "p95"];
    }

    public sealed record PerformanceSort(string Key, bool Descending)
    {
        public static PerformanceSort Default { get; } = new("activity", false);

        private static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(
            new[] { "activity", "attempts", "success" }
                .Concat(PerformanceMetrics.All.SelectMany(metric => PerformanceMetrics.Percentiles.Select(percentile => metric.Key + "-" + percentile))),
            StringComparer.Ordinal);

        public string Direction => Descending ? "desc" : "asc";

        public static PerformanceSort Parse(string? key, string? direction) =>
            new(key is not null && AllowedKeys.Contains(key) ? key : "activity",
                string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase));
    }

    public sealed record PerformanceViewState(
        string Range,
        string Search,
        IReadOnlyList<string> Tags,
        string? JobId,
        PerformanceSort Sort)
    {
        public bool HasFilters => Search.Length > 0 || Tags.Count > 0 || JobId is not null;

        public string Href
        {
            get
            {
                var query = new List<string> { "view=performance", "range=" + Uri.EscapeDataString(Range) };
                if (Search.Length > 0) query.Add("q=" + Uri.EscapeDataString(Search));
                query.AddRange(Tags.Select(tag => "tag=" + Uri.EscapeDataString(tag)));
                if (JobId is not null) query.Add("jobId=" + Uri.EscapeDataString(JobId));
                if (Sort != PerformanceSort.Default)
                {
                    query.Add("sort=" + Uri.EscapeDataString(Sort.Key));
                    query.Add("dir=" + Sort.Direction);
                }

                return "/activity?" + string.Join("&", query);
            }
        }

        public string RangeHref(string range) => (this with { Range = PerformanceRangeOptions.Normalize(range) }).Href;

        public string SortHref(string key) =>
            (this with { Sort = new PerformanceSort(key, Sort.Key != key || !Sort.Descending) }).Href;

        public string SortAria(string key) => Sort.Key == key ? (Sort.Descending ? "descending" : "ascending") : "none";

        public string SortIndicator(string key) => Sort.Key == key ? (Sort.Descending ? "\u25be" : "\u25b4") : string.Empty;

        public string ToggleTagHref(string tag)
        {
            var next = Tags.Contains(tag, StringComparer.Ordinal)
                ? Tags.Where(selected => !string.Equals(selected, tag, StringComparison.Ordinal))
                : Tags.Concat([tag]);
            return (this with { Tags = ScheduleTags.NormalizeDistinct(next) }).Href;
        }

        public string ClearFiltersHref => (this with { Search = string.Empty, Tags = [], JobId = null }).Href;
    }

    public sealed record PerformanceJobGroup(
        string JobId,
        string ActivityId,
        int? ConfiguredChunks,
        PerformanceAggregateRow Total,
        IReadOnlyList<PerformanceAggregateRow> Chunks)
    {
        public bool MatchesSearch(string search) => ActivityId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record PerformancePageData(
        PerformanceViewState State,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc,
        IReadOnlyList<PerformanceJobGroup> Jobs,
        IReadOnlyList<JobTagSummary> AvailableTags,
        string? SelectedJobLabel,
        PerformanceCollectionReadout Collection,
        bool ExecutionEnabled,
        string? FilterError)
    {
        public int VisibleJobCount => Jobs.Count(job => job.MatchesSearch(State.Search));
        public PerformanceCoverageCounts VisibleCoverage => PerformanceCoverageCounts.Sum(
            Jobs.Where(job => job.MatchesSearch(State.Search)).Select(job => job.Total.Coverage));
    }

    public static class PerformanceFormatting
    {
        public static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        public static string Value(double? value, string unit = "s") =>
            value?.ToString(unit == "GiB" ? "N3" : "N2", CultureInfo.InvariantCulture) ?? "n/a";

        public static string Timestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

        public static string DisplayTimestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        public static string Success(PerformanceAggregateRow row) =>
            row.SuccessPercent is { } percent ? percent.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "n/a";

        public static string MetricTooltip(PerformanceAggregateRow row, PerformanceMetricDefinition metric, string percentile)
        {
            var value = metric.Value(row, percentile);
            var measurement = value is { } number
                ? number.ToString("R", CultureInfo.InvariantCulture) + " " + metric.Unit
                : "unavailable";
            var samples = metric.Samples(row).SampleCount;
            return $"{metric.Label} {percentile.ToUpperInvariant()}: {measurement}; {Count(samples)} {(samples == 1 ? "sample" : "samples")} from {Count(row.SucceededAttempts)} successful {(row.SucceededAttempts == 1 ? "attempt" : "attempts")}.";
        }

        public static string CoverageMessage(PerformanceCoverageCounts coverage)
        {
            var percent = coverage.MissingPercent is { } value
                ? decimal.Round(value, 1, MidpointRounding.AwayFromZero).ToString("0.#", CultureInfo.InvariantCulture) + "%"
                : "n/a";
            return $"{Count(coverage.MissingAttempts)} of {Count(coverage.EligibleAttempts)} eligible successful attempts ({percent}) in the selected period and filters are missing one or more resource measurements.";
        }
    }
}
