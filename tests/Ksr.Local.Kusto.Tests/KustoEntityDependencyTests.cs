// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Data;
using Kusto.Data.Common;
using Ksr.Local.Kusto.Execution;

namespace Ksr.Local.Kusto.Tests
{
    public sealed class KustoEntityDependencyTests
    {
        private const string Host = "sample-data.centralus.kusto.windows.net";

        [Fact]
        public void Bare_dependency_resolves_to_the_source_database()
        {
            var parsed = KustoSdkEntityDependencyReader.ParseDependency("_Ecu5MinProfile", "SampleAnalytics", Host);

            Assert.NotNull(parsed);
            Assert.Equal("SampleAnalytics", parsed!.Value.Database);
            Assert.Equal("_Ecu5MinProfile", parsed.Value.Name);
        }

        [Fact]
        public void Database_qualified_dependency_resolves_to_that_database()
        {
            var parsed = KustoSdkEntityDependencyReader.ParseDependency("database('Usage').UserDaily", "SampleAnalytics", Host);

            Assert.NotNull(parsed);
            Assert.Equal("Usage", parsed!.Value.Database);
            Assert.Equal("UserDaily", parsed.Value.Name);
        }

        [Fact]
        public void Same_cluster_qualified_dependency_is_kept()
        {
            var parsed = KustoSdkEntityDependencyReader.ParseDependency(
                "cluster('https://sample-data.centralus.kusto.windows.net/').database('DemoMetrics').Foo",
                "SampleAnalytics",
                Host);

            Assert.NotNull(parsed);
            Assert.Equal("DemoMetrics", parsed!.Value.Database);
            Assert.Equal("Foo", parsed.Value.Name);
        }

        [Fact]
        public void Cross_cluster_dependency_is_kept_with_its_cluster()
        {
            var parsed = KustoSdkEntityDependencyReader.ParseDependency(
                "cluster('https://sample-compute.centralus.kusto.windows.net/').database('compute').fetchMetrics",
                "SampleAnalytics",
                Host);

            Assert.NotNull(parsed);
            Assert.Equal("sample-compute.centralus.kusto.windows.net", parsed!.Value.Cluster);
            Assert.Equal("compute", parsed.Value.Database);
            Assert.Equal("fetchMetrics", parsed.Value.Name);
        }

        [Fact]
        public void Wrapped_table_reference_is_unwrapped()
        {
            var parsed = KustoSdkEntityDependencyReader.ParseDependency("database('X').table('_T')", "SampleAnalytics", Host);

            Assert.NotNull(parsed);
            Assert.Equal("X", parsed!.Value.Database);
            Assert.Equal("_T", parsed.Value.Name);
        }

        [Fact]
        public void Parse_keeps_local_and_cross_cluster_edges_with_their_clusters()
        {
            using var table = EdgeTable(
                ("SampleAnalytics", "BuildEcu5MinProfile", "Function", "_NodeCpu5Min", "Table"),
                ("SampleAnalytics", "LatestEcu5MinProfile", "Function", "_Ecu5MinProfile", "Table"),
                ("SampleAnalytics", "BuildNodeCpu5Min", "Function", "cluster('https://sample-fleet.centralus.kusto.windows.net/').database('fleet').MetricsPerNode", "RemoteEntity"));

            var edges = KustoSdkEntityDependencyReader.Parse(table.CreateDataReader(), Host);

            Assert.Equal(3, edges.Count);
            Assert.All(edges, e => Assert.Equal(Host, e.SourceCluster));
            Assert.Contains(edges, e => e.SourceName == "BuildEcu5MinProfile" && e.DependencyName == "_NodeCpu5Min" && e.DependencyCluster == Host && e.DependencyDatabase == "SampleAnalytics");
            var remote = Assert.Single(edges, e => e.SourceName == "BuildNodeCpu5Min");
            Assert.Equal("sample-fleet.centralus.kusto.windows.net", remote.DependencyCluster);
            Assert.Equal("fleet", remote.DependencyDatabase);
            Assert.Equal("MetricsPerNode", remote.DependencyName);
        }

        [Fact]
        public async Task ReadAsync_connects_through_the_supplied_database_and_returns_edges()
        {
            using var table = EdgeTable(("SampleAnalytics", "LatestEcu5MinProfile", "Function", "_Ecu5MinProfile", "Table"));
            var factory = new FakeFactory(table);
            var reader = new KustoSdkEntityDependencyReader(factory);

            var edges = await reader.ReadAsync(new Uri("https://sample-data.centralus.kusto.windows.net"), "SampleAnalytics");

            var edge = Assert.Single(edges);
            Assert.Equal("LatestEcu5MinProfile", edge.SourceName);
            Assert.Equal("_Ecu5MinProfile", edge.DependencyName);
            Assert.Equal("SampleAnalytics", factory.LastDatabase);
            Assert.Contains(".show databases entities", factory.LastCommand);
        }

        private static DataTable EdgeTable(params (string SourceDb, string SourceName, string SourceType, string DepName, string DepType)[] rows)
        {
            var table = new DataTable();
            table.Columns.Add("SourceDatabase", typeof(string));
            table.Columns.Add("SourceName", typeof(string));
            table.Columns.Add("SourceType", typeof(string));
            table.Columns.Add("DepName", typeof(string));
            table.Columns.Add("DepType", typeof(string));
            foreach (var row in rows)
            {
                table.Rows.Add(row.SourceDb, row.SourceName, row.SourceType, row.DepName, row.DepType);
            }

            return table;
        }

        private sealed class FakeFactory : IKustoControlCommandClientFactory
        {
            private readonly DataTable table;
            public string? LastDatabase { get; private set; }
            public string? LastCommand { get; private set; }

            public FakeFactory(DataTable table) => this.table = table;

            public IKustoControlCommandClient Create(KustoExecutionRequest request) => throw new NotSupportedException();

            public IKustoControlCommandClient CreateForDatabase(Uri clusterUri, string database) =>
                new FakeClient(table, command => LastCommand = command, db => LastDatabase = db);

            public KsrKustoConnectionDescriptor Describe(KustoExecutionRequest request) => throw new NotSupportedException();
        }

        private sealed class FakeClient : IKustoControlCommandClient
        {
            private readonly DataTable table;
            private readonly Action<string> recordCommand;
            private readonly Action<string> recordDatabase;

            public FakeClient(DataTable table, Action<string> recordCommand, Action<string> recordDatabase)
            {
                this.table = table;
                this.recordCommand = recordCommand;
                this.recordDatabase = recordDatabase;
            }

            public Task<IDataReader> ExecuteControlCommandAsync(string database, string commandText, ClientRequestProperties properties, CancellationToken cancellationToken)
            {
                recordDatabase(database);
                recordCommand(commandText);
                return Task.FromResult<IDataReader>(table.CreateDataReader());
            }

            public void Dispose()
            {
            }
        }
    }
}
