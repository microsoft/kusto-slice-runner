using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Schema;
using Microsoft.Data.Sqlite;

namespace KoLite.Local.Sqlite.Tests
{
    // Issue #3: soft-delete must be blocked by default when a job still has active downstream
    // dependents, must succeed when forced, and dependents that are themselves soft-deleted must not
    // count (they are already hidden from the active catalog and cannot break).
    public sealed class SoftDeleteDependentsTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "soft-delete-dependents-tests", Guid.NewGuid().ToString("N"));
        private readonly KoLiteSqliteConnectionFactory factory;
        private readonly SqliteJobCatalogRepository catalog;

        public SoftDeleteDependentsTests()
        {
            Directory.CreateDirectory(testDirectory);
            factory = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(Path.Combine(testDirectory, "dependents.db")) { BusyTimeoutMilliseconds = 10_000 });
            new KoLiteSqliteSchema(factory).EnsureSchema();
            catalog = new SqliteJobCatalogRepository(factory);
        }

        [Fact]
        public void FindDependents_returns_other_jobs_that_reference_the_upstream_by_id()
        {
            catalog.Create(Schedule("upstream"));
            catalog.Create(Schedule("downstream", dependsOn: "upstream"));
            catalog.Create(Schedule("unrelated"));

            var dependents = catalog.FindDependents(JobId("upstream"));

            Assert.Single(dependents);
            Assert.Equal(JobId("downstream"), dependents[0].JobId);
            Assert.Equal("downstream", dependents[0].ActivityId);
            Assert.Empty(catalog.FindDependents(JobId("downstream")));
            Assert.Empty(catalog.FindDependents(JobId("unrelated")));
        }

        [Fact]
        public void GetActiveDependents_excludes_dependents_that_are_themselves_soft_deleted()
        {
            catalog.Create(Schedule("upstream"));
            var downstreamA = catalog.Create(Schedule("downstream.a", dependsOn: "upstream"));
            catalog.Create(Schedule("downstream.b", dependsOn: "upstream"));

            Assert.Equal(2, Service().GetActiveDependents(JobId("upstream")).Count);

            // Nothing depends on downstream.a, so it can be soft-deleted without force; it then drops
            // out of the active-dependents set for upstream.
            Service().SoftDelete(JobId("downstream.a"), downstreamA.CatalogVersion, "tester", "hide one dependent");

            var active = Service().GetActiveDependents(JobId("upstream"));
            Assert.Single(active);
            Assert.Equal(JobId("downstream.b"), active[0].JobId);
        }

        [Fact]
        public void SoftDelete_throws_when_active_dependents_and_not_forced_and_leaves_job_enabled()
        {
            var upstream = catalog.Create(Schedule("upstream"));
            catalog.Create(Schedule("downstream", dependsOn: "upstream"));

            var ex = Assert.Throws<DownstreamDependentsException>(() =>
                Service().SoftDelete(JobId("upstream"), upstream.CatalogVersion, "tester", "should be blocked"));

            Assert.Equal(JobId("upstream"), ex.JobId);
            Assert.Single(ex.Dependents);
            Assert.Equal("downstream", ex.Dependents[0].ActivityId);
            Assert.Contains("downstream", ex.Message);

            // Blocked before any state change: still enabled, same catalog version, no SoftDeleted event.
            var current = catalog.Get(JobId("upstream"));
            Assert.NotNull(current);
            Assert.True(current!.IsEnabled);
            Assert.Equal(upstream.CatalogVersion, current!.CatalogVersion);
            Assert.DoesNotContain(ReadLifecycleEventTypes(JobId("upstream")), e => e == "SoftDeleted");
        }

        [Fact]
        public void SoftDelete_with_force_proceeds_past_active_dependents()
        {
            var upstream = catalog.Create(Schedule("upstream"));
            catalog.Create(Schedule("downstream", dependsOn: "upstream"));

            var deleted = Service().SoftDelete(JobId("upstream"), upstream.CatalogVersion, "tester", "force past dependents", force: true);

            Assert.False(deleted.IsEnabled);
            Assert.DoesNotContain(catalog.List(enabledOnly: true), j => j.JobId == JobId("upstream"));
            Assert.Contains(ReadLifecycleEventTypes(JobId("upstream")), e => e == "SoftDeleted");
        }

        public void Dispose()
        {
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private SqliteJobLifecycleService Service() => new(factory, catalog);

        private IReadOnlyList<string> ReadLifecycleEventTypes(string jobId)
        {
            using var c = factory.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT event_type FROM job_lifecycle_events WHERE job_id=$job ORDER BY recorded_at_utc;";
            cmd.Parameters.AddWithValue("$job", jobId);
            using var r = cmd.ExecuteReader();
            var values = new List<string>();
            while (r.Read()) values.Add(r.GetString(0));
            return values;
        }

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string? dependsOn = null) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "DependentsFunction",
          "outputTable": "Output",
          "queryWindowSize": "00:05:00",
          "delayFromUtcNow": "00:00:00",
          "maxParallelism": 1,
          "queryTimeout": "00:01:00",
          "isPaused": false,
          "startFrom": "2026-01-01T00:00:00Z",
          "dependsOn": {{(dependsOn is null ? "[]" : $"[{{ \"activityId\": \"{dependsOn}\" }}]")}},
          "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
        }
        """;
    }
}
