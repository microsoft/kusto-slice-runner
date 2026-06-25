using KoLite.Local.Core.Graph;

namespace KoLite.Local.Core.Tests
{
    public sealed class KustoLineageEngineTests
    {
        [Fact]
        public void Direct_consumer_function_is_linked_to_the_producing_job()
        {
            var result = KustoLineageEngine.ComputeConsumers(
                new[] { Edge("SampleAnalytics", "LatestT", "Function", "SampleAnalytics", "_T", "Table") },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            var node = Assert.Single(result.Nodes);
            Assert.Equal("LatestT", node.Name);
            Assert.Equal("Function", node.EntityType);
            Assert.Contains(new KustoConsumerEdge("job1", node.Key), result.Edges);
        }

        [Fact]
        public void Consumer_that_is_itself_a_job_is_skipped()
        {
            // BuildU is job2's function reading job1's output table; the job->job link is already in
            // the dependency graph, so it must not become a Kusto consumer node.
            var result = KustoLineageEngine.ComputeConsumers(
                new[] { Edge("SampleAnalytics", "BuildU", "Function", "SampleAnalytics", "_T", "Table") },
                new[]
                {
                    Job("job1", "SampleAnalytics", "_T", "BuildT"),
                    Job("job2", "SampleAnalytics", "_U", "BuildU")
                });

            Assert.Empty(result.Nodes);
            Assert.Empty(result.Edges);
        }

        [Fact]
        public void Pass_through_table_is_collapsed()
        {
            // _T2 is a non-job table (e.g. update-policy target) reading _T; F reads _T2.
            var result = KustoLineageEngine.ComputeConsumers(
                new[]
                {
                    Edge("SampleAnalytics", "_T2", "Table", "SampleAnalytics", "_T", "Table"),
                    Edge("SampleAnalytics", "F", "Function", "SampleAnalytics", "_T2", "Table")
                },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            var node = Assert.Single(result.Nodes);
            Assert.Equal("F", node.Name);
            Assert.DoesNotContain(result.Nodes, n => n.Name == "_T2");
            Assert.Contains(new KustoConsumerEdge("job1", node.Key), result.Edges);
        }

        [Fact]
        public void Function_to_function_chains_are_followed()
        {
            var result = KustoLineageEngine.ComputeConsumers(
                new[]
                {
                    Edge("SampleAnalytics", "F1", "Function", "SampleAnalytics", "_T", "Table"),
                    Edge("SampleAnalytics", "F2", "Function", "SampleAnalytics", "F1", "Function")
                },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            Assert.Equal(new[] { "F1", "F2" }, result.Nodes.Select(n => n.Name).OrderBy(n => n));
            Assert.Contains(new KustoConsumerEdge("job1", KeyOf(result, "F1")), result.Edges);
            Assert.Contains(new KustoConsumerEdge(KeyOf(result, "F1"), KeyOf(result, "F2")), result.Edges);
        }

        [Fact]
        public void Cross_database_consumer_on_the_same_cluster_is_found()
        {
            var result = KustoLineageEngine.ComputeConsumers(
                new[] { Edge("Usage", "CrossReader", "Function", "SampleAnalytics", "_T", "Table") },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            var node = Assert.Single(result.Nodes);
            Assert.Equal("CrossReader", node.Name);
            Assert.Equal("Usage", node.Database);
            Assert.Contains(new KustoConsumerEdge("job1", node.Key), result.Edges);
        }

        [Fact]
        public void Materialized_view_consumers_are_included()
        {
            var result = KustoLineageEngine.ComputeConsumers(
                new[] { Edge("SampleAnalytics", "MvView", "MaterializedView", "SampleAnalytics", "_T", "Table") },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            var node = Assert.Single(result.Nodes);
            Assert.Equal("MaterializedView", node.EntityType);
        }

        [Fact]
        public void No_jobs_yields_empty_result()
        {
            var result = KustoLineageEngine.ComputeConsumers(
                new[] { Edge("SampleAnalytics", "F", "Function", "SampleAnalytics", "_T", "Table") },
                Array.Empty<KustoJobOutput>());

            Assert.True(result.Nodes.Count == 0 && result.Edges.Count == 0);
        }

        [Fact]
        public void Cycles_terminate_and_surface_every_function()
        {
            var result = KustoLineageEngine.ComputeConsumers(
                new[]
                {
                    Edge("SampleAnalytics", "F1", "Function", "SampleAnalytics", "_T", "Table"),
                    Edge("SampleAnalytics", "F2", "Function", "SampleAnalytics", "F1", "Function"),
                    Edge("SampleAnalytics", "F1", "Function", "SampleAnalytics", "F2", "Function")
                },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            Assert.Equal(new[] { "F1", "F2" }, result.Nodes.Select(n => n.Name).OrderBy(n => n));
        }

        [Fact]
        public void Matching_is_case_insensitive()
        {
            // The job stores "_T" but Kusto returns the dependency as "_t".
            var result = KustoLineageEngine.ComputeConsumers(
                new[] { Edge("efficiency", "LatestT", "Function", "efficiency", "_t", "Table") },
                new[] { Job("job1", "SampleAnalytics", "_T", "BuildT") });

            Assert.Single(result.Nodes);
        }

        [Fact]
        public void Upstream_read_of_another_jobs_output_is_a_job_link()
        {
            var result = KustoLineageEngine.ComputeUpstream(
                new[] { new KustoEntityEdge("c1", "db", "BuildB", "Function", "c1", "db", "_A", "Table") },
                new[] { Job("jobB", "db", "_B", "BuildB") },
                new[] { Job("jobA", "db", "_A", "BuildA"), Job("jobB", "db", "_B", "BuildB") });

            Assert.Empty(result.SourceNodes);
            var link = Assert.Single(result.JobLinks);
            Assert.Equal("jobA", link.UpstreamJobId);
            Assert.Equal("jobB", link.DownstreamJobId);
        }

        [Fact]
        public void Upstream_non_job_read_is_a_source_node()
        {
            var result = KustoLineageEngine.ComputeUpstream(
                new[] { new KustoEntityEdge("c1", "db", "BuildB", "Function", "c1", "db", "RawTable", "Table") },
                new[] { Job("jobB", "db", "_B", "BuildB") });

            Assert.Empty(result.JobLinks);
            var node = Assert.Single(result.SourceNodes);
            Assert.Equal("RawTable", node.Name);
            Assert.Equal("Table", node.EntityType);
            Assert.False(node.IsRemote);
            Assert.Contains(new KustoConsumerEdge(node.Key, "jobB"), result.SourceEdges);
        }

        [Fact]
        public void Upstream_cross_cluster_source_is_flagged_remote()
        {
            var result = KustoLineageEngine.ComputeUpstream(
                new[] { new KustoEntityEdge("c1", "db", "BuildB", "Function", "remote.host", "fleet", "MetricsPerNode", "RemoteEntity") },
                new[] { Job("jobB", "db", "_B", "BuildB") });

            var node = Assert.Single(result.SourceNodes);
            Assert.Equal("MetricsPerNode", node.Name);
            Assert.Equal("remote.host", node.Cluster);
            Assert.True(node.IsRemote);
        }

        private static KustoEntityEdge Edge(string sourceDb, string sourceName, string sourceType, string depDb, string depName, string depType) =>
            new("c1", sourceDb, sourceName, sourceType, "c1", depDb, depName, depType);

        private static KustoJobOutput Job(string jobId, string database, string outputTable, string functionName) =>
            new(jobId, "c1", database, outputTable, functionName);

        private static string KeyOf(KustoLineageResult result, string name) =>
            result.Nodes.Single(n => n.Name == name).Key;
    }
}
