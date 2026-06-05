using System.Globalization;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;

namespace KoLite.LocalApp.Ui
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
        public string AccessibleLabel => $"{AppFormatting.Iso(SliceStartUtc)} to {AppFormatting.Iso(SliceEndUtc)}; {StatusLabel}; attempt {Attempt}";
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
        bool HasStarted);

    public sealed record SliceDetailsPageData(
        JobCatalogRecord Job,
        DateTimeOffset SliceStartUtc,
        DateTimeOffset SliceEndUtc,
        SliceStatusReadout? Status,
        IReadOnlyList<DurableWorkItem> QueueItems,
        IReadOnlyList<SliceAttemptReadout> Attempts,
        IReadOnlyList<OperationalLogReadout> Logs,
        IReadOnlyList<SliceEventReadout> Events);

    public sealed class JobDetailsPageQuery(
        SqliteJobCatalogRepository catalog,
        SqliteOperationalReadModelRepository readModels,
        SqliteWorkQueueRepository queue,
        LifecycleReadModel lifecycle,
        OperationalDetailsReadModel operationalDetails)
    {
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
            var sliceHistory = BuildSliceHistory(job.Definition, statuses, queueItems, fromUtc, toUtc, sliceHistoryCellLimit);
            return new JobDetailsPageData(
                job,
                job.Definition,
                summary,
                lifecycleState,
                CatalogHistoryDiffBuilder.Build(catalog.History(jobId)),
                sliceHistory.Rows,
                sliceHistory.Range,
                statuses,
                queueItems,
                operationalDetails.GetAttempts(jobId, take: 25),
                operationalDetails.GetLogs(jobId, take: 25),
                operationalDetails.GetEvents(jobId, take: 25),
                catalog.HasStarted(jobId));
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

            return new SliceDetailsPageData(
                job,
                sliceStartUtc,
                sliceEndUtc,
                status,
                queue.List(jobId).Where(q => q.SliceStartUtc == sliceStartUtc && q.SliceEndUtc == sliceEndUtc).ToList(),
                operationalDetails.GetAttempts(jobId, sliceStartUtc, sliceEndUtc, 100),
                operationalDetails.GetLogs(jobId, sliceStartUtc, sliceEndUtc, 100),
                operationalDetails.GetEvents(jobId, sliceStartUtc, sliceEndUtc, 100));
        }

        private static SliceHistoryBuildResult BuildSliceHistory(JobDefinition definition, IReadOnlyList<SliceStatusReadout> statuses, IReadOnlyList<DurableWorkItem> queueItems, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, int cellLimit)
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
                return BuildFixedSpanSliceHistory(definition, statusByStart, activeQueueByStart, start, end, now, cellLimit, rowSpan);
            }

            return BuildSequentialSliceHistory(definition, statusByStart, activeQueueByStart, start, end, now, cellLimit);
        }

        private static SliceHistoryBuildResult BuildFixedSpanSliceHistory(
            JobDefinition definition,
            IReadOnlyDictionary<DateTimeOffset, SliceStatusReadout> statusByStart,
            IReadOnlyDictionary<DateTimeOffset, DurableWorkItem> activeQueueByStart,
            DateTimeOffset start,
            DateTimeOffset end,
            DateTimeOffset now,
            int cellLimit,
            TimeSpan rowSpan)
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
                    cells.Add(BuildSliceHistoryCell(definition, statusByStart, activeQueueByStart, firstSliceStart.AddTicks(definition.QueryWindowSize.Ticks * i), now));
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
            int cellLimit)
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
                cells.Add(BuildSliceHistoryCell(definition, statusByStart, activeQueueByStart, cursor, now));
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
            DateTimeOffset now)
        {
            sliceStart = sliceStart.ToUniversalTime();
            var sliceEnd = sliceStart.Add(definition.QueryWindowSize);
            statusByStart.TryGetValue(sliceStart, out var status);
            activeQueueByStart.TryGetValue(sliceStart, out var activeQueueItem);
            var state = VisualState(status, activeQueueItem, definition, sliceStart, sliceEnd, now);
            var attempt = DisplayAttempt(status, activeQueueItem);
            var css = AppFormatting.StateCss(state);
            var statusLabel = AppFormatting.StatusLabel(state);
            var url = $"/jobs/{Uri.EscapeDataString(definition.ActivityId)}/slices?start={Uri.EscapeDataString(AppFormatting.Iso(sliceStart))}&end={Uri.EscapeDataString(AppFormatting.Iso(sliceEnd))}";
            var tooltipLines = BuildTooltipLines(sliceStart, sliceEnd, statusLabel, attempt, activeQueueItem);
            return new SliceHistoryCell(sliceStart, sliceEnd, state, attempt, css, statusLabel, url, tooltipLines);
        }

        private static IReadOnlyList<SliceHistoryTooltipLine> BuildTooltipLines(DateTimeOffset sliceStart, DateTimeOffset sliceEnd, string statusLabel, int attempt, DurableWorkItem? activeQueueItem)
        {
            var lines = new List<SliceHistoryTooltipLine>
            {
                new("Start", AppFormatting.Iso(sliceStart)),
                new("End", AppFormatting.Iso(sliceEnd)),
                new("Status", statusLabel),
                new("Attempt", attempt.ToString(CultureInfo.InvariantCulture))
            };

            if (activeQueueItem is not null)
            {
                lines.Add(new SliceHistoryTooltipLine("Queue", AppFormatting.StatusLabel(activeQueueItem.State.ToString())));
                lines.Add(new SliceHistoryTooltipLine("Available", AppFormatting.Iso(activeQueueItem.AvailableAtUtc)));
                lines.Add(new SliceHistoryTooltipLine(
                    "Queue attempts",
                    $"{activeQueueItem.Attempts.ToString(CultureInfo.InvariantCulture)}/{activeQueueItem.MaxAttempts.ToString(CultureInfo.InvariantCulture)}"));
            }

            return lines;
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
                return "Running";
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
