// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net.Http.Json;
using System.Text.Json;
using Ksr.Local.Core.FailureSummaries;
using Ksr.Local.Core.Performance;
using Ksr.Local.Core.Scheduling;
using Ksr.Local.Kusto.Execution;
using Ksr.Local.Sqlite.Catalog;
using Ksr.Local.Sqlite.Connections;
using Ksr.Local.Sqlite.Schema;
using Ksr.Local.Sqlite.State;
using Ksr.LocalApp.FailureAnalysis;
using Ksr.LocalApp.ScreenshotHost;
using Ksr.LocalApp.Ui;
using Microsoft.Extensions.DependencyInjection;

namespace Ksr.LocalApp.Tests
{
    public sealed class DocumentationScreenshotTests : IClassFixture<DocumentationScreenshotTests.Fixture>
    {
        private readonly Fixture fixture;

        public DocumentationScreenshotTests(Fixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public async Task Published_app_surfaces_use_only_the_owned_execution_disabled_database()
        {
            using var response = await fixture.Client.GetAsync("/api/v1/system/status");
            response.EnsureSuccessStatusCode();
            Assert.Equal(fixture.Sandbox.RunId, Assert.Single(response.Headers.GetValues(ScreenshotAppFactory.IdentityHeader)));
            var status = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(fixture.Sandbox.DatabasePath, status.GetProperty("database").GetProperty("path").GetString());
            Assert.Equal(10, status.GetProperty("database").GetProperty("jobCount").GetInt32());
            Assert.False(status.GetProperty("scheduler").GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("workerPool").GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("retention").GetProperty("enabled").GetBoolean());
            Assert.False(status.GetProperty("update").GetProperty("enabled").GetBoolean());
            Assert.Equal(0, status.GetProperty("workerPool").GetProperty("starts").GetInt64());
            Assert.Contains("Demo.Revenue5Min", await fixture.Client.GetStringAsync("/"));
            var details = await fixture.Client.GetStringAsync($"/jobs/{ScreenshotDataset.DetailJobId}");
            Assert.Contains("Slice history", details);
            Assert.Contains("data-slice-status=\"Running\"", details);
            Assert.DoesNotContain("data-slice-status=\"Stalled (orphaned lease)\"", details);
            Assert.Contains("Executions processed", await fixture.Client.GetStringAsync("/activity"));
            Assert.Contains("initFailureAnalysis", await fixture.Client.GetStringAsync("/js/site.js"));
            Assert.Contains(".dependency-graph", await fixture.Client.GetStringAsync("/css/site.css"));
            Assert.Equal(0, fixture.Factory.Services.GetRequiredService<ForbiddenExternalServices>().Attempts);
        }

        [Fact]
        public void Synthetic_counts_preserve_chunks_retries_and_terminal_outcomes()
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var data = scope.ServiceProvider.GetRequiredService<ActivityQuery>().GetActivity(TimeSpan.FromDays(1));
            Assert.Equal(2, data.RunningNow.RunningCount);
            Assert.Equal(2, data.RunningNow.QueuedCount);
            Assert.Equal(3, data.RunningNow.RunningExecutionCount);
            Assert.Equal(3, data.RunningNow.QueuedExecutionCount);
            Assert.Equal(5836, data.AllTime.Succeeded);
            Assert.Equal(3, data.AllTime.Failed);
            Assert.Equal(3, data.Last7Days.Failed);
            Assert.True(data.LastDay.Total < data.Last7Days.Total);
            Assert.True(data.Last7Days.Total < data.Last30Days.Total);
            Assert.True(data.Last30Days.Total < data.AllTime.Total);
            Assert.True(data.Chart.HasData);
            Assert.All(data.RunningNow.RunningSlices, row => Assert.NotNull(row.EtaUtc));

            var orders = Assert.Single(data.RunningNow.RunningSlices, row => row.Slice.JobId == ScreenshotDataset.JobId(1));
            Assert.Equal(2, orders.CompletedChunks);
            Assert.Equal(4, orders.TotalChunks);
            Assert.Equal(new int?[] { 2, 3 }, orders.RunningExecutions.Select(execution => execution.ChunkId));
            Assert.All(orders.RunningExecutions, execution => Assert.False(execution.LeaseExpired));

            var retryStart = ScreenshotDataset.Now.AddHours(-6).AddMinutes(-15);
            var retry = scope.ServiceProvider.GetRequiredService<SqliteSliceStateRepository>()
                .Get(ScreenshotDataset.DetailJobId, retryStart, retryStart.AddMinutes(5));
            Assert.Equal(2, retry.Attempt);
            Assert.Equal(DurableSliceStatus.Completed, retry.Status);

            var catalog = scope.ServiceProvider.GetRequiredService<SqliteJobCatalogRepository>();
            Assert.All(catalog.List(), job =>
            {
                Assert.Equal(ScreenshotDataset.Cluster, job.Definition.Target.ClusterUri);
                Assert.Equal(ScreenshotDataset.Database, job.Definition.Target.Database);
                Assert.StartsWith("Demo.", job.ActivityId);
            });
            Assert.True(catalog.Get(ScreenshotDataset.JobId(10))!.Definition.IsPaused);
        }

        [Fact]
        public async Task Real_lineage_endpoint_renders_the_synthetic_declared_and_implicit_graph()
        {
            using var response = await fixture.Client.PostAsJsonAsync("/api/v1/dependency-graphs/kusto-lineage",
                new { jobIds = new[] { ScreenshotDataset.DetailJobId } });
            response.EnsureSuccessStatusCode();
            var graph = await response.Content.ReadFromJsonAsync<JsonElement>();
            var nodes = graph.GetProperty("nodes").EnumerateArray().ToArray();
            Assert.Equal(16, nodes.Length);
            Assert.Equal(9, nodes.Count(node => node.GetProperty("kind").GetString()!.StartsWith("Kusto", StringComparison.Ordinal)));
            Assert.Single(graph.GetProperty("edges").EnumerateArray(), edge => edge.GetProperty("implicit").GetBoolean());
            Assert.Equal(0, fixture.Factory.Services.GetRequiredService<ForbiddenExternalServices>().Attempts);
        }

        [Fact]
        public async Task Illustrative_analysis_matches_actual_seeded_failure_evidence_without_a_cli()
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<SqliteJobCatalogRepository>().Get(ScreenshotDataset.FailureJobId)!;
            var evidence = scope.ServiceProvider.GetRequiredService<FailureAnalysisPromptBuilder>().Build(job);
            Assert.Contains("deadLettered: 3", evidence.Prompt);
            Assert.Contains("completed: 9", evidence.Prompt);
            var runner = scope.ServiceProvider.GetRequiredService<IFailureSummaryRunner>();
            var result = await runner.RunAsync(evidence.Prompt);
            Assert.True(result.Succeeded);
            Assert.Contains("No Copilot request was made.", result.SummaryMarkdown);
            Assert.Contains("3 dead-lettered windows", result.SummaryMarkdown);
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync("unrelated evidence"));
            Assert.Equal(0, fixture.Factory.Services.GetRequiredService<ForbiddenExternalServices>().Attempts);
        }

        [Fact]
        public void Sandbox_refuses_live_port_existing_runs_and_nonempty_catalogs()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ScreenshotSandbox.Create(fixture.Sandbox.Workspace, Guid.NewGuid(), 5057));
            Assert.Throws<IOException>(() => ScreenshotSandbox.Create(fixture.Sandbox.Workspace, Guid.Parse(fixture.Sandbox.RunId), 5107));
            Assert.Throws<ArgumentException>(() => ScreenshotSandbox.Create(fixture.Sandbox.Workspace, Guid.Empty, 5107));
            Assert.Throws<InvalidOperationException>(() => ScreenshotDataset.Seed(fixture.Connections));
        }

        [Fact]
        public async Task Remote_clients_fail_closed_even_if_accidentally_called()
        {
            var blocked = new ForbiddenExternalServices();
            Assert.Throws<InvalidOperationException>(() => blocked.CreateForDatabase(new Uri(ScreenshotDataset.Cluster), ScreenshotDataset.Database));
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.InvokeAsync("example", CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.CheckAsync("example/example", null, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.ReadAsync(new KustoCommandStatisticsQuery(
                new Uri(ScreenshotDataset.Cluster), ScreenshotDataset.Database, ScreenshotDataset.Now.AddHours(-1),
                ScreenshotDataset.Now, Array.Empty<string>())));
            Assert.Equal(4, blocked.Attempts);
        }

        public sealed class Fixture : IDisposable
        {
            public ScreenshotSandbox Sandbox { get; }
            public KsrSqliteConnectionFactory Connections { get; }
            public ScreenshotAppFactory Factory { get; }
            public HttpClient Client { get; }

            public Fixture()
            {
                var directory = new DirectoryInfo(AppContext.BaseDirectory);
                while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ksr.Local.sln")))
                {
                    directory = directory.Parent;
                }
                Sandbox = ScreenshotSandbox.Create(directory?.FullName ?? throw new InvalidOperationException("Kusto Slice Runner checkout not found."), Guid.NewGuid(), 5107);
                Connections = new KsrSqliteConnectionFactory(new KsrSqliteConnectionOptions(Sandbox.DatabasePath));
                new KsrSqliteSchema(Connections).EnsureSchema();
                ScreenshotDataset.Seed(Connections);
                Factory = new ScreenshotAppFactory(Sandbox);
                Factory.UseKestrel(0);
                Client = Factory.CreateClient();
            }

            public void Dispose()
            {
                Client.Dispose();
                Factory.Dispose();
                TestCleanup.DeleteDirectoryWithRetry(Sandbox.RunDirectory);
            }
        }
    }
}
