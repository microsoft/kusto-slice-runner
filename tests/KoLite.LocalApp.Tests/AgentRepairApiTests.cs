using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class AgentRepairApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "agent-repair-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public AgentRepairApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "repair.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Preview_then_create_returns_accepted_with_a_durable_location()
        {
            var job = new SqliteJobCatalogRepository(sqlite).Create(Schedule("repair.v1"));
            new SqliteSliceStateRepository(sqlite).Append(
                "failed",
                job.JobId,
                At(0),
                At(5),
                DurableSliceStatus.Failed,
                expectedVersion: 0,
                reason: "boom");

            using var client = factory.CreateClient();
            var route = $"/api/v1/jobs/{Guid.ParseExact(job.JobId, "N"):D}";
            using var preview = await client.PostAsJsonAsync(
                route + "/repair-previews",
                new { from = At(0).ToString("O"), to = At(5).ToString("O") });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.Equal(1, previewBody.RootElement.GetProperty("repairableSliceCount").GetInt32());

            using var create = await client.PostAsJsonAsync(
                route + "/repairs",
                new
                {
                    from = At(0).ToString("O"),
                    to = At(5).ToString("O"),
                    reason = "close gap",
                    expectedSliceCount = 1
                });
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            Assert.StartsWith("/api/v1/operations/repairs/", create.Headers.Location?.OriginalString, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Repair_preserves_preview_conflicts_and_requires_explicit_offsets()
        {
            var job = new SqliteJobCatalogRepository(sqlite).Create(Schedule("repair.guards"));
            new SqliteSliceStateRepository(sqlite).Append(
                "failed",
                job.JobId,
                At(0),
                At(5),
                DurableSliceStatus.DeadLettered,
                expectedVersion: 0,
                reason: "boom");
            using var client = factory.CreateClient();
            var route = $"/api/v1/jobs/{Guid.ParseExact(job.JobId, "N"):D}";

            using var stale = await client.PostAsJsonAsync(
                route + "/repairs",
                new
                {
                    from = At(0).ToString("O"),
                    to = At(5).ToString("O"),
                    reason = "stale",
                    expectedSliceCount = 9
                });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
            Assert.Equal("repair-preview-conflict", staleBody.RootElement.GetProperty("code").GetString());

            using var offsetless = await client.PostAsJsonAsync(
                route + "/repair-previews",
                new { from = "2026-01-01T00:00:00", to = "2026-01-01T00:05:00" });
            Assert.Equal(HttpStatusCode.BadRequest, offsetless.StatusCode);
        }

        private WebApplicationFactory<Program> CreateFactory()
        {
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                    ["KoLite:Scheduler:Enabled"] = "false",
                    ["KoLite:UpdateCheck:Enabled"] = "false"
                }));
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.ClearProviders());
                    services.AddTestLocalRequestPolicy();
                });
            });
        }

        private static string Schedule(string activityId)
        {
            return $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "BuildThing",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:01:00",
              "startFrom": "2026-01-01T00:00:00Z",
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;
        }

        private static string JobId(string value)
        {
            return new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value))).ToString("N");
        }

        private static DateTimeOffset At(int minutes)
        {
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minutes);
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
