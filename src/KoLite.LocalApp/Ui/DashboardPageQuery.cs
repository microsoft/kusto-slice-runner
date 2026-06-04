using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Connections;
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
        TimeSpan SelectedRange);

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

    public sealed class DashboardPageQuery(
        SqliteJobCatalogRepository catalog,
        SqliteOperationalReadModelRepository readModels,
        LifecycleReadModel lifecycleReadModel,
        JobChartQuery chartQuery,
        IKoLiteSqliteConnectionFactory connectionFactory,
        IClock clock)
    {
        public DashboardPageData Get(TimeSpan selectedRange, IEnumerable<string>? selectedTags = null)
        {
            var now = clock.UtcNow;
            var normalizedSelectedTags = ScheduleTags.NormalizeDistinct(selectedTags ?? Array.Empty<string>());
            var lifecycleStates = lifecycleReadModel.GetLatestStates();
            var summaries = readModels.GetJobStatusSummaries().ToDictionary(s => s.JobId, StringComparer.Ordinal);
            var queuedAvailability = GetQueuedAvailability();
            var latestSliceEnds = GetLatestSliceEnds();
            var jobs = catalog.List().Select(record =>
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
            }).ToList();
            var tagSummaries = BuildTagSummaries(jobs, normalizedSelectedTags);
            var filteredJobs = normalizedSelectedTags.Count == 0
                ? jobs
                : jobs.Where(job => MatchesSelectedTags(job, normalizedSelectedTags)).ToList();
            var activeJobs = filteredJobs.Where(j => j.LifecycleStatus != "SoftDeleted" && !j.IsCompleted).OrderBy(j => j.Record.JobId, StringComparer.Ordinal).ToList();
            var completedJobs = filteredJobs.Where(j => j.LifecycleStatus != "SoftDeleted" && j.IsCompleted).OrderBy(j => j.Record.JobId, StringComparer.Ordinal).ToList();
            var softDeletedJobs = filteredJobs.Where(j => j.LifecycleStatus == "SoftDeleted").OrderBy(j => j.Record.JobId, StringComparer.Ordinal).ToList();
            var chartJobIds = activeJobs.Select(job => job.Record.JobId).ToArray();

            return new DashboardPageData(
                activeJobs,
                completedJobs,
                softDeletedJobs,
                tagSummaries,
                normalizedSelectedTags,
                chartQuery.GetDashboardCharts(selectedRange, chartJobIds),
                readModels.GetRecentFailures(10),
                selectedRange);
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

        private IReadOnlyDictionary<string, DateTimeOffset> GetQueuedAvailability()
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_id, MIN(available_at_utc)
                FROM work_queue
                WHERE state = 'Queued'
                GROUP BY job_id;
                """;
            using var reader = command.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            while (reader.Read())
            {
                results[reader.GetString(0)] = SqliteUi.ParseUtc(reader.GetString(1));
            }

            return results;
        }

        private IReadOnlyDictionary<string, DateTimeOffset> GetLatestSliceEnds()
        {
            using var connection = connectionFactory.OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_id, MAX(slice_end_utc)
                FROM current_slice_state
                GROUP BY job_id;
                """;
            using var reader = command.ExecuteReader();
            var results = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            while (reader.Read())
            {
                results[reader.GetString(0)] = SqliteUi.ParseUtc(reader.GetString(1));
            }

            return results;
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
