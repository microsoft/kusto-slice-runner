using KoLite.Local.Core.Graph;
using KoLite.Local.Kusto.Execution;
using KoLite.Local.Sqlite.Catalog;

namespace KoLite.LocalApp.Ui
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

            // The chain's resolved jobs are the ones whose output tables we look for consumers of.
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

            var kustoNodes = new List<KustoConsumerNode>();
            var kustoEdges = new List<KustoConsumerEdge>();

            foreach (var clusterGroup in chainRecords.GroupBy(record => ClusterKey(record.Definition.Target.ClusterUri), StringComparer.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(clusterGroup.Key, UriKind.Absolute, out var clusterUri))
                {
                    continue;
                }

                var connectionDatabase = clusterGroup.First().Definition.Target.Database;
                var edges = await reader.ReadAsync(clusterUri, connectionDatabase, cancellationToken).ConfigureAwait(false);

                var jobOutputs = clusterGroup
                    .Select(record => new KustoJobOutput(record.JobId, record.Definition.Target.Database, record.Definition.OutputTable, record.Definition.FunctionName))
                    .ToList();
                var jobIdsOnCluster = new HashSet<string>(clusterGroup.Select(record => record.JobId), StringComparer.Ordinal);

                var lineage = KustoLineageEngine.ComputeConsumers(edges, jobOutputs);
                var host = clusterUri.Host;

                foreach (var node in lineage.Nodes)
                {
                    kustoNodes.Add(node with { Key = Qualify(host, node.Key) });
                }

                foreach (var edge in lineage.Edges)
                {
                    // FromId is a KO Lite job id (global) for a direct consumer, otherwise a consumer
                    // key that must be cluster-qualified to match the qualified node keys above.
                    var from = jobIdsOnCluster.Contains(edge.FromId) ? edge.FromId : Qualify(host, edge.FromId);
                    kustoEdges.Add(new KustoConsumerEdge(from, Qualify(host, edge.ToId)));
                }
            }

            return query.Build(focal, kustoNodes, kustoEdges);
        }

        private static string ClusterKey(string clusterUri) =>
            Uri.TryCreate(clusterUri, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : clusterUri;

        private static string Qualify(string host, string key) => host + "\u0001" + key;
    }
}
