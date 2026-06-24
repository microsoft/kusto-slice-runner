using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp.Ui
{
    public sealed record DashboardPageData(
        IReadOnlyList<JobListItem> ActiveJobs,
        IReadOnlyList<JobListItem> CompletedJobs,
        IReadOnlyList<JobListItem> SoftDeletedJobs,
        IReadOnlyList<JobTagSummary> AvailableTags,
        IReadOnlyList<string> SelectedTags,
        DashboardCharts Charts,
        IReadOnlyList<RecentFailure> RecentFailures,
        TimeSpan SelectedRange,
        DashboardSort Sort);

    // Dashboard ordering. Default is activityId ascending; the user may sort by status, activity,
    // schedule (query window size), or next-eligible time.
    public sealed record DashboardSort(string Key, bool Descending)
    {
        public static readonly DashboardSort Default = new("activity", false);

        private static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "activity", "status", "schedule", "next"
        };

        public static DashboardSort Parse(string? key, string? direction)
        {
            var normalizedKey = key is not null && AllowedKeys.Contains(key) ? key : "activity";
            var descending = string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase);
            return new DashboardSort(normalizedKey, descending);
        }

        public string Direction => Descending ? "desc" : "asc";

        public bool IsDefault => string.Equals(Key, Default.Key, StringComparison.Ordinal) && Descending == Default.Descending;
    }

    public sealed record JobTagSummary(string Name, int JobCount, bool IsSelected);

    public sealed record JobTableViewModel(
        string Title,
        IReadOnlyList<JobListItem> Jobs,
        bool SoftDeletedSection,
        bool ShowSoftDeleteAction = true,
        string? SectionCssClass = null,
        bool ShowDefinitionColumns = true,
        bool ShowUpdatedColumn = true,
        bool ShowNextEligibleColumn = false,
        bool ShowRunningProgressCount = true,
        bool ShowProgressColumn = true,
        bool ShowHistoryAction = true,
        bool ShowFolderLine = true,
        bool EnableClientFilter = false,
        bool EnableColumnResize = false,
        bool EnableInlineToggle = false,
        bool EnableBulkSelect = false,
        bool EnableSort = false,
        DashboardSort? Sort = null,
        string SortBaseQuery = "",
        string? TableKey = null);

    public sealed record JobListItem(
        JobCatalogRecord Record,
        JobDefinition Definition,
        JobStatusSummary? Summary,
        JobLifecycleProjection? Lifecycle,
        string LifecycleStatus,
        string StatusText,
        string StatusCss,
        NextSliceTiming NextSlice,
        bool IsCompleted)
    {
        public int TotalSlices => Summary is null
            ? 0
            : Summary.MissingCount + Summary.QueuedCount + Summary.RunningCount + Summary.CompletedCount + Summary.FailedCount + Summary.DeadLetteredCount + Summary.DependencyBlockedCount;

        public int FailedLikeSlices => Summary is null ? 0 : Summary.FailedCount + Summary.DeadLetteredCount + Summary.DependencyBlockedCount;
    }

    public sealed record NextSliceTiming(DateTimeOffset? EligibleAtUtc, string Text, string? Detail);

    public sealed class DashboardPageQuery
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly LifecycleReadModel lifecycleReadModel;
        private readonly JobChartQuery chartQuery;
        private readonly IClock clock;

        public DashboardPageQuery(
            SqliteJobCatalogRepository catalog,
            SqliteOperationalReadModelRepository readModels,
            LifecycleReadModel lifecycleReadModel,
            JobChartQuery chartQuery,
            IClock clock)
        {
            this.catalog = catalog;
            this.readModels = readModels;
            this.lifecycleReadModel = lifecycleReadModel;
            this.chartQuery = chartQuery;
            this.clock = clock;
        }

        public DashboardPageData Get(TimeSpan selectedRange, IEnumerable<string>? selectedTags = null, DashboardSort? sort = null)
        {
            var effectiveSort = sort ?? DashboardSort.Default;
            var normalizedSelectedTags = ScheduleTags.NormalizeDistinct(selectedTags ?? Array.Empty<string>());
            var jobs = GetAllJobs();
            var tagSummaries = BuildTagSummaries(jobs, normalizedSelectedTags);
            var filteredJobs = normalizedSelectedTags.Count == 0
                ? jobs
                : jobs.Where(job => MatchesSelectedTags(job, normalizedSelectedTags)).ToList();
            var activeJobs = ApplySort(filteredJobs.Where(j => j.LifecycleStatus != "SoftDeleted" && !j.IsCompleted), effectiveSort);
            var completedJobs = ApplySort(filteredJobs.Where(j => j.LifecycleStatus != "SoftDeleted" && j.IsCompleted), effectiveSort);
            var softDeletedJobs = ApplySort(filteredJobs.Where(j => j.LifecycleStatus == "SoftDeleted"), effectiveSort);
            var chartJobIds = activeJobs.Select(job => job.Record.JobId).ToArray();

            return new DashboardPageData(
                activeJobs,
                completedJobs,
                softDeletedJobs,
                tagSummaries,
                normalizedSelectedTags,
                chartQuery.GetDashboardCharts(selectedRange, chartJobIds),
                readModels.GetRecentFailures(10),
                selectedRange,
                effectiveSort);
        }

        // Every job in the catalog projected to a JobListItem (status, summary, lifecycle, next
        // slice) in one batched read pass. Shared by the dashboard and the dependency graph so the
        // exact same status logic backs both.
        public IReadOnlyList<JobListItem> GetAllJobs()
        {
            var now = clock.UtcNow;
            var lifecycleStates = lifecycleReadModel.GetLatestStates();
            var summaries = readModels.GetJobStatusSummaries().ToDictionary(s => s.JobId, StringComparer.Ordinal);
            var queuedAvailability = readModels.GetQueuedAvailabilityByJob();
            var latestSliceEnds = readModels.GetLatestSliceEndsByJob();
            return catalog.List()
                .Select(record => BuildJobListItem(record, lifecycleStates, summaries, queuedAvailability, latestSliceEnds, now))
                .ToList();
        }

        private static IReadOnlyList<JobListItem> ApplySort(IEnumerable<JobListItem> jobs, DashboardSort sort)
        {
            var ordered = sort.Key switch
            {
                "status" => sort.Descending
                    ? jobs.OrderByDescending(j => j.StatusText, StringComparer.OrdinalIgnoreCase)
                    : jobs.OrderBy(j => j.StatusText, StringComparer.OrdinalIgnoreCase),
                "schedule" => sort.Descending
                    ? jobs.OrderByDescending(j => j.Definition.QueryWindowSize)
                    : jobs.OrderBy(j => j.Definition.QueryWindowSize),
                "next" => sort.Descending
                    ? jobs.OrderByDescending(j => j.NextSlice.EligibleAtUtc ?? DateTimeOffset.MaxValue)
                    : jobs.OrderBy(j => j.NextSlice.EligibleAtUtc ?? DateTimeOffset.MaxValue),
                _ => sort.Descending
                    ? jobs.OrderByDescending(j => j.Record.ActivityId, StringComparer.OrdinalIgnoreCase)
                    : jobs.OrderBy(j => j.Record.ActivityId, StringComparer.OrdinalIgnoreCase),
            };

            // Deterministic tie-break so equal sort keys keep a stable, human-friendly order.
            return ordered.ThenBy(j => j.Record.ActivityId, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public JobListItem? GetJob(string jobId)
        {
            var record = catalog.Get(jobId);
            if (record is null)
            {
                return null;
            }

            var now = clock.UtcNow;
            var lifecycleStates = lifecycleReadModel.GetLatestStates();
            var summaries = readModels.GetJobStatusSummaries().ToDictionary(s => s.JobId, StringComparer.Ordinal);
            var queuedAvailability = readModels.GetQueuedAvailabilityByJob();
            var latestSliceEnds = readModels.GetLatestSliceEndsByJob();
            return BuildJobListItem(record, lifecycleStates, summaries, queuedAvailability, latestSliceEnds, now);
        }

        private static JobListItem BuildJobListItem(
            JobCatalogRecord record,
            IReadOnlyDictionary<string, JobLifecycleProjection> lifecycleStates,
            IReadOnlyDictionary<string, JobStatusSummary> summaries,
            IReadOnlyDictionary<string, DateTimeOffset> queuedAvailability,
            IReadOnlyDictionary<string, DateTimeOffset> latestSliceEnds,
            DateTimeOffset now)
        {
            var definition = record.Definition;
            lifecycleStates.TryGetValue(record.JobId, out var lifecycle);
            summaries.TryGetValue(record.JobId, out var summary);
            var completed = IsCompletedSchedule(definition, summary, now);
            var status = lifecycle?.IsSoftDeleted == true
                ? "SoftDeleted"
                : !record.IsEnabled
                    ? "Paused"
                    : summary is { FailedCount: > 0 } or { DeadLetteredCount: > 0 }
                        ? "Failed"
                        : summary is { DependencyBlockedCount: > 0 }
                            ? "DependencyBlocked"
                            : summary is { QueuedCount: > 0 } or { RunningCount: > 0 }
                                ? "Running"
                                : completed
                                    ? "Completed"
                                    : "Healthy";

            return new JobListItem(
                record,
                definition,
                summary,
                lifecycle,
                status,
                status switch
                {
                    "SoftDeleted" => "Soft deleted",
                    "DependencyBlocked" => "Dependency blocked",
                    _ => status
                },
                AppFormatting.BadgeCss(status),
                GetNextSliceTiming(record, definition, lifecycle, summary, completed, queuedAvailability, latestSliceEnds, now),
                completed);
        }

        private static IReadOnlyList<JobTagSummary> BuildTagSummaries(IReadOnlyList<JobListItem> jobs, IReadOnlyList<string> selectedTags)
        {
            var tagCounts = jobs
                .Where(job => job.LifecycleStatus != "SoftDeleted")
                .SelectMany(job => job.Definition.Tags)
                .GroupBy(tag => tag, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            return tagCounts.Keys
                .Concat(selectedTags)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(tag => tag, StringComparer.Ordinal)
                .Select(tag => new JobTagSummary(tag, tagCounts.GetValueOrDefault(tag), selectedTags.Contains(tag, StringComparer.Ordinal)))
                .ToList();
        }

        private static bool MatchesSelectedTags(JobListItem job, IReadOnlyList<string> selectedTags) =>
            selectedTags.All(tag => job.Definition.Tags.Contains(tag, StringComparer.Ordinal));

        private static bool IsCompletedSchedule(JobDefinition definition, JobStatusSummary? summary, DateTimeOffset now)
        {
            if (definition.EndOn is not { } endOn)
            {
                return false;
            }

            var delayedEnd = now.ToUniversalTime() - definition.DelayFromUtcNow;
            if (endOn.ToUniversalTime() > delayedEnd)
            {
                return false;
            }

            var expectedSlices = CountFiniteEligibleSlices(definition, endOn);
            var completedSlices = summary?.CompletedCount ?? 0;
            var incompleteKnownSlices = summary is null
                ? 0
                : summary.QueuedCount
                    + summary.RunningCount
                    + summary.FailedCount
                    + summary.DeadLetteredCount
                    + summary.DependencyBlockedCount;

            return incompleteKnownSlices == 0 && completedSlices >= expectedSlices;
        }

        private static long CountFiniteEligibleSlices(JobDefinition definition, DateTimeOffset endOn)
        {
            var startFromUtc = definition.StartFrom.ToUniversalTime();
            var endOnUtc = endOn.ToUniversalTime();
            if (endOnUtc <= startFromUtc)
            {
                return 0;
            }

            return (endOnUtc - startFromUtc).Ticks / definition.QueryWindowSize.Ticks;
        }

        private static NextSliceTiming GetNextSliceTiming(
            JobCatalogRecord record,
            JobDefinition definition,
            JobLifecycleProjection? lifecycle,
            JobStatusSummary? summary,
            bool completed,
            IReadOnlyDictionary<string, DateTimeOffset> queuedAvailability,
            IReadOnlyDictionary<string, DateTimeOffset> latestSliceEnds,
            DateTimeOffset now)
        {
            if (lifecycle?.IsSoftDeleted == true)
            {
                return new NextSliceTiming(null, "-", "Soft-deleted jobs are not scheduled.");
            }

            if (completed)
            {
                return new NextSliceTiming(null, "Complete", "The schedule end has passed and all eligible slices are complete.");
            }

            if (!record.IsEnabled || definition.IsPaused)
            {
                return new NextSliceTiming(null, "Paused", "Paused jobs are not eligible for new scheduling.");
            }

            if (queuedAvailability.TryGetValue(record.JobId, out var queuedAtUtc))
            {
                return Eligibility(queuedAtUtc, now, $"Queued work can be claimed at {AppFormatting.Iso(queuedAtUtc)}.");
            }

            var nextStartUtc = latestSliceEnds.TryGetValue(record.JobId, out var latestEndUtc)
                ? latestEndUtc
                : definition.StartFrom.ToUniversalTime();
            var startFromUtc = definition.StartFrom.ToUniversalTime();
            if (nextStartUtc < startFromUtc)
            {
                nextStartUtc = startFromUtc;
            }

            var nextEndUtc = nextStartUtc.Add(definition.QueryWindowSize);
            if (definition.EndOn is { } endOn && nextEndUtc > endOn.ToUniversalTime())
            {
                return ExhaustedButIncomplete(summary);
            }

            var eligibleAtUtc = nextEndUtc.Add(definition.DelayFromUtcNow);
            return Eligibility(eligibleAtUtc, now, $"Next window {AppFormatting.Iso(nextStartUtc)} to {AppFormatting.Iso(nextEndUtc)}.");
        }

        private static NextSliceTiming Eligibility(DateTimeOffset eligibleAtUtc, DateTimeOffset now, string detail) =>
            eligibleAtUtc <= now
                ? new NextSliceTiming(eligibleAtUtc, "Eligible now", detail)
                : new NextSliceTiming(eligibleAtUtc, AppFormatting.Iso(eligibleAtUtc), detail);

        private static NextSliceTiming ExhaustedButIncomplete(JobStatusSummary? summary)
        {
            const string detail = "The schedule end has passed, but not all eligible slices are complete.";
            return summary switch
            {
                { QueuedCount: > 0 } or { RunningCount: > 0 } => new NextSliceTiming(null, "In progress", detail),
                { DependencyBlockedCount: > 0 } => new NextSliceTiming(null, "Blocked", detail),
                { FailedCount: > 0 } or { DeadLetteredCount: > 0 } => new NextSliceTiming(null, "Attention", detail),
                _ => new NextSliceTiming(null, "Incomplete", detail)
            };
        }
    }
}
