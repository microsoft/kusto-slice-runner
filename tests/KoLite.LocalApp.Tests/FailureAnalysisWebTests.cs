using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class FailureAnalysisWebTests : IDisposable
    {
        private const string JobId = "11111111222233334444555566667777";

        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "failure-analysis-web-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> factory;
        private readonly KoLiteSqliteConnectionFactory sqlite;

        public FailureAnalysisWebTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "analysis.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Operations_tab_renders_the_analyze_card()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SeedJobAsync(client);

            var page = await client.GetStringAsync($"/jobs/{JobId}");

            Assert.Contains("Analyze failures with Copilot", page);
            Assert.Contains("data-analyze-card", page);
            Assert.Contains("data-analyze-failures", page);
            Assert.Contains("data-analyze-status", page);
            Assert.Contains("data-analyze-output", page);
            Assert.Contains("__RequestVerificationToken", page);
        }

        [Fact]
        public async Task Static_assets_include_the_failure_analysis_hooks()
        {
            using var client = factory.CreateClient();

            var script = await client.GetStringAsync("/js/site.js");
            var css = await client.GetStringAsync("/css/site.css");

            Assert.Contains("initFailureAnalysis", script);
            Assert.Contains("data-analyze-failures", script);
            Assert.Contains("sanitizeMarkdownHtml", script);
            Assert.Contains("safeMarkdownRenderer", script);
            Assert.Contains(".analyze-failures-card", css);
            Assert.Contains(".analyze-output", css);
            Assert.Contains(".markdown-body", css);
        }

        [Fact]
        public async Task Analyze_endpoint_completes_immediately_when_there_are_no_failures()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SeedJobAsync(client);

            var token = await ReadFormToken(client, $"/jobs/{JobId}");
            using var response = await PostAnalyze(client, JobId, token);
            var body = await response.Content.ReadAsStringAsync();

            Assert.True(response.IsSuccessStatusCode, body);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            Assert.Equal("Completed", root.GetProperty("status").GetString());
            Assert.Contains("No recent failures", root.GetProperty("markdown").GetString());

            // The run is retrievable by id and scoped to the job.
            var runId = root.GetProperty("runId").GetString();
            var poll = await client.GetStringAsync($"/api/jobs/{JobId}/analyze-failures/{runId}");
            using var pollDoc = JsonDocument.Parse(poll);
            Assert.Equal("Completed", pollDoc.RootElement.GetProperty("status").GetString());
        }

        [Fact]
        public async Task Analyze_endpoint_requires_the_antiforgery_token()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SeedJobAsync(client);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{JobId}/analyze-failures");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        [Fact]
        public async Task Analyze_endpoint_returns_404_for_unknown_job()
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            await SeedJobAsync(client);

            var token = await ReadFormToken(client, $"/jobs/{JobId}");
            using var response = await PostAnalyze(client, "ffffffffffffffffffffffffffffffff", token);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        private static async Task SeedJobAsync(HttpClient client)
        {
            var schedule = $$"""
            {
              "id": "{{JobId}}",
              "activityId": "job.analysis.web",
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
            using var response = await client.PostAsync("/api/jobs/import", new StringContent(schedule, Encoding.UTF8, "application/json"));
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, body);
        }

        private static async Task<HttpResponseMessage> PostAnalyze(HttpClient client, string jobId, FormToken token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/jobs/{jobId}/analyze-failures");
            request.Headers.Add("X-CSRF-TOKEN", token.Value);
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static async Task<FormToken> ReadFormToken(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var value = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant).Groups["token"].Value;
            Assert.False(string.IsNullOrWhiteSpace(value));
            var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(v => v.Split(';', 2)[0]))
                : string.Empty;
            return new FormToken(value, cookie);
        }

        private WebApplicationFactory<Program> CreateFactory() =>
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:KoLiteSqlite"] = databasePath,
                    ["KoLite:Scheduler:Enabled"] = "false",
                    ["KoLite:UpdateCheck:Enabled"] = "false"
                }));
                builder.ConfigureServices(services => services.AddLogging(logging => logging.ClearProviders()));
            });

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
        }

        private sealed record FormToken(string Value, string Cookie);
    }
}
