// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.RegularExpressions;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Schema;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    public sealed class DependencyPickerViewModelTests
    {
        [Fact]
        public void Current_dependencies_resolve_guid_tokens_to_upstream_names()
        {
            var options = new List<DependencyOption>
            {
                new("00000000000000000000000000000001", "ingest.hourly"),
                new("00000000000000000000000000000002", "rollup.daily")
            };
            var input = new ScheduleFormInput { DependsOn = "00000000000000000000000000000001\n00000000000000000000000000000002" };
            var model = new ScheduleEditorViewModel("/x", input, "{}", null, true, "Save job", false, options);

            Assert.Collection(
                model.CurrentDependencies,
                chip => { Assert.Equal("00000000000000000000000000000001", chip.Value); Assert.Equal("ingest.hourly", chip.Label); Assert.True(chip.Resolved); },
                chip => { Assert.Equal("00000000000000000000000000000002", chip.Value); Assert.Equal("rollup.daily", chip.Label); Assert.True(chip.Resolved); });
        }

        [Fact]
        public void Current_dependencies_resolve_legacy_activity_id_tokens()
        {
            var options = new List<DependencyOption> { new("00000000000000000000000000000001", "ingest.hourly") };
            var input = new ScheduleFormInput { DependsOn = "ingest.hourly" };
            var model = new ScheduleEditorViewModel("/x", input, "{}", null, true, "Save job", false, options);

            var chip = Assert.Single(model.CurrentDependencies);
            Assert.Equal("ingest.hourly", chip.Label);
            Assert.True(chip.Resolved);
        }

        [Fact]
        public void Current_dependencies_flag_unresolved_tokens()
        {
            var options = new List<DependencyOption> { new("00000000000000000000000000000001", "ingest.hourly") };
            var input = new ScheduleFormInput { DependsOn = "ffffffffffffffffffffffffffffffff" };
            var model = new ScheduleEditorViewModel("/x", input, "{}", null, true, "Save job", false, options);

            var chip = Assert.Single(model.CurrentDependencies);
            Assert.Equal("ffffffffffffffffffffffffffffffff", chip.Label);
            Assert.False(chip.Resolved);
        }

        [Fact]
        public void Current_dependencies_dedupe_repeated_tokens()
        {
            var options = new List<DependencyOption> { new("00000000000000000000000000000001", "ingest.hourly") };
            var input = new ScheduleFormInput { DependsOn = "00000000000000000000000000000001\n00000000000000000000000000000001" };
            var model = new ScheduleEditorViewModel("/x", input, "{}", null, true, "Save job", false, options);

            Assert.Single(model.CurrentDependencies);
        }
    }

    public sealed class DependencyPickerWebTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "dependency-picker-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> factory;
        private readonly KoLiteSqliteConnectionFactory sqlite;

        public DependencyPickerWebTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "picker.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteSchema(sqlite).EnsureSchema();
            factory = CreateFactory();
        }

        [Fact]
        public async Task Edit_page_renders_upstream_name_chip_and_picker_options()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(UpstreamSchedule("ingest.hourly", "IngestFunction"));
            catalog.Create(DownstreamSchedule("rollup.daily", "RollupFunction", JobId("ingest.hourly")));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var html = await client.GetStringAsync($"/jobs/{JobId("rollup.daily")}/edit");

            // The picker (not a freeform textarea) is rendered with the upstream as a selectable option.
            Assert.Contains("data-dependency-picker", html, StringComparison.Ordinal);
            Assert.Contains("data-dependency-select", html, StringComparison.Ordinal);
            Assert.Contains($"<option value=\"{JobId("ingest.hourly")}\">ingest.hourly</option>", html, StringComparison.Ordinal);

            // The existing dependency renders as a chip showing the upstream NAME, with the GUID kept as the title.
            Assert.Contains($"title=\"{JobId("ingest.hourly")}\">ingest.hourly</span>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("One activity id per line", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task New_page_renders_dependency_picker()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(UpstreamSchedule("ingest.hourly", "IngestFunction"));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var html = await client.GetStringAsync("/jobs/new");

            Assert.Contains("data-dependency-picker", html, StringComparison.Ordinal);
            Assert.Contains($"<option value=\"{JobId("ingest.hourly")}\">ingest.hourly</option>", html, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Posting_edit_form_saves_picked_dependency_by_id()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(UpstreamSchedule("ingest.hourly", "IngestFunction"));
            var downstream = catalog.Create(UpstreamSchedule("rollup.daily", "RollupFunction"));

            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var token = await ReadFormToken(client, $"/jobs/{downstream.JobId}/edit");
            var version = new SqliteJobCatalogRepository(sqlite).Get(downstream.JobId)!.CatalogVersion;

            var form = new Dictionary<string, string>
            {
                ["formMode"] = "fields",
                ["expectedVersion"] = version.ToString(),
                ["Input.Id"] = downstream.JobId,
                ["Input.ActivityId"] = "rollup.daily",
                ["Input.FunctionName"] = "RollupFunction",
                ["Input.OutputTable"] = "Output",
                ["Input.QueryWindowSize"] = "00:05:00",
                ["Input.DelayFromUtcNow"] = "00:00:00",
                ["Input.MaxParallelism"] = "1",
                ["Input.QueryTimeout"] = "00:01:00",
                ["Input.StartFrom"] = "2026-01-01T00:00:00Z",
                ["Input.ClusterUri"] = "https://kolite-example.invalid",
                ["Input.Database"] = "DemoDb",
                ["Input.IsPaused"] = "false",
                ["Input.JobSettingsJson"] = "{}",
                ["Input.DependsOn"] = JobId("ingest.hourly")
            };

            using var response = await PostForm(client, $"/jobs/{downstream.JobId}/edit", token, form);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

            // The picked dependency round-trips through the unchanged Input.DependsOn binding and is
            // stored canonically by the upstream GUID id.
            var saved = new SqliteJobCatalogRepository(sqlite).Get(downstream.JobId)!;
            Assert.Contains(saved.Definition.DependsOn, dependency => dependency.Id == JobId("ingest.hourly"));
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryBestEffort(testDirectory);
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

        private sealed record FormToken(string Value, string Cookie);

        private static async Task<FormToken> ReadFormToken(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync();
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"(?<token>[^\"]+)\"", RegexOptions.CultureInvariant).Groups["token"].Value;
            Assert.False(string.IsNullOrWhiteSpace(token));
            var cookie = response.Headers.TryGetValues("Set-Cookie", out var cookies)
                ? string.Join("; ", cookies.Select(value => value.Split(';', 2)[0]))
                : string.Empty;
            return new FormToken(token, cookie);
        }

        private static async Task<HttpResponseMessage> PostForm(HttpClient client, string path, FormToken token, Dictionary<string, string> values)
        {
            values["__RequestVerificationToken"] = token.Value;
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(values) };
            if (!string.IsNullOrEmpty(token.Cookie)) request.Headers.Add("Cookie", token.Cookie);
            return await client.SendAsync(request);
        }

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string UpstreamSchedule(string activityId, string functionName) => $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "{{functionName}}",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:01:00",
              "isPaused": false,
              "startFrom": "2026-01-01T00:00:00Z",
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;

        private static string DownstreamSchedule(string activityId, string functionName, string upstreamId) => $$"""
            {
              "id": "{{JobId(activityId)}}",
              "activityId": "{{activityId}}",
              "functionName": "{{functionName}}",
              "outputTable": "Output",
              "queryWindowSize": "00:05:00",
              "delayFromUtcNow": "00:00:00",
              "maxParallelism": 1,
              "queryTimeout": "00:01:00",
              "isPaused": false,
              "startFrom": "2026-01-01T00:00:00Z",
              "dependsOn": [ { "id": "{{upstreamId}}" } ],
              "target": { "clusterUri": "https://kolite-example.invalid", "database": "DemoDb" }
            }
            """;
    }
}
