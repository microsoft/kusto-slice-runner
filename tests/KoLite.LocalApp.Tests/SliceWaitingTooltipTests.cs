using KoLite.Local.Core.Scheduling;
using KoLite.Local.Core.Time;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Queue;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Ui;
using Microsoft.Data.Sqlite;

namespace KoLite.LocalApp.Tests
{
    // Verifies the "Waiting on <upstream> [range]" slice-history tooltip lines that JobDetailsPageQuery
    // attaches to dependency-blocked slices. The harness seeds the slice state directly (no scheduler run)
    // so the scenario is deterministic: a DependencyBlocked downstream slice whose upstream slice has not
    // completed must surface a tooltip line naming the upstream job and the missing upstream window.
    public sealed class SliceWaitingTooltipTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "slice-waiting-tooltip-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly SqliteSliceStateRepository sliceState;
        private readonly SqliteOperationalReadModelRepository readModels;
        private readonly SqliteWorkQueueRepository queue;

        public SliceWaitingTooltipTests()
        {
            Directory.CreateDirectory(testDirectory);
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "tooltip.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteMigrator(sqlite).Migrate();
            catalog = new SqliteJobCatalogRepository(sqlite);
            sliceState = new SqliteSliceStateRepository(sqlite);
            readModels = new SqliteOperationalReadModelRepository(sqlite);
            queue = new SqliteWorkQueueRepository(sqlite);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }

        [Fact]
        public void Dependency_blocked_slice_tooltip_names_upstream_job_and_missing_range()
        {
            // Upstream has no dependency; downstream depends on the upstream by its GUID id.
            catalog.Create(Schedule("ingest.hourly"));
            catalog.Create(Schedule("rollup.hourly", dependsOnIds: [JobId("ingest.hourly")]));

            var start = At(0);
            var end = At(5);

            // Park the downstream slice DependencyBlocked. The upstream slice is intentionally NOT completed,
            // so the readiness evaluator reports the upstream [start, end) window as missing.
            sliceState.Append(
                "blocked-op",
                JobId("rollup.hourly"),
                start,
                end,
                DurableSliceStatus.DependencyBlocked,
                expectedVersion: 0,
                reason: SliceKey.Create(JobId("ingest.hourly"), start, end).Value);

            var data = CreateQuery().Get(JobId("rollup.hourly"));

            Assert.NotNull(data);
            var cell = data!.SliceHistory.SelectMany(row => row.Cells).Single(c => c.SliceStartUtc == start);
            Assert.Equal("DependencyBlocked", cell.State);

            var waitingLine = Assert.Single(cell.TooltipLines, line => line.Label == "Waiting on");
            Assert.Contains("ingest.hourly", waitingLine.Value, StringComparison.Ordinal);
            Assert.Contains(AppFormatting.Iso(start), waitingLine.Value, StringComparison.Ordinal);
            Assert.Contains(AppFormatting.Iso(end), waitingLine.Value, StringComparison.Ordinal);
        }

        [Fact]
        public void Dependency_blocked_slice_tooltip_merges_contiguous_upstream_windows()
        {
            // Downstream window (10m) spans two upstream windows (5m), so a single downstream slice waits on
            // two adjacent upstream slices that must collapse into one contiguous range in the tooltip.
            catalog.Create(Schedule("ingest.fine", queryWindowSize: "00:05:00"));
            catalog.Create(Schedule("rollup.coarse", queryWindowSize: "00:10:00", dependsOnIds: [JobId("ingest.fine")]));

            var start = At(0);
            var end = At(10);

            sliceState.Append(
                "blocked-merge-op",
                JobId("rollup.coarse"),
                start,
                end,
                DurableSliceStatus.DependencyBlocked,
                expectedVersion: 0);

            var data = CreateQuery().Get(JobId("rollup.coarse"));

            Assert.NotNull(data);
            var cell = data!.SliceHistory.SelectMany(row => row.Cells).Single(c => c.SliceStartUtc == start);
            Assert.Equal("DependencyBlocked", cell.State);

            var waitingLine = Assert.Single(cell.TooltipLines, line => line.Label == "Waiting on");
            Assert.Contains("ingest.fine", waitingLine.Value, StringComparison.Ordinal);
            Assert.Contains(AppFormatting.Iso(At(0)), waitingLine.Value, StringComparison.Ordinal);
            Assert.Contains(AppFormatting.Iso(At(10)), waitingLine.Value, StringComparison.Ordinal);
            // The intermediate boundary (00:05) must not appear: the two upstream windows are merged.
            Assert.DoesNotContain(AppFormatting.Iso(At(5)), waitingLine.Value, StringComparison.Ordinal);
        }

        private JobDetailsPageQuery CreateQuery() => new(
            catalog,
            readModels,
            queue,
            new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)),
            new OperationalDetailsReadModel(new SqliteOperationalReadModelRepository(sqlite)),
            new ManualClock(At(60)),
            sliceState);

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string queryWindowSize = "00:05:00", IReadOnlyList<string>? dependsOnIds = null)
        {
            var dependsOn = dependsOnIds is { Count: > 0 }
                ? "[" + string.Join(", ", dependsOnIds.Select(id => $"{{ \"id\": \"{id}\" }}")) + "]"
                : "[]";

            return $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "TooltipFunction",
              "outputTable": "TooltipOutput",
              "queryWindowSize": "{{queryWindowSize}}",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:01:00",
              "isPaused": false,
              "startFrom": "2026-01-01T00:00:00Z",
              "dependsOn": {{dependsOn}},
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;
        }
    }
}
