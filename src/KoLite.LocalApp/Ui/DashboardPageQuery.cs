// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Schedules;
using KoLite.Local.Core.Scheduling;
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
        string? TableKey = null,
        string? HeaderActionHref = null,
        string? HeaderActionText = null);

    public sealed record JobListItem(
        JobCatalogRecord Record,
        JobDefinition Definition,
        JobStatusSummary? Summary,
        JobLifecycleProjection? Lifecycle,
        string PrimaryState,
        string StatusText,
        string StatusCss,
        JobHealth Health,
        bool InProgress,
        string HealthTooltip,
        string CompletenessTooltip,
        NextSliceTiming NextSlice,
        bool IsCompleted)
    {
        public int TotalSlices => Summary is null
            ? 0
            : Summary.MissingCount + Summary.QueuedCount + Summary.RunningCount + Summary.CompletedCount + Summary.FailedCount + Summary.DeadLetteredCount + Summary.DependencyBlockedCount;

        public int FailedSlices => Summary is null ? 0 : Summary.FailedCount + Summary.DeadLetteredCount;

        public int WaitingOnDependencySlices => Summary is null ? 0 : Summary.DependencyBlockedCount;

        // Whether to render the completeness segment of the split pill. Complete-policy jobs show
        // it (green "Complete" or amber "N gaps"); Recent-policy and soft-deleted jobs do not.
        public bool ShowCompleteness => Health.TracksCompleteness && PrimaryState != "SoftDeleted";

        public int GapCount => Health.GapCount;

        // Ordering weight for the "status" sort: higher is more urgent so attention rises to the
        // top. Historical gaps nudge an otherwise-calm job up so incomplete jobs are easy to find.
        public int StatusSortRank
        {
            get
            {
                var rank = PrimaryState switch
                {
                    "Attention" => 60,
                    "DependencyBlocked" => 50,
                    "Borderline" => 40,
                    "Paused" => 20,
                    "WaitingOnUpstream" => 15,
                    "Completed" => 10,
                    "SoftDeleted" => 0,
                    _ => 5 // Healthy
                };

                return ShowCompleteness && GapCount > 0 ? Math.Max(rank, 30) : rank;
            }
        }
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
            var activeJobs = ApplySort(filteredJobs.Where(j => j.PrimaryState != "SoftDeleted" && !j.IsCompleted), effectiveSort);
            var completedJobs = ApplySort(filteredJobs.Where(j => j.PrimaryState != "SoftDeleted" && j.IsCompleted), effectiveSort);
            var softDeletedJobs = ApplySort(filteredJobs.Where(j => j.PrimaryState == "SoftDeleted"), effectiveSort);
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
            var recentStates = readModels.GetRecentSliceStates(JobHealthEvaluator.RecentWindowSize);
            var queuedAvailability = readModels.GetQueuedAvailabilityByJob();
            var latestSliceEnds = readModels.GetLatestSliceEndsByJob();
            var records = catalog.List();
            var healthByJob = BuildHealthMap(records, summaries, recentStates);
            var baseStatusById = BuildBaseStatusMap(records, lifecycleStates, summaries, healthByJob, now);
            var classifier = DependencyStatusClassifier.Build(records, baseStatusById);
            return records
                .Select(record => BuildJobListItem(record, lifecycleStates, summaries, healthByJob, queuedAvailability, latestSliceEnds, now, classifier))
                .ToList();
        }

        private static IReadOnlyList<JobListItem> ApplySort(IEnumerable<JobListItem> jobs, DashboardSort sort)
        {
            var ordered = sort.Key switch
            {
                "status" => sort.Descending
                    ? jobs.OrderByDescending(j => j.StatusSortRank)
                    : jobs.OrderBy(j => j.StatusSortRank),
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
            var recentStates = readModels.GetRecentSliceStates(JobHealthEvaluator.RecentWindowSize);
            var queuedAvailability = readModels.GetQueuedAvailabilityByJob();
            var latestSliceEnds = readModels.GetLatestSliceEndsByJob();
            // The whole catalog is needed so upstream health can be classified transitively.
            var records = catalog.List();
            var healthByJob = BuildHealthMap(records, summaries, recentStates);
            var baseStatusById = BuildBaseStatusMap(records, lifecycleStates, summaries, healthByJob, now);
            var classifier = DependencyStatusClassifier.Build(records, baseStatusById);
            return BuildJobListItem(record, lifecycleStates, summaries, healthByJob, queuedAvailability, latestSliceEnds, now, classifier);
        }

        private static JobListItem BuildJobListItem(
            JobCatalogRecord record,
            IReadOnlyDictionary<string, JobLifecycleProjection> lifecycleStates,
            IReadOnlyDictionary<string, JobStatusSummary> summaries,
            IReadOnlyDictionary<string, JobHealth> healthByJob,
            IReadOnlyDictionary<string, DateTimeOffset> queuedAvailability,
            IReadOnlyDictionary<string, DateTimeOffset> latestSliceEnds,
            DateTimeOffset now,
            DependencyStatusClassifier classifier)
        {
            var definition = record.Definition;
            lifecycleStates.TryGetValue(record.JobId, out var lifecycle);
            summaries.TryGetValue(record.JobId, out var summary);
            var completed = IsCompletedSchedule(definition, summary, now);
            var health = healthByJob.TryGetValue(record.JobId, out var h)
                ? h
                : JobHealthEvaluator.Evaluate(Array.Empty<string>(), definition.HealthPolicy, summary?.DeadLetteredCount ?? 0);
            var primary = classifier.Resolve(record.JobId);
            var inProgress = summary is { QueuedCount: > 0 } or { RunningCount: > 0 };

            return new JobListItem(
                record,
                definition,
                summary,
                lifecycle,
                primary,
                AppFormatting.PrimaryStatusLabel(primary),
                AppFormatting.PrimaryStatusBadgeCss(primary),
                health,
                inProgress,
                BuildHealthTooltip(primary, health, inProgress),
                BuildCompletenessTooltip(health),
                GetNextSliceTiming(record, definition, lifecycle, summary, completed, queuedAvailability, latestSliceEnds, now),
                completed);
        }

        private static IReadOnlyDictionary<string, JobHealth> BuildHealthMap(
            IReadOnlyList<JobCatalogRecord> records,
            IReadOnlyDictionary<string, JobStatusSummary> summaries,
            IReadOnlyDictionary<string, IReadOnlyList<string>> recentStates)
        {
            var map = new Dictionary<string, JobHealth>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                summaries.TryGetValue(record.JobId, out var summary);
                var recent = recentStates.TryGetValue(record.JobId, out var states) ? states : Array.Empty<string>();
                map[record.JobId] = JobHealthEvaluator.Evaluate(recent, record.Definition.HealthPolicy, summary?.DeadLetteredCount ?? 0);
            }

            return map;
        }

        private static IReadOnlyDictionary<string, string> BuildBaseStatusMap(
            IReadOnlyList<JobCatalogRecord> records,
            IReadOnlyDictionary<string, JobLifecycleProjection> lifecycleStates,
            IReadOnlyDictionary<string, JobStatusSummary> summaries,
            IReadOnlyDictionary<string, JobHealth> healthByJob,
            DateTimeOffset now)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var record in records)
            {
                lifecycleStates.TryGetValue(record.JobId, out var lifecycle);
                summaries.TryGetValue(record.JobId, out var summary);
                var completed = IsCompletedSchedule(record.Definition, summary, now);
                healthByJob.TryGetValue(record.JobId, out var health);
                map[record.JobId] = ComputeBasePrimaryState(record, lifecycle, summary, health, completed);
            }

            return map;
        }

        // The base job status ignoring dependency-health reclassification. Lifecycle states (soft
        // deleted, completed, paused) win first because they are intentional/terminal; otherwise the
        // recent-trend health tier drives the color. The dependency-blocked family is resolved to a
        // calm ("WaitingOnUpstream") or an attention ("DependencyBlocked") state by the classifier.
        private static string ComputeBasePrimaryState(
            JobCatalogRecord record,
            JobLifecycleProjection? lifecycle,
            JobStatusSummary? summary,
            JobHealth? health,
            bool completed)
            => lifecycle?.IsSoftDeleted == true
                ? "SoftDeleted"
                : completed
                    ? "Completed"
                    : !record.IsEnabled
                        ? "Paused"
                        : health?.Tier == JobHealthTier.Attention
                            ? "Attention"
                            : health?.Tier == JobHealthTier.Borderline
                                ? "Borderline"
                                : summary is { DependencyBlockedCount: > 0 }
                                    ? "DependencyBlocked"
                                    : "Healthy";

        // Builds the hover tooltip for the health (left) half of the color pill: a plain-language
        // description of the recent-trend status, the recent slice ratio, and whether work is in
        // progress (running/queued is surfaced only here now that the pill carries no dot).
        private static string BuildHealthTooltip(string primary, JobHealth health, bool inProgress)
        {
            var parts = new List<string>
            {
                primary switch
                {
                    "Attention" => "Attention \u2014 recent slices are failing now",
                    "Borderline" => "Warning \u2014 some recent slices failed",
                    "Healthy" => "Healthy \u2014 recent slices are succeeding",
                    "Paused" => "Paused",
                    "Completed" => "Completed",
                    "DependencyBlocked" => "Blocked \u2014 waiting on an unhealthy upstream",
                    "WaitingOnUpstream" => "Waiting on a healthy upstream",
                    "SoftDeleted" => "Soft deleted",
                    _ => primary
                }
            };

            if (health.RecentConsidered > 0)
            {
                parts.Add($"recent {health.RecentSucceeded}/{health.RecentConsidered} slices succeeded");
            }

            if (inProgress)
            {
                parts.Add("work in progress");
            }

            return string.Join(" \u00b7 ", parts);
        }

        // Builds the hover tooltip for the completeness (right) half of the color pill. Only shown for
        // complete-policy jobs, so callers gate on ShowCompleteness.
        private static string BuildCompletenessTooltip(JobHealth health) =>
            health.GapCount > 0
                ? $"{health.GapCount} unaddressed dead-lettered slice(s) \u2014 rerun or repair to close the gaps"
                : "History complete \u2014 no unaddressed gaps";

        private static IReadOnlyList<JobTagSummary> BuildTagSummaries(IReadOnlyList<JobListItem> jobs, IReadOnlyList<string> selectedTags)
        {
            var tagCounts = jobs
                .Where(job => job.PrimaryState != "SoftDeleted")
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

        // Classifies each job's dependency-blocked family into a calm "WaitingOnUpstream" (the whole
        // ancestor chain is healthy and simply behind) or an attention-worthy "DependencyBlocked" (an
        // ancestor has failed, is paused, is soft-deleted, or is missing). The distinction is fully
        // transitive: a job is blocked if any ancestor along its DependsOn chain is a stuck source.
        // Only jobs whose base status is already the blocked family are reclassified.
        internal sealed class DependencyStatusClassifier
        {
            private static readonly string[] StuckBaseStatuses = { "Attention", "Paused", "SoftDeleted" };

            private readonly IReadOnlyDictionary<string, string> baseStatusById;
            private readonly IReadOnlyDictionary<string, bool> blockedByStuckById;

            private DependencyStatusClassifier(
                IReadOnlyDictionary<string, string> baseStatusById,
                IReadOnlyDictionary<string, bool> blockedByStuckById)
            {
                this.baseStatusById = baseStatusById;
                this.blockedByStuckById = blockedByStuckById;
            }

            public string Resolve(string jobId)
            {
                var baseStatus = baseStatusById.TryGetValue(jobId, out var value) ? value : "Healthy";
                if (baseStatus != "DependencyBlocked")
                {
                    return baseStatus;
                }

                return blockedByStuckById.TryGetValue(jobId, out var stuck) && stuck
                    ? "DependencyBlocked"
                    : "WaitingOnUpstream";
            }

            public static DependencyStatusClassifier Build(
                IReadOnlyList<JobCatalogRecord> records,
                IReadOnlyDictionary<string, string> baseStatusById)
            {
                var jobIdByActivityId = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var record in records)
                {
                    jobIdByActivityId[record.ActivityId] = record.JobId;
                }

                var knownJobIds = new HashSet<string>(records.Select(record => record.JobId), StringComparer.Ordinal);
                var upstreamsById = new Dictionary<string, IReadOnlyList<string?>>(StringComparer.Ordinal);
                foreach (var record in records)
                {
                    var upstreams = new List<string?>();
                    foreach (var dependency in record.Definition.DependsOn)
                    {
                        // A null entry marks an unresolvable/missing upstream reference.
                        upstreams.Add(ResolveUpstreamJobId(dependency, knownJobIds, jobIdByActivityId));
                    }

                    upstreamsById[record.JobId] = upstreams;
                }

                var blockedByStuck = new Dictionary<string, bool>(StringComparer.Ordinal);
                var visiting = new HashSet<string>(StringComparer.Ordinal);
                foreach (var record in records)
                {
                    ComputeBlockedByStuck(record.JobId, baseStatusById, upstreamsById, blockedByStuck, visiting);
                }

                return new DependencyStatusClassifier(baseStatusById, blockedByStuck);
            }

            private static bool ComputeBlockedByStuck(
                string jobId,
                IReadOnlyDictionary<string, string> baseStatusById,
                IReadOnlyDictionary<string, IReadOnlyList<string?>> upstreamsById,
                Dictionary<string, bool> memo,
                HashSet<string> visiting)
            {
                if (memo.TryGetValue(jobId, out var cached))
                {
                    return cached;
                }

                // Cycle guard: a malformed dependency cycle must not recurse forever. Treat the
                // re-entered node as not-yet-stuck so the traversal can unwind without caching a
                // partial answer.
                if (!visiting.Add(jobId))
                {
                    return false;
                }

                var hasUpstreams = upstreamsById.TryGetValue(jobId, out var upstreams) && upstreams.Count > 0;

                // A blocked-family job with no declared dependency is an unexplained block: there is no
                // upstream to vouch that the wait is healthy, so keep it flagged rather than calling it a
                // calm wait. (In practice a blocked slice always has a declared dependency.)
                var result = !hasUpstreams
                    && baseStatusById.TryGetValue(jobId, out var ownBase)
                    && ownBase == "DependencyBlocked";

                if (hasUpstreams)
                {
                    foreach (var upstreamId in upstreams!)
                    {
                        if (upstreamId is null)
                        {
                            result = true;
                            break;
                        }

                        if (IsStuckSource(baseStatusById, upstreamId)
                            || ComputeBlockedByStuck(upstreamId, baseStatusById, upstreamsById, memo, visiting))
                        {
                            result = true;
                            break;
                        }
                    }
                }

                visiting.Remove(jobId);
                memo[jobId] = result;
                return result;
            }

            private static bool IsStuckSource(IReadOnlyDictionary<string, string> baseStatusById, string jobId)
                => baseStatusById.TryGetValue(jobId, out var status) && Array.IndexOf(StuckBaseStatuses, status) >= 0;

            private static string? ResolveUpstreamJobId(
                DependentJob dependency,
                IReadOnlySet<string> knownJobIds,
                IReadOnlyDictionary<string, string> jobIdByActivityId)
            {
                if (!string.IsNullOrWhiteSpace(dependency.Id))
                {
                    return knownJobIds.Contains(dependency.Id) ? dependency.Id : null;
                }

                if (!string.IsNullOrWhiteSpace(dependency.ActivityId)
                    && jobIdByActivityId.TryGetValue(dependency.ActivityId, out var resolved))
                {
                    return resolved;
                }

                return null;
            }
        }
    }
}
