// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Schema;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class AgentJobsApiTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "agent-jobs-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public AgentJobsApiTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "jobs.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Create_get_list_and_replace_use_named_v1_contracts()
        {
            using var client = factory.CreateClient();
            using var created = await client.PostAsJsonAsync(
                "/api/v1/jobs",
                new { schedule = JsonDocument.Parse(Schedule("jobs.first", isPaused: true, description: "# purpose")).RootElement });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.NotNull(created.Headers.Location);
            Assert.NotNull(created.Headers.ETag);
            using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var job = createdBody.RootElement.GetProperty("job");
            var jobId = job.GetProperty("jobId").GetString()!;
            Assert.Equal("jobs.first", job.GetProperty("activityId").GetString());
            Assert.Equal("paused", job.GetProperty("lifecycleState").GetString());
            Assert.False(job.TryGetProperty("displayName", out _));
            Assert.Equal("# purpose", createdBody.RootElement.GetProperty("schedule").GetProperty("description").GetString());

            using var list = JsonDocument.Parse(await client.GetStringAsync("/api/v1/jobs?activityId=jobs.first"));
            Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());

            using var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/jobs/{Guid.ParseExact(jobId, "N"):D}")
            {
                Content = JsonContent.Create(new
                {
                    schedule = JsonDocument.Parse(Schedule("jobs.renamed", isPaused: true, id: jobId, maxParallelism: 4)).RootElement
                })
            };
            update.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
            using var replaced = await client.SendAsync(update);
            Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
            using var replacedBody = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
            Assert.Equal("jobs.renamed", replacedBody.RootElement.GetProperty("job").GetProperty("activityId").GetString());
            Assert.Equal(4, replacedBody.RootElement.GetProperty("schedule").GetProperty("maxParallelism").GetInt32());
        }

        [Fact]
        public async Task Pause_resume_soft_delete_and_restore_are_explicit_etag_actions()
        {
            var created = new SqliteJobCatalogRepository(sqlite).Create(Schedule("jobs.lifecycle", isPaused: false));
            using var client = factory.CreateClient();
            var route = $"/api/v1/jobs/{Guid.ParseExact(created.JobId, "N"):D}";

            using var get = await client.GetAsync(route);
            var etag = get.Headers.ETag!.Tag;

            using var paused = await PostAction(client, route + "/actions/pause", etag, new { reason = "maintenance" });
            Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
            using var pausedBody = JsonDocument.Parse(await paused.Content.ReadAsStringAsync());
            Assert.Equal("paused", pausedBody.RootElement.GetProperty("job").GetProperty("lifecycleState").GetString());
            Assert.True(pausedBody.RootElement.GetProperty("schedule").GetProperty("isPaused").GetBoolean());

            using var resumed = await PostAction(client, route + "/actions/resume", paused.Headers.ETag!.Tag, new { reason = "ready" });
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            using var resumedBody = JsonDocument.Parse(await resumed.Content.ReadAsStringAsync());
            Assert.Equal("active", resumedBody.RootElement.GetProperty("job").GetProperty("lifecycleState").GetString());

            using var deleted = await PostAction(client, route + "/actions/soft-delete", resumed.Headers.ETag!.Tag, new { reason = "retire", force = false });
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            using var deletedBody = JsonDocument.Parse(await deleted.Content.ReadAsStringAsync());
            Assert.Equal("softDeleted", deletedBody.RootElement.GetProperty("job").GetProperty("lifecycleState").GetString());

            using var restored = await PostAction(client, route + "/actions/restore", deleted.Headers.ETag!.Tag, new { reason = "return" });
            Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        }

        [Fact]
        public async Task Replace_and_import_require_an_explicit_restore_for_soft_deleted_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var created = catalog.Create(Schedule("jobs.deleted", isPaused: false));
            var deleted = new SqliteJobLifecycleService(sqlite, catalog)
                .SoftDelete(created.JobId, created.CatalogVersion, "test", "retired");
            using var client = factory.CreateClient();
            var route = $"/api/v1/jobs/{Guid.ParseExact(deleted.JobId, "N"):D}";

            using var replace = new HttpRequestMessage(HttpMethod.Put, route)
            {
                Content = JsonContent.Create(new
                {
                    schedule = JsonDocument.Parse(
                        Schedule("jobs.deleted", isPaused: false, id: deleted.JobId, maxParallelism: 2)).RootElement
                })
            };
            replace.Headers.TryAddWithoutValidation("If-Match", $"\"catalog-{deleted.CatalogVersion}\"");
            using var replaced = await client.SendAsync(replace);
            Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);
            using var replacedBody = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync());
            Assert.Equal("soft-deleted-job", replacedBody.RootElement.GetProperty("code").GetString());

            using var imported = await client.PostAsJsonAsync(
                "/api/v1/jobs/import",
                new
                {
                    schedules = new[]
                    {
                        JsonDocument.Parse(
                            Schedule("jobs.deleted", isPaused: false, id: deleted.JobId, maxParallelism: 2)).RootElement
                    }
                });
            Assert.Equal(HttpStatusCode.Conflict, imported.StatusCode);
            using var importedBody = JsonDocument.Parse(await imported.Content.ReadAsStringAsync());
            Assert.Equal("soft-deleted-job", importedBody.RootElement.GetProperty("code").GetString());
            Assert.False(catalog.Get(deleted.JobId)!.IsEnabled);
            Assert.Equal(deleted.CatalogVersion, catalog.Get(deleted.JobId)!.CatalogVersion);
        }

        [Fact]
        public async Task Import_uses_a_typed_envelope_and_export_remains_import_compatible()
        {
            using var client = factory.CreateClient();
            using var import = await client.PostAsJsonAsync(
                "/api/v1/jobs/import",
                new
                {
                    schedules = new[]
                    {
                        JsonDocument.Parse(Schedule("jobs.import.one", isPaused: true)).RootElement,
                        JsonDocument.Parse(Schedule("jobs.import.two", isPaused: false)).RootElement
                    }
                });
            Assert.Equal(HttpStatusCode.OK, import.StatusCode);
            using var body = JsonDocument.Parse(await import.Content.ReadAsStringAsync());
            Assert.Equal(2, body.RootElement.GetProperty("created").GetInt32());

            using var export = JsonDocument.Parse(await client.GetStringAsync("/api/v1/jobs/export"));
            Assert.Equal(2, export.RootElement.GetArrayLength());
        }

        private static async Task<HttpResponseMessage> PostAction(
            HttpClient client,
            string route,
            string etag,
            object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, route)
            {
                Content = JsonContent.Create(body)
            };
            request.Headers.TryAddWithoutValidation("If-Match", etag);
            return await client.SendAsync(request);
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

        private static string Schedule(
            string activityId,
            bool isPaused,
            string? id = null,
            int maxParallelism = 1,
            string? description = null)
        {
            id ??= JobId(activityId);
            var descriptionJson = description is null ? string.Empty : $",\n  \"description\": {JsonSerializer.Serialize(description)}";
            return $$"""
            {
              "id": "{{id}}",
              "activityId": "{{activityId}}",
              "functionName": "BuildThing",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": {{maxParallelism}},
              "queryTimeout": "00:01:00",
              "startFrom": "2026-01-01T00:00:00Z",
              "isPaused": {{isPaused.ToString().ToLowerInvariant()}},
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }{{descriptionJson}}
            }
            """;
        }

        private static string JobId(string value)
        {
            return new Guid(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value))).ToString("N");
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }
    }
}
