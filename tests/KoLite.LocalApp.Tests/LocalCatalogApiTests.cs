using System.Net;
using System.Text;
using System.Text.Json;
using KoLite.Local.Core.Scheduling;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.State;
using KoLite.LocalApp.Api;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class LocalCatalogApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "localapi-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public LocalCatalogApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "api.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteMigrator(sqlite).Migrate();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Get_jobs_lists_seeded_jobs_with_summary_fields()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.active", "ActiveFunction", isPaused: false, tags: ["prod", "daily"]));
            catalog.Create(Schedule("job.paused", "PausedFunction", isPaused: true));
            var soft = catalog.Create(Schedule("job.soft", "SoftFunction", isPaused: false));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete("job.soft", soft.CatalogVersion, "api-test", "exclude");

            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/api/jobs"));
            var jobs = document.RootElement.GetProperty("jobs")
                .EnumerateArray()
                .ToDictionary(job => job.GetProperty("jobId").GetString()!, job => job);

            Assert.Equal(3, jobs.Count);

            var active = jobs["job.active"];
            Assert.True(active.GetProperty("isEnabled").GetBoolean());
            Assert.False(active.GetProperty("isPaused").GetBoolean());
            Assert.False(active.GetProperty("isSoftDeleted").GetBoolean());
            Assert.False(active.GetProperty("hasStarted").GetBoolean());
            Assert.Equal(1, active.GetProperty("catalogVersion").GetInt64());
            Assert.Equal("https://kolite-example.invalid", active.GetProperty("target").GetProperty("clusterUri").GetString());
            Assert.Equal("DemoDb", active.GetProperty("target").GetProperty("database").GetString());
            var tags = active.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).ToList();
            Assert.Equal(2, tags.Count);
            Assert.Contains("prod", tags);
            Assert.Contains("daily", tags);

            // A paused schedule is stored as not-enabled (the scheduler emits no work for it).
            Assert.False(jobs["job.paused"].GetProperty("isEnabled").GetBoolean());
            Assert.True(jobs["job.paused"].GetProperty("isPaused").GetBoolean());
            Assert.True(jobs["job.soft"].GetProperty("isSoftDeleted").GetBoolean());
        }

        [Fact]
        public async Task Get_job_returns_canonical_schedule_and_404_for_missing()
        {
            new SqliteJobCatalogRepository(sqlite).Create(Schedule("job.detail", "DetailFunction", isPaused: true));
            using var client = factory.CreateClient();

            using var found = JsonDocument.Parse(await client.GetStringAsync("/api/jobs/job.detail"));
            Assert.Equal("job.detail", found.RootElement.GetProperty("job").GetProperty("jobId").GetString());
            var schedule = found.RootElement.GetProperty("schedule");
            Assert.Equal("job.detail", schedule.GetProperty("activityId").GetString());
            Assert.Equal("DetailFunction", schedule.GetProperty("functionName").GetString());
            Assert.True(schedule.GetProperty("isPaused").GetBoolean());

            using var missing = await client.GetAsync("/api/jobs/job.missing");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var error = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Assert.Contains("does not exist", error.RootElement.GetProperty("error").GetString());
        }

        [Fact]
        public async Task Get_jobs_export_returns_import_compatible_array_excluding_soft_deleted()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.active", "ActiveFunction", isPaused: false));
            catalog.Create(Schedule("job.paused", "PausedFunction", isPaused: true));
            var soft = catalog.Create(Schedule("job.soft", "SoftFunction", isPaused: false));
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete("job.soft", soft.CatalogVersion, "api-test", "exclude");
            using var client = factory.CreateClient();

            using var response = await client.GetAsync("/api/jobs/export");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var ids = document.RootElement.EnumerateArray()
                .Select(item => item.GetProperty("activityId").GetString()!)
                .ToArray();
            Assert.Equal(["job.active", "job.paused"], ids);
        }

        [Fact]
        public async Task Post_import_creates_then_upserts_without_deleting_omitted_jobs()
        {
            using var client = factory.CreateClient();
            var catalog = new SqliteJobCatalogRepository(sqlite);

            using var created = await PostImport(client, Schedule("job.one", "OneFunction", isPaused: true));
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Assert.Equal(1, createdBody.RootElement.GetProperty("created").GetInt32());
            Assert.Equal(0, createdBody.RootElement.GetProperty("updated").GetInt32());
            Assert.Equal(1, createdBody.RootElement.GetProperty("total").GetInt32());
            Assert.Equal("OneFunction", catalog.Get("job.one")?.QueryRef);

            var array = "[" + Schedule("job.one", "OneFunctionV2", isPaused: true) + "," + Schedule("job.two", "TwoFunction", isPaused: true) + "]";
            using var upserted = await PostImport(client, array);
            Assert.Equal(HttpStatusCode.OK, upserted.StatusCode);
            using var upsertedBody = JsonDocument.Parse(await upserted.Content.ReadAsStringAsync());
            Assert.Equal(1, upsertedBody.RootElement.GetProperty("created").GetInt32());
            Assert.Equal(1, upsertedBody.RootElement.GetProperty("updated").GetInt32());
            Assert.Equal("OneFunctionV2", catalog.Get("job.one")?.QueryRef);
            Assert.NotNull(catalog.Get("job.two"));

            // A later single-job import must not delete the jobs it omits.
            using var additive = await PostImport(client, Schedule("job.three", "ThreeFunction", isPaused: true));
            Assert.Equal(HttpStatusCode.OK, additive.StatusCode);
            Assert.NotNull(catalog.Get("job.one"));
            Assert.NotNull(catalog.Get("job.two"));
            Assert.NotNull(catalog.Get("job.three"));
        }

        [Fact]
        public async Task Post_import_rejects_invalid_and_empty_bodies_with_400()
        {
            using var client = factory.CreateClient();

            using var malformed = await PostImport(client, "{ not valid json ");
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            using var malformedBody = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync());
            Assert.False(string.IsNullOrWhiteSpace(malformedBody.RootElement.GetProperty("error").GetString()));

            var unknownField = Schedule("job.unknown", "UnknownFunction", isPaused: true)
                .Replace("\"outputTable\": \"Output\",", "\"outputTable\": \"Output\",\n  \"bogus\": 1,", StringComparison.Ordinal);
            using var unknown = await PostImport(client, unknownField);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

            using var empty = await PostImport(client, "   ");
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

            Assert.Null(new SqliteJobCatalogRepository(sqlite).Get("job.unknown"));
        }

        [Fact]
        public async Task Post_import_rejects_immutable_field_change_on_started_job_with_400()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("job.started", "StartedFunction", isPaused: true));
            new SqliteSliceStateRepository(sqlite).Append("started-1", "job.started", At(0), At(5), DurableSliceStatus.Queued, expectedVersion: 0);
            using var client = factory.CreateClient();

            var tampered = Schedule("job.started", "StartedFunction", isPaused: true)
                .Replace("\"queryWindowSize\": \"00:05:00\"", "\"queryWindowSize\": \"00:10:00\"", StringComparison.Ordinal);
            using var response = await PostImport(client, tampered);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains("queryWindowSize", body.RootElement.GetProperty("error").GetString());
            Assert.Equal(TimeSpan.FromMinutes(5), catalog.Get("job.started")!.Definition.QueryWindowSize);
        }

        [Fact]
        public void LocalApiGuard_allows_loopback_and_blocks_remote_addresses()
        {
            Assert.True(LocalApiGuard.IsLoopback(null));
            Assert.True(LocalApiGuard.IsLoopback(IPAddress.Loopback));
            Assert.True(LocalApiGuard.IsLoopback(IPAddress.IPv6Loopback));
            Assert.True(LocalApiGuard.IsLoopback(IPAddress.Parse("127.0.0.9")));

            Assert.False(LocalApiGuard.IsLoopback(IPAddress.Parse("10.0.0.5")));
            Assert.False(LocalApiGuard.IsLoopback(IPAddress.Parse("192.168.1.10")));
        }

        private static Task<HttpResponseMessage> PostImport(HttpClient client, string json) =>
            client.PostAsync("/api/jobs/import", new StringContent(json, Encoding.UTF8, "application/json"));

        private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

        private WebApplicationFactory<Program> CreateFactory() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                        ["KoLite:Scheduler:Enabled"] = "false",
                        ["KoLite:UpdateCheck:Enabled"] = "false"
                    });
                });
                builder.ConfigureServices(services => services.AddLogging(logging => logging.ClearProviders()));
            });

        private static string Schedule(string activityId, string functionName, bool isPaused, IReadOnlyList<string>? tags = null)
        {
            var tagsLine = tags is { Count: > 0 }
                ? $"  \"tags\": {JsonSerializer.Serialize(tags)},\n"
                : string.Empty;
            return
                "{\n" +
                $"  \"activityId\": \"{activityId}\",\n" +
                $"  \"functionName\": \"{functionName}\",\n" +
                "  \"outputTable\": \"Output\",\n" +
                "  \"queryWindowSize\": \"00:05:00\",\n" +
                "  \"delayFromUtcNow\": \"00:00:00\",\n" +
                "  \"maxParallelism\": 1,\n" +
                "  \"queryTimeout\": \"00:01:00\",\n" +
                $"  \"isPaused\": {(isPaused ? "true" : "false")},\n" +
                "  \"startFrom\": \"2026-01-01T00:00:00Z\",\n" +
                tagsLine +
                "  \"target\": { \"clusterUri\": \"https://kolite-example.invalid\", \"database\": \"DemoDb\" }\n" +
                "}";
        }

        public void Dispose()
        {
            factory.Dispose();
            try
            {
                Directory.Delete(testDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the SQLite file may still be held briefly on Windows.
            }
        }
    }
}
