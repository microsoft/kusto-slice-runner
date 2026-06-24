using System.Net;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Migrations;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KoLite.LocalApp.Tests
{
    // Covers the dependency graph: the DependencyGraphQuery projection (nodes/edges/focal/unresolved)
    // and the web surfaces that render it (the /dependencies page, the job details tab, and the
    // dashboard bulk-bar entry point).
    public sealed class DependencyGraphWebTests : IDisposable
    {
        private readonly string testDirectory = Path.Combine(AppContext.BaseDirectory, "dependency-graph-web-tests", Guid.NewGuid().ToString("N"));
        private readonly string databasePath;
        private readonly WebApplicationFactory<Program> factory;
        private readonly KoLiteSqliteConnectionFactory sqlite;

        public DependencyGraphWebTests()
        {
            Directory.CreateDirectory(testDirectory);
            databasePath = Path.Combine(testDirectory, "web.db");
            sqlite = new KoLiteSqliteConnectionFactory(new KoLiteSqliteConnectionOptions(databasePath));
            new KoLiteSqliteMigrator(sqlite).Migrate();
            factory = CreateFactory();
        }

        [Fact]
        public void Query_pulls_in_transitive_upstream_and_downstream_from_a_single_focal_job()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("chain.a"));
            catalog.Create(Schedule("chain.b", dependsOn: "chain.a"));
            catalog.Create(Schedule("chain.c", dependsOn: "chain.b"));
            catalog.Create(Schedule("chain.unrelated"));

            var graph = BuildGraph(JobId("chain.b"));

            Assert.Equal(
                new[] { "chain.a", "chain.b", "chain.c" },
                graph.Nodes.Select(n => n.Label).OrderBy(label => label, StringComparer.Ordinal));
            Assert.DoesNotContain(graph.Nodes, n => n.Label == "chain.unrelated");
            Assert.True(graph.Nodes.Single(n => n.Label == "chain.b").Focal);
            Assert.False(graph.Nodes.Single(n => n.Label == "chain.a").Focal);
            Assert.All(graph.Nodes, n => Assert.True(n.Resolved));
            Assert.Contains(graph.Edges, e => e.FromId == JobId("chain.a") && e.ToId == JobId("chain.b"));
            Assert.Contains(graph.Edges, e => e.FromId == JobId("chain.b") && e.ToId == JobId("chain.c"));
        }

        [Fact]
        public void Query_marks_unknown_focal_job_as_an_unresolved_placeholder()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("present.job"));

            var graph = BuildGraph("does-not-exist");

            var node = Assert.Single(graph.Nodes);
            Assert.False(node.Resolved);
            Assert.Null(node.Href);
            Assert.Equal(1, graph.FocalCount);
            Assert.Equal(0, graph.ResolvedFocalCount);
        }

        [Fact]
        public void Query_returns_empty_for_no_focal_jobs()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("lonely.job"));

            Assert.True(BuildGraph().IsEmpty);
        }

        [Fact]
        public async Task Dependencies_page_with_no_selection_prompts_to_select_jobs()
        {
            using var client = factory.CreateClient();

            var html = await client.GetStringAsync("/dependencies");

            Assert.Contains("No jobs selected", html);
        }

        [Fact]
        public async Task Dependencies_page_renders_the_focal_chain()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("page.upstream"));
            catalog.Create(Schedule("page.downstream", dependsOn: "page.upstream"));
            using var client = factory.CreateClient();

            // Only the upstream is focal; the downstream must still appear via the transitive chain.
            var html = await client.GetStringAsync($"/dependencies?job={JobId("page.upstream")}");

            Assert.Contains("data-dependency-graph", html);
            Assert.Contains("data-dependency-graph-data", html);
            Assert.Contains("page.upstream", html);
            Assert.Contains("page.downstream", html);
            Assert.Contains($"/jobs/{JobId("page.downstream")}", html);
        }

        [Fact]
        public async Task Job_details_page_has_a_dependencies_tab_with_the_graph()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("detail.upstream"));
            catalog.Create(Schedule("detail.downstream", dependsOn: "detail.upstream"));
            using var client = factory.CreateClient();

            var html = await client.GetStringAsync($"/jobs/{JobId("detail.downstream")}");

            Assert.Contains("id=\"dependencies\"", html);
            Assert.Contains("data-dependency-graph", html);
            // The upstream is part of this job's chain and should be linked from the graph.
            Assert.Contains($"/jobs/{JobId("detail.upstream")}", html);
        }

        [Fact]
        public async Task Dashboard_bulk_bar_exposes_the_dependencies_button()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("bar.job"));
            using var client = factory.CreateClient();

            var html = await client.GetStringAsync("/");

            Assert.Contains("data-bulk-dependencies", html);
        }

        public void Dispose()
        {
            factory.Dispose();
            TestCleanup.DeleteDirectoryWithRetry(testDirectory);
        }

        private DependencyGraphViewModel BuildGraph(params string[] focalJobIds)
        {
            using var scope = factory.Services.CreateScope();
            var query = scope.ServiceProvider.GetRequiredService<DependencyGraphQuery>();
            return query.Build(focalJobIds);
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

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string? dependsOn = null) => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "DependencyGraphFunction",
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
