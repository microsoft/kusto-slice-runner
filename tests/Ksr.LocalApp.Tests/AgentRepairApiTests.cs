// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.State;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ksr.LocalApp.Tests
{
    public sealed class AgentRepairApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "agent-repair-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KsrSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public AgentRepairApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "repair.db");
            sqlite = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(databasePath));
            new KsrSqliteSchema(sqlite).EnsureSchema();
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
            var previewToken = previewBody.RootElement.GetProperty("previewToken").GetString();

            using var create = await client.PostAsJsonAsync(
                route + "/repairs",
                new
                {
                    from = At(0).ToString("O"),
                    to = At(5).ToString("O"),
                    reason = "close gap",
                    expectedSliceCount = 1,
                    previewToken
                });
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
            Assert.StartsWith("/api/v1/operations/repairs/", create.Headers.Location?.OriginalString, StringComparison.Ordinal);

            using var detail = await client.GetAsync(create.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        }

        [Fact]
        public async Task Repair_requires_the_preview_token_and_rejects_same_count_stale_approval()
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

            using var missingToken = await client.PostAsJsonAsync(
                route + "/repairs",
                new
                {
                    from = At(0).ToString("O"),
                    to = At(5).ToString("O"),
                    reason = "missing token",
                    expectedSliceCount = 1
                });
            Assert.Equal(HttpStatusCode.BadRequest, missingToken.StatusCode);
            using var missingTokenBody = JsonDocument.Parse(await missingToken.Content.ReadAsStringAsync());
            Assert.Equal("preview-token-required", missingTokenBody.RootElement.GetProperty("code").GetString());

            using var stale = await client.PostAsJsonAsync(
                route + "/repairs",
                new
                {
                    from = At(0).ToString("O"),
                    to = At(5).ToString("O"),
                    reason = "stale",
                    expectedSliceCount = 1,
                    previewToken = "stale-token"
                });
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using var staleBody = JsonDocument.Parse(await stale.Content.ReadAsStringAsync());
            Assert.Equal("repair-preview-conflict", staleBody.RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task Childless_repair_batch_has_a_durable_empty_detail_resource()
        {
            var job = new SqliteJobCatalogRepository(sqlite).Create(Schedule("repair.empty"));
            using var client = factory.CreateClient();
            var route = $"/api/v1/jobs/{Guid.ParseExact(job.JobId, "N"):D}";
            using var preview = await client.PostAsJsonAsync(
                route + "/repair-previews",
                new { from = At(0).ToString("O"), to = At(5).ToString("O") });
            using var previewBody = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
            Assert.Equal(0, previewBody.RootElement.GetProperty("repairableSliceCount").GetInt32());

            using var create = await client.PostAsJsonAsync(
                route + "/repairs",
                new
                {
                    from = At(0).ToString("O"),
                    to = At(5).ToString("O"),
                    reason = "confirm no gaps",
                    expectedSliceCount = 0,
                    previewToken = previewBody.RootElement.GetProperty("previewToken").GetString()
                });
            Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);

            using var detail = await client.GetAsync(create.Headers.Location);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            Assert.Empty(detailBody.RootElement.GetProperty("slices").EnumerateArray());
            Assert.Empty(detailBody.RootElement.GetProperty("chunks").EnumerateArray());
        }

        [Fact]
        public async Task Repair_preview_requires_explicit_offsets()
        {
            var job = new SqliteJobCatalogRepository(sqlite).Create(Schedule("repair.offsets"));
            using var client = factory.CreateClient();
            var route = $"/api/v1/jobs/{Guid.ParseExact(job.JobId, "N"):D}";
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
                    ["ConnectionStrings:KsrSqlite"] = databasePath,
                    ["Ksr:Scheduler:Enabled"] = "false",
                    ["Ksr:UpdateCheck:Enabled"] = "false"
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
              "target": { "clusterUri": "https://ksr-example.invalid", "database": "DemoDb" }
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
