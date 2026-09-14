using KoLite.Local.Core.Performance;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;

namespace KoLite.LocalApp.Ui
{
    public sealed class PerformancePageQuery
    {
        private readonly IPerformanceReportRepository repository;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly LifecycleReadModel lifecycle;
        private readonly IClock clock;
        private readonly LocalBackgroundSchedulerOptions executionOptions;

        public PerformancePageQuery(
            IPerformanceReportRepository repository,
            SqliteJobCatalogRepository catalog,
            LifecycleReadModel lifecycle,
            IClock clock,
            LocalBackgroundSchedulerOptions executionOptions)
        {
            this.repository = repository;
            this.catalog = catalog;
            this.lifecycle = lifecycle;
            this.clock = clock;
            this.executionOptions = executionOptions;
        }

        public PerformancePageData Get(
            string? range = null,
            string? search = null,
            IEnumerable<string>? tags = null,
            string? jobId = null,
            string? sort = null,
            string? direction = null)
        {
            var until = clock.UtcNow.ToUniversalTime();
            var rangeKey = PerformanceRangeOptions.Normalize(range);
            var since = until - PerformanceRangeOptions.Parse(rangeKey);
            var selectedTags = ScheduleTags.NormalizeDistinct(tags ?? []);
            var selectedJobId = string.IsNullOrWhiteSpace(jobId) ? null : jobId.Trim();
            string? filterError = null;
            if (selectedJobId is not null)
            {
                if (Guid.TryParse(selectedJobId, out var parsedId))
                {
                    selectedJobId = parsedId.ToString("N");
                }
                else
                {
                    filterError = "The job filter must be a valid job ID.";
                }
            }

            var state = new PerformanceViewState(
                rangeKey,
                search?.Trim() ?? string.Empty,
                selectedTags,
                selectedJobId,
                PerformanceSort.Parse(sort, direction));
            var lifecycleStates = lifecycle.GetLatestStates();
            var jobs = catalog.List()
                .Where(job => !lifecycleStates.TryGetValue(job.JobId, out var latest) || !latest.IsSoftDeleted)
                .Select(job => (Record: job, Definition: job.Definition))
                .ToArray();
            var tagCounts = jobs.SelectMany(job => job.Definition.Tags)
                .GroupBy(tag => tag, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var availableTags = tagCounts.Keys.Concat(selectedTags)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .Select(tag => new JobTagSummary(tag, tagCounts.GetValueOrDefault(tag), selectedTags.Contains(tag, StringComparer.Ordinal)))
                .ToArray();
            var selectedJobLabel = jobs.FirstOrDefault(job => job.Record.JobId == selectedJobId).Record?.ActivityId;
            var includedJobs = jobs.Where(job =>
                    (selectedJobId is null || job.Record.JobId == selectedJobId)
                    && selectedTags.All(tag => job.Definition.Tags.Contains(tag, StringComparer.Ordinal)))
                .ToArray();
            var collection = repository.GetCollectionStatus();
            var groups = new List<PerformanceJobGroup>();

            if (collection.HistoryInitialized && filterError is null && includedJobs.Length > 0)
            {
                // Keep groups available when the local text filter is cleared, without refetching
                // data or losing the expansion state of hidden chunks.
                var rows = repository.GetAggregates(
                    since, until, includedJobs.Select(job => job.Record.JobId).ToArray(),
                    coverageCutoffUtc: until - PerformanceCoveragePolicy.GracePeriod);
                var rowsByJob = rows.ToLookup(row => row.JobId, StringComparer.Ordinal);
                foreach (var job in includedJobs)
                {
                    var jobRows = rowsByJob[job.Record.JobId];
                    var total = jobRows.SingleOrDefault(row => row.IsJobTotal);
                    if (total is null || total.CompletedAttempts == 0)
                    {
                        continue;
                    }

                    var children = new List<PerformanceAggregateRow>();
                    if (job.Definition.Chunks is { } count)
                    {
                        var byChunk = jobRows.Where(row => !row.IsJobTotal && row.ChunkId is not null)
                            .ToDictionary(row => row.ChunkId!.Value);
                        if (byChunk.Keys.Any(chunkId => chunkId < 0 || chunkId >= count))
                        {
                            throw new InvalidOperationException($"Stored performance chunk IDs do not match the configured chunks for job '{job.Record.ActivityId}'.");
                        }

                        for (var chunkId = 0; chunkId < count; chunkId++)
                        {
                            children.Add(byChunk.GetValueOrDefault(chunkId) ?? new PerformanceAggregateRow(
                                job.Record.JobId, chunkId, false, 0, 0,
                                PerformancePercentiles.Empty, PerformancePercentiles.Empty, PerformancePercentiles.Empty));
                        }
                    }

                    groups.Add(new PerformanceJobGroup(
                        job.Record.JobId, job.Record.ActivityId, job.Definition.Chunks, total, children));
                }
            }

            return new PerformancePageData(
                state, since, until, ApplySort(groups, state.Sort), availableTags, selectedJobLabel,
                collection, executionOptions.Enabled, filterError);
        }

        private static IReadOnlyList<PerformanceJobGroup> ApplySort(IEnumerable<PerformanceJobGroup> jobs, PerformanceSort sort)
        {
            IOrderedEnumerable<PerformanceJobGroup> ordered;
            if (sort.Key == "activity")
            {
                ordered = sort.Descending
                    ? jobs.OrderByDescending(job => job.ActivityId, StringComparer.OrdinalIgnoreCase)
                    : jobs.OrderBy(job => job.ActivityId, StringComparer.OrdinalIgnoreCase);
            }
            else if (sort.Key == "attempts")
            {
                ordered = sort.Descending
                    ? jobs.OrderByDescending(job => job.Total.CompletedAttempts)
                    : jobs.OrderBy(job => job.Total.CompletedAttempts);
            }
            else
            {
                Func<PerformanceJobGroup, double?> value;
                if (sort.Key == "success")
                {
                    value = job => job.Total.SuccessPercent;
                }
                else
                {
                    var parts = sort.Key.Split('-', 2);
                    var metric = PerformanceMetrics.All.Single(metric => metric.Key == parts[0]);
                    value = job => metric.Value(job.Total, parts[1]);
                }

                var presentFirst = jobs.OrderBy(job => value(job) is null);
                ordered = sort.Descending ? presentFirst.ThenByDescending(value) : presentFirst.ThenBy(value);
            }

            return ordered.ThenBy(job => job.ActivityId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(job => job.JobId, StringComparer.Ordinal)
                .ToArray();
        }
    }
}
