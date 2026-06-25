using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KoLite.Local.Core.Graph;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;
using KoLite.Local.Sqlite.Connections;
using KoLite.Local.Sqlite.Lifecycle;
using KoLite.Local.Sqlite.Migrations;
using KoLite.LocalApp.Ui;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        private readonly TestKustoEntityDependencyReader kustoReader = new();
        private const string Cluster = "kolite-example.invalid";

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
        public void Query_excludes_a_soft_deleted_upstream_and_drops_its_edge()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("sd.upstream"));
            catalog.Create(Schedule("sd.downstream", dependsOn: "sd.upstream"));
            SoftDelete("sd.upstream");

            var graph = BuildGraph(JobId("sd.downstream"));

            var node = Assert.Single(graph.Nodes);
            Assert.Equal("sd.downstream", node.Label);
            // The soft-deleted upstream is hidden entirely - not drawn, and not an "unknown" placeholder.
            Assert.Empty(graph.Edges);
            Assert.DoesNotContain(graph.Nodes, n => !n.Resolved);
        }

        [Fact]
        public void Query_returns_empty_when_the_only_focal_job_is_soft_deleted()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("sd.only"));
            SoftDelete("sd.only");

            Assert.True(BuildGraph(JobId("sd.only")).IsEmpty);
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
        public async Task Dependencies_page_omits_a_soft_deleted_job_from_the_chain()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("hide.upstream"));
            catalog.Create(Schedule("hide.downstream", dependsOn: "hide.upstream"));
            SoftDelete("hide.upstream");
            using var client = factory.CreateClient();

            var html = await client.GetStringAsync($"/dependencies?job={JobId("hide.downstream")}");

            Assert.Contains("hide.downstream", html);
            Assert.DoesNotContain("hide.upstream", html);
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

        [Fact]
        public async Task Kusto_consumers_endpoint_enriches_the_graph_with_consumer_nodes()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("kusto.job", functionName: "BuildT", outputTable: "_T"));
            // A non-job function LatestT reads the job's output table _T.
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "LatestT", "Function", Cluster, "DemoDb", "_T", "Table") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("kusto.job") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Contains(nodes, n => n.GetProperty("kind").GetString() == "KustoFunction" && n.GetProperty("label").GetString() == "LatestT");
            Assert.Contains(nodes, n => n.GetProperty("kind").GetString() == "Job" && n.GetProperty("label").GetString() == "kusto.job");
        }

        [Fact]
        public async Task Kusto_endpoint_adds_upstream_source_nodes()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("src.job", functionName: "BuildSrc", outputTable: "_Out"));
            // The job's function reads a non-job raw table -> a source node.
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "BuildSrc", "Function", Cluster, "DemoDb", "RawSource", "Table") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("src.job") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Contains(nodes, n => n.GetProperty("kind").GetString() == "KustoTable" && n.GetProperty("label").GetString() == "RawSource");
        }

        [Fact]
        public async Task Kusto_endpoint_adds_a_cross_cluster_source_node()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("remote.job", functionName: "BuildRemote", outputTable: "_Out"));
            // The job reads a table on another cluster (named directly in its dependencies).
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "BuildRemote", "Function", "other.kusto.windows.net", "fleet", "MetricsPerNode", "RemoteEntity") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("remote.job") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Contains(nodes, n => n.GetProperty("label").GetString() == "cluster('other').database('fleet').MetricsPerNode" && n.GetProperty("kind").GetString() == "KustoExternal");
        }

        [Fact]
        public async Task Kusto_endpoint_qualifies_a_cross_database_source()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("xdb.job", functionName: "BuildXdb", outputTable: "_Out"));
            // Same cluster, different database -> database('Other').RawX
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "BuildXdb", "Function", Cluster, "Other", "RawX", "Table") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("xdb.job") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Contains(nodes, n => n.GetProperty("label").GetString() == "database('Other').RawX" && n.GetProperty("kind").GetString() == "KustoTable");
        }

        [Fact]
        public async Task Kusto_endpoint_qualifies_a_wildcard_reference_and_does_not_call_it_a_function()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("wild.job", functionName: "BuildWild", outputTable: "_Out"));
            // A `union database('fc').*`-style read is reported by Kusto as a RemoteEntity named "*".
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "BuildWild", "Function", Cluster, "fc", "*", "RemoteEntity") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("wild.job") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Contains(nodes, n => n.GetProperty("label").GetString() == "database('fc').*" && n.GetProperty("kind").GetString() == "KustoExternal" && n.GetProperty("statusText").GetString() == "All entities");
        }

        [Fact]
        public async Task Kusto_endpoint_flags_an_implicit_job_dependency()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("impl.a", functionName: "BuildA", outputTable: "_A"));
            // B does NOT declare A in dependsOn, but its function reads A's output table _A.
            catalog.Create(Schedule("impl.b", functionName: "BuildB", outputTable: "_B"));
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "BuildB", "Function", Cluster, "DemoDb", "_A", "Table") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("impl.b") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var edges = payload.GetProperty("edges").EnumerateArray().ToList();
            Assert.Contains(edges, e => e.GetProperty("from").GetString() == JobId("impl.a") && e.GetProperty("to").GetString() == JobId("impl.b") && e.GetProperty("implicit").GetBoolean());
            var nodes = payload.GetProperty("nodes").EnumerateArray().ToList();
            Assert.Contains(nodes, n => n.GetProperty("kind").GetString() == "Job" && n.GetProperty("label").GetString() == "impl.a");
        }

        [Fact]
        public async Task Kusto_endpoint_does_not_flag_a_declared_dependency_as_implicit()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("decl.a", functionName: "BuildA", outputTable: "_A"));
            catalog.Create(Schedule("decl.b", dependsOn: "decl.a", functionName: "BuildB", outputTable: "_B"));
            kustoReader.Edges = new[] { new KustoEntityEdge(Cluster, "DemoDb", "BuildB", "Function", Cluster, "DemoDb", "_A", "Table") };
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("decl.b") } });

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            var edges = payload.GetProperty("edges").EnumerateArray().ToList();
            Assert.Contains(edges, e => e.GetProperty("from").GetString() == JobId("decl.a") && e.GetProperty("to").GetString() == JobId("decl.b"));
            Assert.DoesNotContain(edges, e => e.GetProperty("from").GetString() == JobId("decl.a") && e.GetProperty("to").GetString() == JobId("decl.b") && e.GetProperty("implicit").GetBoolean());
        }

        [Fact]
        public async Task Kusto_consumers_endpoint_returns_an_error_envelope_on_reader_failure()
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            catalog.Create(Schedule("kusto.fail", functionName: "BuildT", outputTable: "_T"));
            kustoReader.Error = new InvalidOperationException("kusto unreachable");
            using var client = factory.CreateClient();

            using var response = await client.PostAsJsonAsync(
                "/api/dependency-graph/kusto-consumers",
                new { jobIds = new[] { JobId("kusto.fail") } });

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains("kusto unreachable", payload.GetProperty("error").GetString());
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

        private void SoftDelete(string activityId)
        {
            var catalog = new SqliteJobCatalogRepository(sqlite);
            var record = catalog.GetByActivityId(activityId) ?? throw new InvalidOperationException($"Missing job '{activityId}'.");
            new SqliteJobLifecycleService(sqlite, catalog).SoftDelete(record.JobId, record.CatalogVersion, "test", "graph test", force: true);
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
                builder.ConfigureServices(services =>
                {
                    services.AddLogging(logging => logging.ClearProviders());
                    // No live Kusto in tests: the enrichment endpoint reads through this fake.
                    services.RemoveAll<IKustoEntityDependencyReader>();
                    services.AddSingleton<IKustoEntityDependencyReader>(kustoReader);
                });
            });

        private static string JobId(string activityId)
        {
            var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(activityId));
            return new Guid(bytes).ToString("N");
        }

        private static string Schedule(string activityId, string? dependsOn = null, string functionName = "DependencyGraphFunction", string outputTable = "Output") => $$"""
        {
          "id": "{{JobId(activityId)}}",
          "activityId": "{{activityId}}",
          "functionName": "{{functionName}}",
          "outputTable": "{{outputTable}}",
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

        private sealed class TestKustoEntityDependencyReader : IKustoEntityDependencyReader
        {
            public IReadOnlyList<KustoEntityEdge> Edges { get; set; } = Array.Empty<KustoEntityEdge>();
            public Exception? Error { get; set; }

            public Task<IReadOnlyList<KustoEntityEdge>> ReadAsync(Uri clusterUri, string database, CancellationToken cancellationToken = default)
            {
                if (Error is not null)
                {
                    throw Error;
                }

                return Task.FromResult(Edges);
            }
        }
    }
}
