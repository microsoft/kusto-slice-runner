using System.Net;
using System.Text;
using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Observability;
using KoLite.Local.Sqlite.Schema;
using KoLite.Local.Sqlite.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class AgentOperationsApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "agent-operations-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public AgentOperationsApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "operations.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Logs_use_stable_cursor_pagination_without_duplicates()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var job = catalog.Create(Schedule("operations.logs"));
            var observability = new SqliteOperationalReadModelRepository(sqlite);
            for (var index = 0; index < 5; index++)
            {
                observability.RecordLog("Information", $"log-{index}", "worker", job.JobId);
                await Task.Delay(5);
            }

            using var client = factory.CreateClient();
            using var first = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/operations/logs?jobId={Guid.ParseExact(job.JobId, "N"):D}&limit=2"));
            var firstIds = first.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("logId").GetString()).ToArray();
            var cursor = first.RootElement.GetProperty("nextCursor").GetString();
            Assert.Equal(2, firstIds.Length);
            Assert.False(string.IsNullOrWhiteSpace(cursor));

            using var second = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/operations/logs?jobId={Guid.ParseExact(job.JobId, "N"):D}&limit=2&cursor={Uri.EscapeDataString(cursor!)}"));
            var secondIds = second.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("logId").GetString()).ToArray();
            Assert.Equal(2, secondIds.Length);
            Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));
        }

        [Fact]
        public async Task Cursor_is_bound_to_endpoint_filters_and_limits_are_not_coerced()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var one = catalog.Create(Schedule("operations.one"));
            var two = catalog.Create(Schedule("operations.two"));
            var observability = new SqliteOperationalReadModelRepository(sqlite);
            observability.RecordLog("Information", "one-a", "worker", one.JobId);
            observability.RecordLog("Information", "one-b", "worker", one.JobId);
            observability.RecordLog("Information", "two-a", "worker", two.JobId);

            using var client = factory.CreateClient();
            using var first = JsonDocument.Parse(await client.GetStringAsync($"/api/v1/operations/logs?jobId={Guid.ParseExact(one.JobId, "N"):D}&limit=1"));
            var cursor = first.RootElement.GetProperty("nextCursor").GetString();

            using var reused = await client.GetAsync($"/api/v1/operations/logs?jobId={Guid.ParseExact(two.JobId, "N"):D}&limit=1&cursor={Uri.EscapeDataString(cursor!)}");
            Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
            using var reusedBody = JsonDocument.Parse(await reused.Content.ReadAsStringAsync());
            Assert.Equal("invalid-cursor", reusedBody.RootElement.GetProperty("code").GetString());

            using var invalidLimit = await client.GetAsync("/api/v1/operations/logs?limit=1001");
            Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);

            using var invalidQueueLimit = await client.GetAsync("/api/v1/operations/queue?limit=1001");
            Assert.Equal(HttpStatusCode.BadRequest, invalidQueueLimit.StatusCode);

            using var malformed = await client.GetAsync("/api/v1/operations/logs?cursor=not-base64");
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        }

        [Fact]
        public async Task Logs_cursor_uses_log_id_as_a_stable_timestamp_tie_breaker()
        {
            var job = new SqliteJobCatalogRepository(sqlite).Create(Schedule("operations.tie"));
            var timestamp = "2026-01-01T00:00:00.0000000Z";
            foreach (var logId in new[] { "log-c", "log-b", "log-a" })
            {
                using var connection = sqlite.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO operational_logs
                        (log_id,job_id,level,message,properties_json,recorded_at_utc)
                    VALUES ($id,$job,'Information',$id,'{}',$time);
                    """;
                command.Parameters.AddWithValue("$id", logId);
                command.Parameters.AddWithValue("$job", job.JobId);
                command.Parameters.AddWithValue("$time", timestamp);
                command.ExecuteNonQuery();
            }

            using var client = factory.CreateClient();
            var collected = new List<string>();
            string? cursor = null;
            do
            {
                var route = $"/api/v1/operations/logs?jobId={Guid.ParseExact(job.JobId, "N"):D}&limit=2";
                if (cursor is not null)
                {
                    route += "&cursor=" + Uri.EscapeDataString(cursor);
                }

                using var page = JsonDocument.Parse(await client.GetStringAsync(route));
                collected.AddRange(page.RootElement.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("logId").GetString()!));
                cursor = page.RootElement.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                    ? null
                    : page.RootElement.GetProperty("nextCursor").GetString();
            } while (cursor is not null);

            Assert.Equal(new[] { "log-c", "log-b", "log-a" }, collected);
        }

        [Fact]
        public async Task Invalid_filters_and_offsetless_instants_are_rejected()
        {
            using var client = factory.CreateClient();
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.GetAsync("/api/v1/operations/slices?state=not-a-state")).StatusCode);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.GetAsync("/api/v1/operations/logs?from=2026-01-01T00:00:00")).StatusCode);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.GetAsync("/api/v1/operations/queue?jobId=activity-name")).StatusCode);
        }

        [Fact]
        public async Task Running_slices_use_cursor_pagination_without_truncation()
        {
            var job = new SqliteJobCatalogRepository(sqlite).Create(Schedule("operations.running"));
            var state = new SqliteSliceStateRepository(sqlite);
            for (var index = 0; index < 3; index++)
            {
                state.AcquireLease(
                    $"running-{index}",
                    job.JobId,
                    At(index * 5),
                    At((index + 1) * 5),
                    $"worker-{index}",
                    TimeSpan.FromMinutes(22),
                    At(30));
            }

            using var client = factory.CreateClient();
            var route = $"/api/v1/operations/running-slices?jobId={Guid.ParseExact(job.JobId, "N"):D}&limit=2";
            using var first = JsonDocument.Parse(await client.GetStringAsync(route));
            var firstItems = first.RootElement.GetProperty("items").EnumerateArray().ToArray();
            var cursor = first.RootElement.GetProperty("nextCursor").GetString();
            Assert.Equal(2, firstItems.Length);
            Assert.False(string.IsNullOrWhiteSpace(cursor));

            using var second = JsonDocument.Parse(
                await client.GetStringAsync(route + "&cursor=" + Uri.EscapeDataString(cursor!)));
            var secondItems = second.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(secondItems);
            Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextCursor").ValueKind);

            var firstStarts = firstItems.Select(item => item.GetProperty("sliceStartUtc").GetDateTimeOffset());
            var secondStarts = secondItems.Select(item => item.GetProperty("sliceStartUtc").GetDateTimeOffset());
            Assert.Equal(3, firstStarts.Concat(secondStarts).Distinct().Count());
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

        private static DateTimeOffset At(int minute)
        {
            return new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(minute);
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
