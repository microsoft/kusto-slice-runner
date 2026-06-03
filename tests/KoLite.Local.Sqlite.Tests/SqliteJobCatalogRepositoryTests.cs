using System.Text.Json;
using KoLite.Local.Core.Schedules;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.State;
using KoLite.Local.Core.Scheduling;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    public sealed class SqliteJobCatalogRepositoryTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "catalog-file-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository repository;

        public SqliteJobCatalogRepositoryTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "catalog.db")));
            new KoLiteSqliteMigrator(factory).Migrate();
            repository = new SqliteJobCatalogRepository(factory);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }

        [Fact]
        public void Create_validates_canonicalizes_and_audits_schedule_json()
        {
            var created = repository.Create(Schedule("job.catalog", paused: false), actor: "test");

            Assert.Equal("job.catalog", created.JobId);
            Assert.True(created.IsEnabled);
            Assert.Equal(1, created.CatalogVersion);
            Assert.DoesNotContain("\r", created.ScheduleJson);
            Assert.DoesNotContain("  ", created.ScheduleJson);
            Assert.Equal("CatalogFunction", created.Definition.FunctionName);
            var history = repository.History("job.catalog");
            Assert.Single(history);
            Assert.Equal("Created", history[0].EventType);
        }

        [Fact]
        public void Update_enable_disable_list_get_and_export_preserve_versions_and_events()
        {
            var created = repository.Create(Schedule("job.catalog", paused: false));
            var disabled = repository.SetEnabled(created.JobId, enabled: false, expectedVersion: created.CatalogVersion);
            var updated = repository.Update(created.JobId, Schedule("job.catalog", paused: true, maxParallelism: 3), expectedVersion: disabled.CatalogVersion);

            Assert.False(disabled.IsEnabled);
            Assert.False(updated.IsEnabled);
            Assert.Equal(3, updated.Definition.MaxParallelism);
            Assert.Empty(repository.List(enabledOnly: true));
            Assert.Equal(updated.ScheduleJson, repository.Get(created.JobId)!.ScheduleJson);
            Assert.Equal(updated.ScheduleJson, repository.Export(created.JobId));
            Assert.Equal(["Created", "Disabled", "Updated"], repository.History(created.JobId).Select(e => e.EventType).ToArray());
            Assert.Throws<InvalidOperationException>(() => repository.SetEnabled(created.JobId, enabled: true, expectedVersion: created.CatalogVersion));
        }

        [Fact]
        public void ExportAll_returns_importable_array_in_catalog_order_and_honors_exclusions()
        {
            repository.Create(Schedule("job.b", paused: false));
            repository.Create(Schedule("job.a", paused: true));
            repository.Create(Schedule("job.c", paused: false));

            var allJson = repository.ExportAll();
            var parsed = ScheduleImportParser.Parse(allJson);
            var errors = string.Join("; ", parsed.Errors.Select(e => $"{e.Field}: {e.Message}"));
            Assert.True(parsed.IsValid, errors);
            Assert.Equal(["job.a", "job.b", "job.c"], ActivityIds(allJson));

            var filteredJson = repository.ExportAll(new HashSet<string>(StringComparer.Ordinal) { "job.b" });

            Assert.Equal(["job.a", "job.c"], ActivityIds(filteredJson));
        }

        [Fact]
        public void Update_allows_window_and_start_changes_before_job_has_started()
        {
            var created = repository.Create(Schedule("job.catalog", paused: false));

            var updated = repository.Update(
                created.JobId,
                Schedule("job.catalog", paused: false, queryWindowSize: "00:10:00", startFrom: "2026-01-01T00:05:00Z"),
                expectedVersion: created.CatalogVersion);

            Assert.Equal(TimeSpan.FromMinutes(10), updated.Definition.QueryWindowSize);
            Assert.Equal(At(5), updated.Definition.StartFrom);
            Assert.Equal(2, updated.CatalogVersion);
        }

        [Fact]
        public void Update_rejects_window_and_start_changes_after_job_has_started()
        {
            var created = repository.Create(Schedule("job.catalog", paused: false));
            MarkStarted(created.JobId);

            var ex = Assert.Throws<InvalidOperationException>(() => repository.Update(
                created.JobId,
                Schedule("job.catalog", paused: false, queryWindowSize: "00:10:00", startFrom: "2026-01-01T00:05:00Z"),
                expectedVersion: created.CatalogVersion));

            Assert.Contains("queryWindowSize", ex.Message, StringComparison.Ordinal);
            Assert.Contains("startFrom", ex.Message, StringComparison.Ordinal);
            var stored = repository.Get(created.JobId)!;
            Assert.Equal(1, stored.CatalogVersion);
            Assert.Equal(TimeSpan.FromMinutes(5), stored.Definition.QueryWindowSize);
            Assert.Equal(At(0), stored.Definition.StartFrom);
            Assert.Equal(["Created"], repository.History(created.JobId).Select(e => e.EventType).ToArray());
        }

        [Fact]
        public void Update_allows_tag_changes_after_job_has_started()
        {
            var created = repository.Create(Schedule("job.catalog", paused: false));
            MarkStarted(created.JobId);

            var updated = repository.Update(
                created.JobId,
                Schedule("job.catalog", paused: false, tags: ["Prod", " daily ", "PROD"]),
                expectedVersion: created.CatalogVersion);

            Assert.Equal(2, updated.CatalogVersion);
            Assert.Equal(["prod", "daily"], updated.Definition.Tags);
            Assert.Contains("\"tags\":[\"prod\",\"daily\"]", updated.ScheduleJson, StringComparison.Ordinal);
        }

        [Fact]
        public void Create_import_and_export_normalize_schedule_tags()
        {
            var created = repository.Create(Schedule("job.tags", paused: false, tags: [" Prod ", "daily", "PROD"]));

            Assert.Equal(["prod", "daily"], created.Definition.Tags);
            Assert.Contains("\"tags\":[\"prod\",\"daily\"]", created.ScheduleJson, StringComparison.Ordinal);
            Assert.DoesNotContain(" Prod ", created.ScheduleJson, StringComparison.Ordinal);

            var result = repository.Import("[" + Schedule("job.tags", paused: false, tags: ["Security", "prod", "security"]) + "," + Schedule("job.tags.new", paused: false, tags: ["Daily"]) + "]", actor: "test-import");
            var updated = repository.Get("job.tags")!;
            var imported = repository.Get("job.tags.new")!;
            var exportAll = ScheduleImportParser.Parse(repository.ExportAll());

            Assert.Equal(1, result.Created);
            Assert.Equal(1, result.Updated);
            Assert.Equal(["security", "prod"], updated.Definition.Tags);
            Assert.Equal(["daily"], imported.Definition.Tags);
            Assert.Contains("\"tags\":[\"security\",\"prod\"]", repository.Export("job.tags"), StringComparison.Ordinal);
            Assert.True(exportAll.IsValid, string.Join(Environment.NewLine, exportAll.Errors.Select(e => $"{e.Field}: {e.Message}")));
            Assert.Equal(["security", "prod"], exportAll.Items.Single(item => item.Definition.ActivityId == "job.tags").Definition.Tags);
            Assert.Equal(["daily"], exportAll.Items.Single(item => item.Definition.ActivityId == "job.tags.new").Definition.Tags);
        }

        [Fact]
        public void Create_rejects_invalid_schedule_before_persistence()
        {
            Assert.Throws<InvalidOperationException>(() => repository.Create("{ \"activityId\": \"missing.required\" }"));
            Assert.Empty(repository.List());
        }

        [Fact]
        public void Import_creates_updates_and_leaves_omitted_jobs_untouched()
        {
            repository.Create(Schedule("job.existing", paused: false, maxParallelism: 1), actor: "seed");
            repository.Create(Schedule("job.omitted", paused: false, maxParallelism: 2), actor: "seed");

            var result = repository.Import("[" + Schedule("job.existing", paused: true, maxParallelism: 3) + "," + Schedule("job.new", paused: false, maxParallelism: 4) + "]", actor: "test-import");

            Assert.Equal(1, result.Created);
            Assert.Equal(1, result.Updated);
            Assert.Equal(2, result.Total);
            Assert.Equal(["Updated", "Created"], result.Items.Select(i => i.Action).ToArray());

            var existing = repository.Get("job.existing")!;
            var omitted = repository.Get("job.omitted")!;
            var created = repository.Get("job.new")!;
            Assert.Equal(2, existing.CatalogVersion);
            Assert.Equal(3, existing.Definition.MaxParallelism);
            Assert.False(existing.IsEnabled);
            Assert.Equal(1, omitted.CatalogVersion);
            Assert.Equal(2, omitted.Definition.MaxParallelism);
            Assert.True(omitted.IsEnabled);
            Assert.Equal(1, created.CatalogVersion);
            Assert.Equal(4, created.Definition.MaxParallelism);
            Assert.Equal(["Created", "Updated"], repository.History("job.existing").Select(e => e.EventType).ToArray());
            Assert.Equal(["Created"], repository.History("job.new").Select(e => e.EventType).ToArray());
        }

        [Fact]
        public void Import_rolls_back_full_batch_when_started_job_changes_protected_fields()
        {
            repository.Create(Schedule("job.existing", paused: false), actor: "seed");
            MarkStarted("job.existing");

            var ex = Assert.Throws<InvalidOperationException>(() => repository.Import(
                "[" +
                Schedule("job.new", paused: false, maxParallelism: 2) +
                "," +
                Schedule("job.existing", paused: false, queryWindowSize: "00:10:00", startFrom: "2026-01-01T00:05:00Z") +
                "]",
                actor: "test-import"));

            Assert.Contains("[1].queryWindowSize", ex.Message, StringComparison.Ordinal);
            Assert.Contains("[1].startFrom", ex.Message, StringComparison.Ordinal);
            Assert.Null(repository.Get("job.new"));
            var existing = repository.Get("job.existing")!;
            Assert.Equal(1, existing.CatalogVersion);
            Assert.Equal(TimeSpan.FromMinutes(5), existing.Definition.QueryWindowSize);
            Assert.Equal(At(0), existing.Definition.StartFrom);
            Assert.Equal(["Created"], repository.History("job.existing").Select(e => e.EventType).ToArray());
        }

        [Fact]
        public void Import_rolls_back_full_batch_when_any_item_is_invalid()
        {
            repository.Create(Schedule("job.keep", paused: false, maxParallelism: 1), actor: "seed");

            var ex = Assert.Throws<InvalidOperationException>(() => repository.Import("[" + Schedule("job.new", paused: false, maxParallelism: 2) + "," + InvalidSchedule("job.bad") + "]", actor: "test-import"));

            Assert.Contains("[1].outputTable", ex.Message, StringComparison.Ordinal);
            Assert.Null(repository.Get("job.new"));
            Assert.NotNull(repository.Get("job.keep"));
            Assert.Single(repository.List());
            Assert.Equal(["Created"], repository.History("job.keep").Select(e => e.EventType).ToArray());
        }

        private void MarkStarted(string activityId)
        {
            var state = new SqliteSliceStateRepository(factory);
            state.Append("started-" + activityId, activityId, At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
        }

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private static string[] ActivityIds(string scheduleArrayJson)
        {
            using var document = JsonDocument.Parse(scheduleArrayJson);
            Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
            return document.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("activityId").GetString() ?? string.Empty)
                .ToArray();
        }

        private static string Schedule(string activityId, bool paused, int maxParallelism = 1, string queryWindowSize = "00:05:00", string startFrom = "2026-01-01T00:00:00Z", IReadOnlyList<string>? tags = null)
        {
            var schedule = $$"""
            {
              "activityId": "{{activityId}}",
              "functionName": "CatalogFunction",
              "outputTable": "CatalogOutput",
              "queryWindowSize": "{{queryWindowSize}}",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": {{maxParallelism}},
              "queryTimeout": "00:01:00",
              "isPaused": {{paused.ToString().ToLowerInvariant()}},
              "startFrom": "{{startFrom}}",
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;

            if (tags is not { Count: > 0 })
            {
                return schedule;
            }

            return schedule.Replace("  \"target\":", $"  \"tags\": {JsonSerializer.Serialize(tags)},\n  \"target\":", StringComparison.Ordinal);
        }

        private static string InvalidSchedule(string activityId) => $$"""
        {
          "activityId": "{{activityId}}",
          "functionName": "CatalogFunction",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
