// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.LocalApp.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class AgentApiContractTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "agent-contract-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly KoLiteSqliteConnectionFactory sqlite;
        private readonly WebApplicationFactory<Program> factory;

        public AgentApiContractTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "contract.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task OpenApi_contains_only_the_supported_agent_surface()
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync("/api/v1/openapi/v1.json");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var paths = document.RootElement.GetProperty("paths");

            Assert.True(paths.TryGetProperty("/api/v1/jobs", out _));
            Assert.True(paths.TryGetProperty("/api/v1/jobs/{jobId}", out _));
            Assert.True(paths.TryGetProperty("/api/v1/operations/logs", out _));
            Assert.True(paths.TryGetProperty("/api/v1/system/status", out _));
            Assert.True(paths.TryGetProperty("/api/v1/dependency-graphs/kusto-lineage", out _));
            Assert.False(paths.TryGetProperty("/healthz", out _));
            Assert.False(paths.TryGetProperty("/control/v1/shutdown", out _));
            Assert.False(paths.TryGetProperty("/ui-api/v1/jobs/{jobId}/failure-analyses", out _));
            Assert.False(paths.TryGetProperty("/jobs/new", out _));

            var operations = paths.EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject()
                    .Select(operation => operation.Name.ToUpperInvariant() + " " + path.Name))
                .ToArray();
            var expectedOperations = new[]
            {
                "GET /api/v1/jobs",
                "POST /api/v1/jobs",
                "GET /api/v1/jobs/{jobId}",
                "PUT /api/v1/jobs/{jobId}",
                "POST /api/v1/jobs/{jobId}/actions/pause",
                "POST /api/v1/jobs/{jobId}/actions/resume",
                "POST /api/v1/jobs/{jobId}/actions/soft-delete",
                "POST /api/v1/jobs/{jobId}/actions/restore",
                "POST /api/v1/jobs/import",
                "GET /api/v1/jobs/export",
                "GET /api/v1/jobs/{jobId}/status",
                "GET /api/v1/jobs/{jobId}/catalog-revisions",
                "GET /api/v1/jobs/{jobId}/dependencies",
                "POST /api/v1/jobs/{jobId}/repair-previews",
                "POST /api/v1/jobs/{jobId}/repairs",
                "GET /api/v1/operations/worker-pool",
                "GET /api/v1/operations/queue",
                "GET /api/v1/operations/slices",
                "GET /api/v1/operations/running-slices",
                "GET /api/v1/operations/chunks",
                "GET /api/v1/operations/attempts",
                "GET /api/v1/operations/events",
                "GET /api/v1/operations/logs",
                "GET /api/v1/operations/throughput",
                "GET /api/v1/operations/failures",
                "GET /api/v1/operations/audit-events",
                "GET /api/v1/operations/reruns",
                "GET /api/v1/operations/reruns/{batchId}",
                "GET /api/v1/operations/repairs",
                "GET /api/v1/operations/repairs/{batchId}",
                "POST /api/v1/dependency-graphs/kusto-lineage",
                "GET /api/v1/system/status"
            };
            Assert.Equal(
                expectedOperations.Order(StringComparer.Ordinal),
                operations.Order(StringComparer.Ordinal));

            var operationIds = paths.EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject())
                .Where(operation => operation.Value.TryGetProperty("operationId", out _))
                .Select(operation => operation.Value.GetProperty("operationId").GetString())
                .ToArray();
            Assert.NotEmpty(operationIds);
            Assert.Equal(operationIds.Length, operationIds.Distinct(StringComparer.Ordinal).Count());

            var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
            Assert.True(schemas.TryGetProperty("JobDetailResponse", out _));
            Assert.True(schemas.TryGetProperty("RepairPreviewResponse", out _));
            Assert.True(schemas.TryGetProperty("SystemStatusResponse", out _));
            var retentionProperties = schemas.GetProperty("RetentionStatusResponse").GetProperty("properties");
            Assert.False(retentionProperties.TryGetProperty("ingestionThrottlesDeleted", out _));
        }

        [Fact]
        public async Task Missing_resource_uses_problem_details_with_a_stable_code()
        {
            using var client = factory.CreateClient();
            using var response = await client.GetAsync($"/api/v1/jobs/{Guid.NewGuid():D}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("job-not-found", body.RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task Single_job_mutations_require_and_replace_the_etag()
        {
            var record = new SqliteJobCatalogRepository(sqlite).Create(Schedule("etag.job", isPaused: true));
            using var client = factory.CreateClient();
            using var get = await client.GetAsync($"/api/v1/jobs/{Guid.ParseExact(record.JobId, "N"):D}");
            var etag = Assert.Single(get.Headers.ETag is null ? Array.Empty<string>() : [get.Headers.ETag.Tag]);

            using var missing = await client.PutAsJsonAsync(
                $"/api/v1/jobs/{Guid.ParseExact(record.JobId, "N"):D}",
                new { schedule = JsonDocument.Parse(Schedule("etag.job", isPaused: true, maxParallelism: 2)).RootElement });
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);

            using var staleRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/jobs/{Guid.ParseExact(record.JobId, "N"):D}")
            {
                Content = JsonContent.Create(new
                {
                    schedule = JsonDocument.Parse(Schedule("etag.job", isPaused: true, maxParallelism: 2)).RootElement
                })
            };
            staleRequest.Headers.TryAddWithoutValidation("If-Match", "\"catalog-999\"");
            using var stale = await client.SendAsync(staleRequest);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);

            using var updateRequest = new HttpRequestMessage(
                HttpMethod.Put,
                $"/api/v1/jobs/{Guid.ParseExact(record.JobId, "N"):D}")
            {
                Content = JsonContent.Create(new
                {
                    schedule = JsonDocument.Parse(Schedule("etag.job", isPaused: true, maxParallelism: 2)).RootElement
                })
            };
            updateRequest.Headers.TryAddWithoutValidation("If-Match", etag);
            using var updated = await client.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            Assert.NotEqual(etag, updated.Headers.ETag?.Tag);
        }

        [Fact]
        public async Task Protected_groups_use_the_injected_local_request_policy()
        {
            using var deniedFactory = CreateFactory(allowLocalRequests: false);
            using var client = deniedFactory.CreateClient();

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/jobs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/openapi/v1.json")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/control/v1/shutdown")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.GetAsync($"/ui-api/v1/jobs/{Guid.NewGuid():D}/failure-analyses/run")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
        }

        [Fact]
        public void Production_local_policy_fails_closed_and_allows_loopback()
        {
            var policy = new LoopbackLocalRequestPolicy();
            var context = new DefaultHttpContext();
            Assert.False(policy.IsAllowed(context));

            context.Connection.RemoteIpAddress = IPAddress.Loopback;
            Assert.True(policy.IsAllowed(context));

            context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.5");
            Assert.False(policy.IsAllowed(context));
        }

        [Fact]
        public async Task Health_probe_omits_database_and_runtime_configuration()
        {
            using var client = factory.CreateClient();
            var json = await client.GetStringAsync("/healthz");
            Assert.DoesNotContain("database", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("scheduler", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kusto", json, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Legacy_routes_are_removed()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/jobs")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/status/health")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/catalog")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/catalog/new")).StatusCode);
        }

        private WebApplicationFactory<Program> CreateFactory(bool allowLocalRequests = true)
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
                    services.RemoveAll<ILocalRequestPolicy>();
                    services.AddSingleton<ILocalRequestPolicy>(
                        allowLocalRequests ? new TestLocalRequestPolicy() : new DenyLocalRequestPolicy());
                });
            });
        }

        private static string Schedule(string activityId, bool isPaused, int maxParallelism = 1)
        {
            return $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "BuildThing",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": {{maxParallelism}},
              "queryTimeout": "00:01:00",
              "startFrom": "2026-01-01T00:00:00Z",
              "isPaused": {{isPaused.ToString().ToLowerInvariant()}},
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
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

        private sealed class DenyLocalRequestPolicy : ILocalRequestPolicy
        {
            public bool IsAllowed(HttpContext context)
            {
                return false;
            }
        }
    }
}
