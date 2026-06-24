using System.Net;
using System.Text.RegularExpressions;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.Local.Sqlite.Observability;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    // Issue #3 at the web layer: the single soft-delete endpoint blocks on active dependents and
    // redirects to a confirm page that offers an explicit force override, and the bulk action skips
    // and reports blocked jobs instead of deleting them.
    public sealed class SoftDeleteDependentsWebTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "soft-delete-dependents-web-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> factory;
        private readonly KoLiteSqliteConnectionFactory sqlite;

        public SoftDeleteDependentsWebTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "web.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteMigrator(sqlite).Migrate();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Soft_delete_on_job_with_active_dependent_redirects_to_confirm_page()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("dep.upstream"));
            catalog.Create(Schedule("dep.downstream", dependsOn: "dep.upstream"));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var token = await ReadFormToken(client, $"/jobs/{JobId("dep.upstream")}");
            var response = await PostForm(client, $"/catalog/{JobId("dep.upstream")}/soft-delete", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["reason"] = "blocked by dependents"
            });

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal($"/catalog/{JobId("dep.upstream")}/soft-delete-confirm", response.Headers.Location?.OriginalString);

            // Blocked, not soft-deleted: the upstream is still enabled in the catalog.
            Assert.True(catalog.Get(JobId("dep.upstream"))?.IsEnabled);
        }

        [Fact]
        public async Task Soft_delete_confirm_page_lists_the_active_dependent()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("dep.upstream"));
            catalog.Create(Schedule("dep.downstream", dependsOn: "dep.upstream"));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var html = await client.GetStringAsync($"/catalog/{JobId("dep.upstream")}/soft-delete-confirm");

            Assert.Contains("Active jobs depend on this job", html);
            Assert.Contains("dep.downstream", html);
            Assert.Contains($"/jobs/{JobId("dep.downstream")}", html);
            Assert.Contains("Soft delete anyway", html);
        }

        [Fact]
        public async Task Soft_delete_with_force_true_soft_deletes_the_blocked_job()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("dep.upstream"));
            catalog.Create(Schedule("dep.downstream", dependsOn: "dep.upstream"));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var token = await ReadFormToken(client, $"/catalog/{JobId("dep.upstream")}/soft-delete-confirm");
            var response = await PostForm(client, $"/catalog/{JobId("dep.upstream")}/soft-delete", token, new Dictionary<string, string>
            {
                ["expectedVersion"] = "1",
                ["reason"] = "forced past dependents",
                ["force"] = "true"
            });

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/", response.Headers.Location?.OriginalString);
            Assert.False(catalog.Get(JobId("dep.upstream"))?.IsEnabled);
            Assert.True(new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)).GetLatestStates()[JobId("dep.upstream")].IsSoftDeleted);
        }

        [Fact]
        public async Task Bulk_soft_delete_skips_blocked_job_and_reports_it()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("bulk.upstream"));
            catalog.Create(Schedule("bulk.downstream", dependsOn: "bulk.upstream"));
            catalog.Create(Schedule("bulk.free"));
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

            var token = await ReadFormToken(client, "/");
            var response = await PostFormValues(client, "/catalog/bulk/soft-delete", token,
            [
                new("jobIds", JobId("bulk.upstream")), new("expectedVersions", "1"),
                new("jobIds", JobId("bulk.free")), new("expectedVersions", "1")
            ]);

            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            var states = new LifecycleReadModel(new SqliteLifecycleReadModelRepository(sqlite)).GetLatestStates();
            Assert.True(states[JobId("bulk.free")].IsSoftDeleted);
            Assert.True(catalog.Get(JobId("bulk.upstream"))?.IsEnabled);
            Assert.False(states.TryGetValue(JobId("bulk.upstream"), out var upstreamState) && upstreamState.IsSoftDeleted);

            // The summary banner (TempData cookie -> dashboard) names the blocked job and its dependent.
            using var dashboard = new HttpRequestMessage(HttpMethod.Get, "/");
            var cookie = CookieHeader(response);
            if (!string.IsNullOrEmpty(cookie)) dashboard.Headers.Add("Cookie", cookie);
            using var dashboardResponse = await client.SendAsync(dashboard);
            var html = await dashboardResponse.Content.ReadAsStringAsync();
            Assert.Contains("skipped because other active jobs depend on them", html);
            Assert.Contains("bulk.upstream (needed by bulk.downstream)", html);
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

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

        private async Task<FormToken> ReadFormToken(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant).Groups["token"].Value;
            Assert.False(string.IsNullOrWhiteSpace(token));
            return new FormToken(token, CookieHeader(response));
        }

        private static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, FormToken token, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = token.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(values)
            };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static async Task<HttpResponseMessage> PostFormValues(HttpClient client, string path, FormToken token, IEnumerable<KeyValuePair<string, string>> values)
        {
            var fields = new List<KeyValuePair<string, string>>(values)
            {
                new("__RequestVerificationToken", token.Value)
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new FormUrlEncodedContent(fields)
            };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static string CookieHeader(HttpResponseMessage response) =>
            response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(v => v.Split(';', 2)[0]))
                : string.Empty;

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

        private sealed record FormToken(string Value, string Cookie);
    }
}
