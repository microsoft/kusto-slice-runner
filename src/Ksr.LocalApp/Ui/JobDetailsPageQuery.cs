// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Globalization;
using Ksr.Local.Core.Schedules;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Core.Time;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Observability;
using Ksr.Local.Sqlite.Queue;
using Ksr.Local.Sqlite.State;

namespace Ksr.LocalApp.Ui
{
    public sealed record SliceHistoryCell(
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        string State,
        int Attempt,
        string CssClass,
        string StatusLabel,
        string Url,
        IReadOnlyList<SliceHistoryTooltipLine> TooltipLines)
    {
        public string AccessibleLabel
        {
            get
            {
                var chunks = TooltipLines.FirstOrDefault(line => string.Equals(line.Label, "Chunks", StringComparison.Ordinal));
                var chunkText = chunks is null ? string.Empty : $"; Chunks {chunks.Value}";
                return $"{AppFormatting.Iso(SliceStartUtc)} to {AppFormatting.Iso(SliceEndUtc)}; {StatusLabel}{chunkText}; attempt {Attempt}";
            }
        }

        public string TooltipText => string.Join("\n", TooltipLines.Select(line => $"{line.Label}: {line.Value}"));
    }

    public sealed record SliceHistoryTooltipLine(string Label, string Value);
    public sealed record SliceHistoryRow(string HourLabel, IReadOnlyList<SliceHistoryCell> Cells);
    public sealed record SliceHistoryRange(
        DateTimeOffset RequestedStartUtc,
        DateTimeOffset RequestedEndUtc,
        DateTimeOffset DisplayStartUtc,
        DateTimeOffset DisplayEndUtc,
        bool IsLimited,
        int CellLimit);

    public sealed record JobDetailsPageData(
        JobCatalogRecord Job,
        JobDefinition Definition,
        JobStatusSummary? Summary,
        JobLifecycleProjection? Lifecycle,
        IReadOnlyList<CatalogHistoryDisplayRow> CatalogHistory,
        IReadOnlyList<SliceHistoryRow> SliceHistory,
        SliceHistoryRange SliceHistoryRange,
        IReadOnlyList<SliceStatusReadout> SliceStatuses,
        IReadOnlyList<DurableWorkItem> QueueItems,
        IReadOnlyList<SliceAttemptReadout> RecentAttempts,
        IReadOnlyList<OperationalLogReadout> RecentLogs,
        IReadOnlyList<SliceEventReadout> RecentEvents,
        bool HasStarted,
        CatchUpProjection CatchUp);

    public sealed record SliceDetailsPageData(
        JobCatalogRecord Job,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        SliceStatusReadout? Status,
        IReadOnlyList<DurableWorkItem> QueueItems,
        IReadOnlyList<SliceAttemptReadout> Attempts,
        IReadOnlyList<OperationalLogReadout> Logs,
        IReadOnlyList<SliceEventReadout> Events,
        IReadOnlyList<DurableChunkState> Chunks,
        IReadOnlyList<ChunkStateEventReadout> ChunkEvents,
        bool IsOrphaned);

    public sealed class JobDetailsPageQuery
    {
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteWorkQueueRepository queue;
        private readonly SqliteSliceStateRepository? sliceState;
        private readonly SqliteChunkStateRepository? chunkState;
        private readonly LifecycleReadModel lifecycle;
        private readonly OperationalDetailsReadModel operationalDetails;
        private readonly IClock clock;

        // sliceState is an optional trailing dependency so the existing positional test callers that
        // construct this query with six arguments keep compiling. The DI container always supplies the
        // registered SqliteSliceStateRepository; when it is null the "waiting on" tooltip lines are skipped.
        public JobDetailsPageQuery(
            SqliteJobCatalogRepository catalog,
            SqliteOperationalReadModelRepository readModels,
            SqliteWorkQueueRepository queue,
            LifecycleReadModel lifecycle,
            OperationalDetailsReadModel operationalDetails,
            IClock clock,
            SqliteSliceStateRepository? sliceState = null,
            SqliteChunkStateRepository? chunkState = null)
        {
            this.catalog = catalog;
            this.readModels = readModels;
            this.queue = queue;
            this.sliceState = sliceState;
            this.chunkState = chunkState;
            this.lifecycle = lifecycle;
            this.operationalDetails = operationalDetails;
            this.clock = clock;
        }

        public const int DefaultSliceHistoryCellLimit = 240;
        public const int FullSliceHistoryCellLimit = 20_000;
        private static readonly TimeSpan HourSliceHistoryRowSpan = TimeSpan.FromHours(1);
        private static readonly TimeSpan DaySliceHistoryRowSpan = TimeSpan.FromDays(1);

        public JobDetailsPageData? Get(string jobId, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, int sliceHistoryCellLimit = DefaultSliceHistoryCellLimit)
        {
            var job = catalog.Get(jobId);
            if (job is null)
            {
                return null;
            }

            var summaries = readModels.GetJobStatusSummaries().ToDictionary(s => s.JobId, StringComparer.Ordinal);
            summaries.TryGetValue(jobId, out var summary);
            var lifecycleStates = lifecycle.GetLatestStates();
            lifecycleStates.TryGetValue(jobId, out var lifecycleState);
            var statuses = readModels.GetSliceStatus(jobId);
            var queueItems = queue.List(jobId);
            var waitingLinesByStart = BuildWaitingOnTooltipLines(job.Definition, statuses);
            var completedChunksByStart = BuildCompletedChunksByStart(job.Definition);
            var sliceHistory = BuildSliceHistory(job.Definition, statuses, queueItems, fromUtc, toUtc, sliceHistoryCellLimit, waitingLinesByStart, completedChunksByStart);
            var catalogHistory = CatalogHistoryDiffBuilder.Build(catalog.History(jobId));
            var catchUp = BuildCatchUp(job, job.Definition, statuses, catalogHistory);
            return new JobDetailsPageData(
                job,
                job.Definition,
                summary,
                lifecycleState,
                catalogHistory,
                sliceHistory.Rows,
                sliceHistory.Range,
                statuses,
                queueItems,
                operationalDetails.GetAttempts(jobId, take: 25),
                operationalDetails.GetLogs(jobId, take: 25),
                operationalDetails.GetEvents(jobId, take: 25),
                catalog.HasStarted(jobId),
                catchUp);
        }

        private IReadOnlyDictionary<DateTimeOffset, int> BuildCompletedChunksByStart(JobDefinition definition)
        {
            if (definition.Chunks is not { } configuredChunks || chunkState is null)
            {
                return new Dictionary<DateTimeOffset, int>();
            }

            var rows = chunkState.ListCompletionProgress(definition.Id!);
            foreach (var row in rows)
            {
                if (row.TotalChunks != configuredChunks)
                {
                    throw new InvalidOperationException(
                        $"Stored chunk count {row.TotalChunks} does not match configured chunks {configuredChunks} " +
                        $"for job '{definition.ActivityId}', slice {AppFormatting.Iso(row.SliceStartUtc)}.");
                }
            }

            return rows.ToDictionary(row => row.SliceStartUtc.ToUniversalTime(), row => row.CompletedChunks);
        }

        // Estimates how long the job will take to work through its eligible backlog and reach its
        // normal delay-bounded frontier. The throughput sample only counts executions since the
        // last schedule change (capped at the recent lookback) so a definition edit does not skew
        // the rate with executions that ran under a different definition.
        private CatchUpProjection BuildCatchUp(
            JobCatalogRecord job,
            JobDefinition definition,
            IReadOnlyList<SliceStatusReadout> statuses,
            IReadOnlyList<CatalogHistoryDisplayRow> catalogHistory)
        {
            var now = clock.UtcNow;
            var completed = statuses.Where(s => string.Equals(s.Status, "Completed", StringComparison.Ordinal)).ToList();
            DateTimeOffset? completedFrontier = completed.Count == 0 ? null : completed.Max(s => s.SliceEndUtc);

            // Slices that are eligible by time but parked waiting on an upstream dependency. They are
            // excluded from the backlog so a job that is only waiting on upstream (for its most recent
            // slices) is not reported as catching up.
            var dependencyBlocked = statuses.Count(s => string.Equals(s.Status, "DependencyBlocked", StringComparison.Ordinal));

            // Terminal dead-lettered slices are treated as done: they will not run again without an
            // operator rerun/repair, so counting them would keep a job that has finished everything it
            // will do on its own permanently reported as catching up.
            var deadLettered = statuses.Count(s => string.Equals(s.Status, "DeadLettered", StringComparison.Ordinal));

            var lastDefinitionChange = catalogHistory
                .Where(h => h.IsInitialDefinition || h.HasScheduleChanges)
                .Select(h => (DateTimeOffset?)h.RecordedAtUtc)
                .FirstOrDefault();

            var options = CatchUpOptions.Default;
            var sinceUtc = CatchUpEstimator.ResolveThroughputWindowStart(now, lastDefinitionChange, options.MaxThroughputLookback);
            var throughput = readModels.GetRecentSucceededThroughput(job.JobId, sinceUtc);
            var sample = new CatchUpThroughputSample(throughput.SucceededCount, throughput.FirstCompletedUtc, throughput.LastCompletedUtc);

            return CatchUpEstimator.Estimate(now, definition, job.IsEnabled, completedFrontier, completed.Count, sample, dependencyBlocked, deadLettered, lastDefinitionChange, options);
        }

        public SliceDetailsPageData? GetSlice(string jobId, DateTimeOffset sliceStartUtc, DateTimeOffset sliceEndUtc)
        {
            var job = catalog.Get(jobId);
            if (job is null)
            {
                return null;
            }

            var status = readModels.GetSliceStatus(jobId)
                .FirstOrDefault(s => s.SliceStartUtc == sliceStartUtc && s.SliceEndUtc == sliceEndUtc);

            var now = clock.UtcNow;
            var queueItems = queue.List(jobId).Where(q => q.SliceStartUtc == sliceStartUtc && q.SliceEndUtc == sliceEndUtc).ToList();
            var chunks = job.Definition.Chunks is null || chunkState is null
                ? Array.Empty<DurableChunkState>()
                : chunkState.List(new SliceRange(jobId, sliceStartUtc, sliceEndUtc));
            var chunkEvents = job.Definition.Chunks is null || chunkState is null
                ? Array.Empty<ChunkStateEventReadout>()
                : chunkState.ListEvents(new SliceRange(jobId, sliceStartUtc, sliceEndUtc), 100);
            var isOrphaned = queueItems.Any(q => IsOrphanedLease(q, now))
                || chunks.Any(chunk => chunk.Status == DurableSliceStatus.Running
                    && (chunk.LeaseExpiresAtUtc is null || chunk.LeaseExpiresAtUtc <= now.ToUniversalTime()));

            return new SliceDetailsPageData(
                job,
                sliceStartUtc,
                sliceEndUtc,
                status,
                queueItems,
                operationalDetails.GetAttempts(jobId, sliceStartUtc, sliceEndUtc, 100),
                operationalDetails.GetLogs(jobId, sliceStartUtc, sliceEndUtc, 100),
                operationalDetails.GetEvents(jobId, sliceStartUtc, sliceEndUtc, 100),
                chunks,
                chunkEvents,
                isOrphaned);
        }

        private static SliceHistoryBuildResult BuildSliceHistory(
            JobDefinition definition,
            IReadOnlyList<SliceStatusReadout> statuses,
            IReadOnlyList<DurableWorkItem> queueItems,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int cellLimit,
            IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> waitingLinesByStart,
            IReadOnlyDictionary<DateTimeOffset, int> completedChunksByStart)
        {
            var now = DateTimeOffset.UtcNow;
            var queryWindow = definition.QueryWindowSize;
            var statusByStart = statuses.ToDictionary(s => s.SliceStartUtc.ToUniversalTime(), s => s);
            var activeQueueByStart = ActiveQueueByStart(queueItems);

            var start = fromUtc
                ?? (statuses.Count == 0
                    ? definition.StartFrom
                    : statuses.Min(s => s.SliceStartUtc));
            start = start.ToUniversalTime();

            var end = toUtc
                ?? (statuses.Count == 0
                    ? definition.StartFrom.Add(queryWindow)
                    : statuses.Max(s => s.SliceEndUtc));
            end = end.ToUniversalTime();

            if (definition.EndOn is { } endOn && end > endOn.ToUniversalTime())
            {
                end = endOn.ToUniversalTime();
            }

            if (end <= start)
            {
                end = start.Add(queryWindow);
            }

            var fixedRowSpan = FixedRowSpanFor(queryWindow);
            if (fixedRowSpan is { } rowSpan && rowSpan.Ticks % queryWindow.Ticks == 0)
            {
                return BuildFixedSpanSliceHistory(definition, statusByStart, activeQueueByStart, start, end, now, cellLimit, rowSpan, waitingLinesByStart, completedChunksByStart);
            }

            return BuildSequentialSliceHistory(definition, statusByStart, activeQueueByStart, start, end, now, cellLimit, waitingLinesByStart, completedChunksByStart);
        }

        private static SliceHistoryBuildResult BuildFixedSpanSliceHistory(
            JobDefinition definition,
            IReadOnlyDictionary<DateTimeOffset, SliceStatusReadout> statusByStart,
            IReadOnlyDictionary<DateTimeOffset, DurableWorkItem> activeQueueByStart,
            DateTimeOffset start,
            DateTimeOffset end,
            DateTimeOffset now,
            int cellLimit,
            TimeSpan rowSpan,
            IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> waitingLinesByStart,
            IReadOnlyDictionary<DateTimeOffset, int> completedChunksByStart)
        {
            var cellsPerRow = (int)(rowSpan.Ticks / definition.QueryWindowSize.Ticks);
            var rowStart = FloorToRowSpan(start, rowSpan);
            var rowEnd = CeilingToRowSpan(end, rowSpan);
            var maxRows = Math.Max(1, cellLimit / cellsPerRow);
            var rowCount = (int)((rowEnd - rowStart).Ticks / rowSpan.Ticks);
            var isLimited = false;
            if (rowCount > maxRows)
            {
                rowStart = rowEnd.AddTicks(-rowSpan.Ticks * maxRows);
                isLimited = true;
            }

            var rows = new List<SliceHistoryRow>();
            for (var row = rowStart; row < rowEnd; row = row.Add(rowSpan))
            {
                var cells = new List<SliceHistoryCell>(cellsPerRow);
                var firstSliceStart = FirstSliceStartAtOrAfter(row, definition.StartFrom, definition.QueryWindowSize);
                for (var i = 0; i < cellsPerRow; i++)
                {
                    cells.Add(BuildSliceHistoryCell(definition, statusByStart, activeQueueByStart, firstSliceStart.AddTicks(definition.QueryWindowSize.Ticks * i), now, waitingLinesByStart, completedChunksByStart));
                }

                rows.Add(new SliceHistoryRow(RowLabel(row, rowSpan), cells));
            }

            return new SliceHistoryBuildResult(rows, new SliceHistoryRange(start, end, rowStart, rowEnd, isLimited, cellLimit));
        }

        private static SliceHistoryBuildResult BuildSequentialSliceHistory(
            JobDefinition definition,
            IReadOnlyDictionary<DateTimeOffset, SliceStatusReadout> statusByStart,
            IReadOnlyDictionary<DateTimeOffset, DurableWorkItem> activeQueueByStart,
            DateTimeOffset start,
            DateTimeOffset end,
            DateTimeOffset now,
            int cellLimit,
            IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> waitingLinesByStart,
            IReadOnlyDictionary<DateTimeOffset, int> completedChunksByStart)
        {
            var queryWindow = definition.QueryWindowSize;
            var requestedStart = start;
            var requestedEnd = end;
            var maxEnd = start.AddTicks(queryWindow.Ticks * cellLimit);
            var isLimited = false;
            if (end > maxEnd)
            {
                start = end.AddTicks(-queryWindow.Ticks * cellLimit);
                isLimited = true;
            }

            var cells = new List<SliceHistoryCell>();
            for (var cursor = start; cursor < end && cells.Count < cellLimit; cursor = cursor.Add(queryWindow))
            {
                cells.Add(BuildSliceHistoryCell(definition, statusByStart, activeQueueByStart, cursor, now, waitingLinesByStart, completedChunksByStart));
            }

            var rows = cells
                .GroupBy(c => new DateTimeOffset(c.SliceStartUtc.UtcDateTime.Year, c.SliceStartUtc.UtcDateTime.Month, c.SliceStartUtc.UtcDateTime.Day, c.SliceStartUtc.UtcDateTime.Hour, 0, 0, TimeSpan.Zero))
                .OrderBy(g => g.Key)
                .Select(g => new SliceHistoryRow(g.Key.ToString("yyyy-MM-dd HH:00", System.Globalization.CultureInfo.InvariantCulture), g.OrderBy(c => c.SliceStartUtc).ToList()))
                .ToList();
            var displayEnd = cells.Count == 0 ? start : cells[^1].SliceEndUtc;
            return new SliceHistoryBuildResult(rows, new SliceHistoryRange(requestedStart, requestedEnd, start, displayEnd, isLimited, cellLimit));
        }

        private sealed record SliceHistoryBuildResult(IReadOnlyList<SliceHistoryRow> Rows, SliceHistoryRange Range);

        private static SliceHistoryCell BuildSliceHistoryCell(
            JobDefinition definition,
            IReadOnlyDictionary<DateTimeOffset, SliceStatusReadout> statusByStart,
            IReadOnlyDictionary<DateTimeOffset, DurableWorkItem> activeQueueByStart,
            DateTimeOffset sliceStart,
            DateTimeOffset now,
            IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> waitingLinesByStart,
            IReadOnlyDictionary<DateTimeOffset, int> completedChunksByStart)
        {
            sliceStart = sliceStart.ToUniversalTime();
            var sliceEnd = sliceStart.Add(definition.QueryWindowSize);
            statusByStart.TryGetValue(sliceStart, out var status);
            activeQueueByStart.TryGetValue(sliceStart, out var activeQueueItem);
            var state = VisualState(status, activeQueueItem, definition, sliceStart, sliceEnd, now);
            var attempt = DisplayAttempt(status, activeQueueItem);
            var css = AppFormatting.StateCss(state);
            var statusLabel = AppFormatting.StatusLabel(state);
            var url = $"/jobs/{Uri.EscapeDataString(definition.Id!)}/slices?start={Uri.EscapeDataString(AppFormatting.Iso(sliceStart))}&end={Uri.EscapeDataString(AppFormatting.Iso(sliceEnd))}";
            var completedChunks = definition.Chunks is null ? (int?)null : completedChunksByStart.GetValueOrDefault(sliceStart);
            var tooltipLines = BuildTooltipLines(sliceStart, sliceEnd, statusLabel, attempt, activeQueueItem, state, waitingLinesByStart, completedChunks, definition.Chunks);
            return new SliceHistoryCell(sliceStart, sliceEnd, state, attempt, css, statusLabel, url, tooltipLines);
        }

        private static IReadOnlyList<SliceHistoryTooltipLine> BuildTooltipLines(
            DateTimeOffset sliceStart,
            DateTimeOffset sliceEnd,
            string statusLabel,
            int attempt,
            DurableWorkItem? activeQueueItem,
            string state,
            IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> waitingLinesByStart,
            int? completedChunks,
            int? totalChunks)
        {
            var lines = new List<SliceHistoryTooltipLine>
            {
                new("Start", AppFormatting.Iso(sliceStart)),
                new("End", AppFormatting.Iso(sliceEnd)),
                new("Status", statusLabel)
            };

            if (totalChunks is { } total)
            {
                lines.Add(new SliceHistoryTooltipLine(
                    "Chunks",
                    $"{completedChunks.GetValueOrDefault().ToString(CultureInfo.InvariantCulture)}/{total.ToString(CultureInfo.InvariantCulture)}"));
            }

            lines.Add(new SliceHistoryTooltipLine("Attempt", attempt.ToString(CultureInfo.InvariantCulture)));

            if (activeQueueItem is not null)
            {
                lines.Add(new SliceHistoryTooltipLine("Queue", AppFormatting.StatusLabel(activeQueueItem.State.ToString())));
                lines.Add(new SliceHistoryTooltipLine("Available", AppFormatting.Iso(activeQueueItem.AvailableAtUtc)));
                if (activeQueueItem.LockedUntilUtc is { } lockedUntil)
                {
                    lines.Add(new SliceHistoryTooltipLine("Lease until", AppFormatting.Iso(lockedUntil)));
                }
                lines.Add(new SliceHistoryTooltipLine(
                    "Queue attempts",
                    $"{activeQueueItem.Attempts.ToString(CultureInfo.InvariantCulture)}/{activeQueueItem.MaxAttempts.ToString(CultureInfo.InvariantCulture)}"));
            }

            // Dependency-blocked slices carry one or more "Waiting on <upstream> [range]" lines naming the
            // specific upstream job(s) and the unmet slice window(s) they are parked on.
            if (string.Equals(state, "DependencyBlocked", StringComparison.Ordinal)
                && waitingLinesByStart.TryGetValue(sliceStart, out var waitingLines))
            {
                lines.AddRange(waitingLines);
            }

            return lines;
        }

        private const int MaxWaitingOnTooltipLines = 3;

        private static readonly IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> NoWaitingTooltipLines =
            new Dictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>>(0);

        // Builds the "Waiting on <upstream> [range]" tooltip lines for dependency-blocked slices, keyed by
        // UTC slice start. For each blocked slice it recomputes the unmet upstream slices with the same
        // evaluator the scheduler uses, names the upstream job by its activityId, and collapses contiguous
        // missing windows. It only runs when at least one slice is blocked, so the recompute cost is bounded;
        // the catalog list and completed-slice set are each fetched once.
        private IReadOnlyDictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>> BuildWaitingOnTooltipLines(
            JobDefinition definition,
            IReadOnlyList<SliceStatusReadout> statuses)
        {
            if (sliceState is null)
            {
                return NoWaitingTooltipLines;
            }

            var blocked = statuses
                .Where(s => string.Equals(s.Status, "DependencyBlocked", StringComparison.Ordinal))
                .ToList();
            if (blocked.Count == 0)
            {
                return NoWaitingTooltipLines;
            }

            // Resolve sibling jobs once: GUID -> definition (for the required-slice math) and GUID ->
            // activityId (for the display name). A sibling whose stored schedule fails to parse still
            // contributes its display name and never breaks this page.
            var jobsById = new Dictionary<string, JobDefinition>(StringComparer.Ordinal);
            var activityIdById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var record in catalog.List())
            {
                activityIdById[record.JobId] = record.ActivityId;
                try
                {
                    jobsById[record.JobId] = record.Definition;
                }
                catch (InvalidOperationException)
                {
                    // Unparseable sibling schedule: the display name remains available via activityIdById.
                }
            }

            var completedSlices = sliceState.ListCompletedSliceKeys();
            var map = new Dictionary<DateTimeOffset, IReadOnlyList<SliceHistoryTooltipLine>>();
            foreach (var status in blocked)
            {
                var downstreamSlice = new SliceRange(definition.Id ?? string.Empty, status.SliceStartUtc, status.SliceEndUtc);
                var readiness = DependencyReadinessEvaluator.Evaluate(definition, downstreamSlice, jobsById, completedSlices);
                var lines = BuildWaitingLinesForSlice(definition, readiness, jobsById, activityIdById);
                if (lines.Count > 0)
                {
                    map[status.SliceStartUtc.ToUniversalTime()] = lines;
                }
            }

            return map;
        }

        private static IReadOnlyList<SliceHistoryTooltipLine> BuildWaitingLinesForSlice(
            JobDefinition definition,
            DependencyReadiness readiness,
            IReadOnlyDictionary<string, JobDefinition> jobsById,
            IReadOnlyDictionary<string, string> activityIdById)
        {
            // Group the unmet upstream slices by upstream job, preserving first-seen order. Each stored slice
            // key parses to range.JobId (the upstream GUID, or the raw reference for an unresolved upstream).
            var rangesByUpstream = new Dictionary<string, List<SliceRange>>(StringComparer.Ordinal);
            var upstreamOrder = new List<string>();
            foreach (var missing in readiness.MissingSlices)
            {
                if (!SliceKey.TryParse(missing.Value, out _, out var range))
                {
                    continue;
                }

                if (!rangesByUpstream.TryGetValue(range.JobId, out var ranges))
                {
                    ranges = new List<SliceRange>();
                    rangesByUpstream.Add(range.JobId, ranges);
                    upstreamOrder.Add(range.JobId);
                }

                ranges.Add(range);
            }

            if (upstreamOrder.Count == 0)
            {
                return BuildFallbackWaitingLines(definition, jobsById, activityIdById);
            }

            var lines = new List<SliceHistoryTooltipLine>();
            foreach (var upstreamId in upstreamOrder)
            {
                var name = ResolveJobName(upstreamId, jobsById, activityIdById);
                foreach (var window in MergeContiguousRanges(rangesByUpstream[upstreamId]))
                {
                    lines.Add(new SliceHistoryTooltipLine(
                        "Waiting on",
                        $"{name} [{AppFormatting.Iso(window.StartUtc)} - {AppFormatting.Iso(window.EndUtc)}]"));
                }
            }

            return CapWaitingLines(lines);
        }

        // Merges adjacent or overlapping [start, end) windows of one upstream into the fewest ranges.
        private static IReadOnlyList<SliceRange> MergeContiguousRanges(List<SliceRange> ranges)
        {
            var merged = new List<SliceRange>();
            foreach (var range in ranges.OrderBy(r => r.StartUtc))
            {
                if (merged.Count > 0 && range.StartUtc <= merged[^1].EndUtc)
                {
                    if (range.EndUtc > merged[^1].EndUtc)
                    {
                        merged[^1] = merged[^1] with { EndUtc = range.EndUtc };
                    }
                }
                else
                {
                    merged.Add(range);
                }
            }

            return merged;
        }

        private static IReadOnlyList<SliceHistoryTooltipLine> CapWaitingLines(List<SliceHistoryTooltipLine> lines)
        {
            if (lines.Count <= MaxWaitingOnTooltipLines)
            {
                return lines;
            }

            var capped = lines.Take(MaxWaitingOnTooltipLines).ToList();
            var remaining = lines.Count - MaxWaitingOnTooltipLines;
            capped.Add(new SliceHistoryTooltipLine("Waiting on", $"+{remaining.ToString(CultureInfo.InvariantCulture)} more upstream slices"));
            return capped;
        }

        // Last-resort line when the evaluator reports no concrete missing slices (e.g. an upstream id that no
        // longer resolves): name the declared upstream dependencies, else a generic placeholder.
        private static IReadOnlyList<SliceHistoryTooltipLine> BuildFallbackWaitingLines(
            JobDefinition definition,
            IReadOnlyDictionary<string, JobDefinition> jobsById,
            IReadOnlyDictionary<string, string> activityIdById)
        {
            var names = definition.DependsOn
                .Select(dependency => ResolveDependencyName(dependency, jobsById, activityIdById))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var value = names.Count > 0 ? string.Join(", ", names) : "upstream dependency";
            return new List<SliceHistoryTooltipLine> { new("Waiting on", value) };
        }

        private static string ResolveJobName(string jobId, IReadOnlyDictionary<string, JobDefinition> jobsById, IReadOnlyDictionary<string, string> activityIdById)
        {
            if (jobsById.TryGetValue(jobId, out var definition))
            {
                return definition.ActivityId;
            }

            return activityIdById.TryGetValue(jobId, out var activityId) ? activityId : jobId;
        }

        private static string ResolveDependencyName(DependentJob dependency, IReadOnlyDictionary<string, JobDefinition> jobsById, IReadOnlyDictionary<string, string> activityIdById)
        {
            if (!string.IsNullOrWhiteSpace(dependency.Id))
            {
                if (jobsById.TryGetValue(dependency.Id, out var definition))
                {
                    return definition.ActivityId;
                }

                if (activityIdById.TryGetValue(dependency.Id, out var activityId))
                {
                    return activityId;
                }
            }

            return dependency.ActivityId ?? dependency.Id ?? "upstream dependency";
        }

        private static IReadOnlyDictionary<DateTimeOffset, DurableWorkItem> ActiveQueueByStart(IEnumerable<DurableWorkItem> queueItems) =>
            queueItems
                .Where(q => q.State is DurableWorkQueueState.Queued or DurableWorkQueueState.Leased)
                .GroupBy(q => q.SliceStartUtc.ToUniversalTime())
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(q => q.State == DurableWorkQueueState.Leased)
                        .ThenBy(q => q.AvailableAtUtc)
                        .ThenBy(q => q.CreatedAtUtc)
                        .First());

        private static int DisplayAttempt(SliceStatusReadout? status, DurableWorkItem? activeQueueItem) =>
            Math.Max(Math.Max(status?.Attempt ?? 0, status?.SuccessfulAttempt ?? 0), activeQueueItem?.Attempts ?? 0);

        // A queue lease is "orphaned" once it is still Leased but its lock has already expired: the prior
        // worker neither completed nor released it (e.g. it faulted or the process was killed). Such a
        // slice renders distinctly from a healthy Running slice and offers an operator recovery action.
        private static bool IsOrphanedLease(DurableWorkItem? activeQueueItem, DateTimeOffset now) =>
            activeQueueItem is { State: DurableWorkQueueState.Leased }
            && activeQueueItem.LockedUntilUtc is { } lockedUntil
            && lockedUntil.ToUniversalTime() <= now.ToUniversalTime();

        private static string VisualState(SliceStatusReadout? status, DurableWorkItem? activeQueueItem, JobDefinition definition, DateTimeOffset sliceStart, DateTimeOffset sliceEnd, DateTimeOffset now)
        {
            var durableState = status?.Status;
            if (string.Equals(durableState, "Completed", StringComparison.Ordinal))
            {
                return IsCompletedAfterRetry(status) ? "CompletedAfterRetry" : "Completed";
            }

            if (string.Equals(durableState, "DeadLettered", StringComparison.Ordinal))
            {
                return "DeadLettered";
            }

            if (string.Equals(durableState, "DependencyBlocked", StringComparison.Ordinal))
            {
                return "DependencyBlocked";
            }

            if (string.Equals(durableState, "Running", StringComparison.Ordinal) || activeQueueItem?.State == DurableWorkQueueState.Leased)
            {
                return IsOrphanedLease(activeQueueItem, now) ? "Stalled" : "Running";
            }

            if (activeQueueItem?.State == DurableWorkQueueState.Queued)
            {
                return "Queued";
            }

            if (string.Equals(durableState, "Queued", StringComparison.Ordinal))
            {
                return "Queued";
            }

            if (string.Equals(durableState, "Failed", StringComparison.Ordinal))
            {
                return "Failed";
            }

            if (durableState is null || string.Equals(durableState, "Missing", StringComparison.Ordinal))
            {
                return MissingState(definition, sliceStart, sliceEnd, now);
            }

            return durableState;
        }

        private static bool IsCompletedAfterRetry(SliceStatusReadout? status)
        {
            if (status is null)
            {
                return false;
            }

            if (status.SuccessfulAttempt is { } successfulAttempt)
            {
                return successfulAttempt > 1;
            }

            return status.Attempt > 1;
        }

        private static string MissingState(JobDefinition definition, DateTimeOffset sliceStart, DateTimeOffset sliceEnd, DateTimeOffset now)
        {
            if (sliceStart < definition.StartFrom.ToUniversalTime())
            {
                return "NotEligible";
            }

            if (definition.EndOn is { } endOn && sliceEnd > endOn.ToUniversalTime())
            {
                return "NotEligible";
            }

            return sliceEnd > now.ToUniversalTime().Subtract(definition.DelayFromUtcNow)
                ? "NotYetEligible"
                : "WaitingToSchedule";
        }

        private static TimeSpan? FixedRowSpanFor(TimeSpan queryWindow)
        {
            if (queryWindow <= TimeSpan.Zero)
            {
                return null;
            }

            return queryWindow < HourSliceHistoryRowSpan
                ? HourSliceHistoryRowSpan
                : queryWindow < DaySliceHistoryRowSpan
                    ? DaySliceHistoryRowSpan
                    : null;
        }

        private static DateTimeOffset FloorToRowSpan(DateTimeOffset value, TimeSpan rowSpan)
        {
            var utcTicks = value.ToUniversalTime().UtcDateTime.Ticks;
            return new DateTimeOffset(utcTicks - (utcTicks % rowSpan.Ticks), TimeSpan.Zero);
        }

        private static DateTimeOffset CeilingToRowSpan(DateTimeOffset value, TimeSpan rowSpan)
        {
            var utc = value.ToUniversalTime();
            var floor = FloorToRowSpan(utc, rowSpan);
            return utc == floor ? floor : floor.Add(rowSpan);
        }

        private static string RowLabel(DateTimeOffset row, TimeSpan rowSpan) =>
            rowSpan == DaySliceHistoryRowSpan
                ? row.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                : row.ToString("yyyy-MM-dd HH:00", System.Globalization.CultureInfo.InvariantCulture);

        private static DateTimeOffset FirstSliceStartAtOrAfter(DateTimeOffset target, DateTimeOffset anchor, TimeSpan queryWindow)
        {
            target = target.ToUniversalTime();
            anchor = anchor.ToUniversalTime();
            return anchor.AddTicks(CeilingDiv(target.Ticks - anchor.Ticks, queryWindow.Ticks) * queryWindow.Ticks);
        }

        private static long CeilingDiv(long numerator, long denominator)
        {
            var quotient = Math.DivRem(numerator, denominator, out var remainder);
            return remainder == 0 || numerator < 0 ? quotient : quotient + 1;
        }
    }
}
