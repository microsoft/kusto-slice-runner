// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Ksr.Local.Core.Graph;
using Ksr.Local.Core.Schedules;
using Ksr.Local.Kusto.Execution;
using Ksr.Local.Sqlite.Catalog;

namespace Ksr.LocalApp.Ui
{
    // Enriches the job dependency graph with the downstream Kusto functions / materialized views that
    // consume each job's output table. It builds the base job graph, runs the read-only entity reader
    // once per distinct cluster in the focal chain, computes consumers via the pure Core lineage
    // engine, and re-lays out the combined graph. This is the only path that contacts Kusto outside
    // the scheduler/worker, and only when explicitly invoked.
    public sealed class DependencyGraphKustoEnricher
    {
        private readonly DependencyGraphQuery query;
        private readonly SqliteJobCatalogRepository catalog;
        private readonly IKustoEntityDependencyReader reader;

        public DependencyGraphKustoEnricher(
            DependencyGraphQuery query,
            SqliteJobCatalogRepository catalog,
            IKustoEntityDependencyReader reader)
        {
            this.query = query ?? throw new ArgumentNullException(nameof(query));
            this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        public async Task<DependencyGraphViewModel> EnrichAsync(IEnumerable<string> focalJobIds, CancellationToken cancellationToken = default)
        {
            if (focalJobIds is null) throw new ArgumentNullException(nameof(focalJobIds));

            var focal = focalJobIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            var baseGraph = query.Build(focal);
            if (baseGraph.IsEmpty)
            {
                return baseGraph;
            }

            // The chain's resolved jobs are the ones whose lineage we resolve. All of them become
            // engine inputs (cluster-qualified) so cross-cluster reads can be matched to a KSR job.
            var chainRecords = baseGraph.Nodes
                .Where(node => node.Kind == "Job" && node.Resolved)
                .Select(node => catalog.Get(node.Id))
                .Where(record => record is not null)
                .Select(record => record!)
                .ToList();
            if (chainRecords.Count == 0)
            {
                return baseGraph;
            }

            var jobOutputs = chainRecords
                .Select(record => new KustoJobOutput(
                    record.JobId,
                    ClusterHost(record.Definition.Target.ClusterUri),
                    record.Definition.Target.Database,
                    record.Definition.OutputTable,
                    record.Definition.FunctionName))
                .ToList();

            // Match reads/consumers against every catalog job (not just the chain) so an undeclared
            // upstream that is not in the focal chain is still recognized as a KSR job.
            var allJobOutputs = new List<KustoJobOutput>();
            foreach (var record in catalog.List())
            {
                JobDefinition definition;
                try
                {
                    definition = record.Definition;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                allJobOutputs.Add(new KustoJobOutput(
                    record.JobId,
                    ClusterHost(definition.Target.ClusterUri),
                    definition.Target.Database,
                    definition.OutputTable,
                    definition.FunctionName));
            }

            var declaredDeps = chainRecords.ToDictionary(
                record => record.JobId,
                record => new HashSet<string>(
                    record.Definition.DependsOn.Where(d => !string.IsNullOrWhiteSpace(d.Id)).Select(d => d.Id!),
                    StringComparer.Ordinal),
                StringComparer.Ordinal);

            var nodes = new Dictionary<string, KustoConsumerNode>(StringComparer.Ordinal);
            var edges = new HashSet<KustoConsumerEdge>();
            var implicitEdges = new HashSet<KustoConsumerEdge>();

            foreach (var clusterGroup in chainRecords.GroupBy(record => ClusterKey(record.Definition.Target.ClusterUri), StringComparer.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(clusterGroup.Key, UriKind.Absolute, out var clusterUri))
                {
                    continue;
                }

                var connectionDatabase = clusterGroup.First().Definition.Target.Database;
                var clusterEdges = await reader.ReadAsync(clusterUri, connectionDatabase, cancellationToken).ConfigureAwait(false);

                // Downstream consumers of job outputs.
                var consumers = KustoLineageEngine.ComputeConsumers(clusterEdges, jobOutputs, allJobOutputs);
                foreach (var node in consumers.Nodes) nodes.TryAdd(node.Key, node);
                foreach (var edge in consumers.Edges) edges.Add(edge);

                // Upstream: source nodes a job's function reads + discovered job->job links.
                var upstream = KustoLineageEngine.ComputeUpstream(clusterEdges, jobOutputs, allJobOutputs);
                foreach (var node in upstream.SourceNodes) nodes.TryAdd(node.Key, node);
                foreach (var edge in upstream.SourceEdges) edges.Add(edge);
                foreach (var link in upstream.JobLinks)
                {
                    var declared = declaredDeps.TryGetValue(link.DownstreamJobId, out var deps) && deps.Contains(link.UpstreamJobId);
                    if (!declared)
                    {
                        implicitEdges.Add(new KustoConsumerEdge(link.UpstreamJobId, link.DownstreamJobId));
                    }
                }
            }

            return query.Build(focal, nodes.Values.ToList(), edges.ToList(), implicitEdges.ToList());
        }

        private static string ClusterKey(string clusterUri) =>
            Uri.TryCreate(clusterUri, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : clusterUri;

        private static string ClusterHost(string clusterUri) =>
            Uri.TryCreate(clusterUri, UriKind.Absolute, out var uri) ? uri.Host : clusterUri;
    }
}
