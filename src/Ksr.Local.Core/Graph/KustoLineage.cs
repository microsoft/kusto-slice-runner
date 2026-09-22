// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace Ksr.Local.Core.Graph
{
    // A normalized "source reads dependency" edge from a cluster's resolved entity graph. Both
    // endpoints carry their cluster host so cross-cluster reads are first-class: a job's upstream
    // source can live on another cluster (it is named directly in the function's dependencies).
    public sealed record KustoEntityEdge(
        string SourceCluster,
        string SourceDatabase,
        string SourceName,
        string SourceType,
        string DependencyCluster,
        string DependencyDatabase,
        string DependencyName,
        string DependencyType);

    // A Kusto Slice Runner job whose Kusto entities we reason about. FunctionName/OutputTable let the engine
    // recognize when a Kusto entity IS this job (so it maps onto the job node instead of becoming a
    // duplicate consumer/source node, and so an upstream read of another job's table becomes a
    // job->job link).
    public sealed record KustoJobOutput(string JobId, string Cluster, string Database, string OutputTable, string FunctionName);

    // A non-job Kusto entity to add to the graph: a downstream consumer (Function/MaterializedView)
    // or an upstream source (Table/Function/MaterializedView). IsRemote is true for a source on a
    // different cluster than the job that reads it. Key is globally unique (includes the cluster).
    public sealed record KustoConsumerNode(string Key, string Cluster, string Database, string Name, string EntityType, bool IsRemote);

    // A produces->consumes edge. FromId is either a Kusto Slice Runner job id or another node Key; ToId is a
    // node Key (downstream consumer) or a job id (upstream source -> job).
    public sealed record KustoConsumerEdge(string FromId, string ToId);

    // A discovered job->job relationship: the downstream job's function reads the upstream job's
    // output. The caller classifies it as declared (already in dependsOn) or implicit (undeclared).
    public sealed record KustoJobLink(string UpstreamJobId, string DownstreamJobId);

    public sealed record KustoLineageResult(IReadOnlyList<KustoConsumerNode> Nodes, IReadOnlyList<KustoConsumerEdge> Edges)
    {
        public static KustoLineageResult Empty { get; } = new(Array.Empty<KustoConsumerNode>(), Array.Empty<KustoConsumerEdge>());
    }

    public sealed record KustoUpstreamResult(
        IReadOnlyList<KustoConsumerNode> SourceNodes,
        IReadOnlyList<KustoConsumerEdge> SourceEdges,
        IReadOnlyList<KustoJobLink> JobLinks)
    {
        public static KustoUpstreamResult Empty { get; } = new(
            Array.Empty<KustoConsumerNode>(),
            Array.Empty<KustoConsumerEdge>(),
            Array.Empty<KustoJobLink>());
    }

    // Pure, cluster-aware lineage around Kusto Slice Runner jobs.
    //
    // ComputeConsumers (downstream): walks "who reads this" from each job's output table to surface
    // the Functions/MaterializedViews that consume job output (transitively, through function chains).
    // Pass-through tables (e.g. update-policy targets) are bridged through but not drawn. Consumers
    // that are themselves Kusto Slice Runner jobs are skipped (that link is already the job dependency graph).
    //
    // ComputeUpstream: for each job's function, classifies its DIRECT reads (one hop). A read of
    // another KSR job's output table/function becomes a job->job link; any other read becomes a source
    // node (table/function/view, possibly on another cluster).
    public static class KustoLineageEngine
    {
        public static KustoLineageResult ComputeConsumers(IEnumerable<KustoEntityEdge> edges, IEnumerable<KustoJobOutput> focusJobs, IEnumerable<KustoJobOutput>? allJobs = null)
        {
            if (edges is null) throw new ArgumentNullException(nameof(edges));
            if (focusJobs is null) throw new ArgumentNullException(nameof(focusJobs));

            var focus = focusJobs.ToList();
            if (focus.Count == 0)
            {
                return KustoLineageResult.Empty;
            }

            var consumersOf = new Dictionary<string, List<EntityRef>>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (string.IsNullOrWhiteSpace(edge.SourceName) || string.IsNullOrWhiteSpace(edge.DependencyName))
                {
                    continue;
                }

                var depKey = Key(edge.DependencyCluster, edge.DependencyDatabase, edge.DependencyName);
                var consumer = new EntityRef(
                    Key(edge.SourceCluster, edge.SourceDatabase, edge.SourceName),
                    edge.SourceCluster,
                    edge.SourceDatabase,
                    edge.SourceName,
                    edge.SourceType);
                AddTo(consumersOf, depKey, consumer);
            }

            var jobByEntityKey = BuildJobEntityMap(allJobs ?? focus);

            var nodes = new Dictionary<string, KustoConsumerNode>(StringComparer.Ordinal);
            var edgeSet = new HashSet<KustoConsumerEdge>();
            var processed = new HashSet<(string ProducerId, string EntityKey)>();
            var work = new Stack<(string ProducerId, string EntityKey)>();

            foreach (var job in focus)
            {
                work.Push((job.JobId, Key(job.Cluster, job.Database, job.OutputTable)));
            }

            while (work.Count > 0)
            {
                var (producerId, entityKey) = work.Pop();
                if (!processed.Add((producerId, entityKey)) || !consumersOf.TryGetValue(entityKey, out var consumers))
                {
                    continue;
                }

                foreach (var consumer in consumers)
                {
                    if (jobByEntityKey.ContainsKey(consumer.Key))
                    {
                        continue; // downstream job - already represented by the job dependency graph
                    }

                    if (IsTable(consumer.Type))
                    {
                        work.Push((producerId, consumer.Key)); // bridge through a pass-through table
                        continue;
                    }

                    if (!IsFunctionLike(consumer.Type))
                    {
                        continue;
                    }

                    nodes.TryAdd(consumer.Key, new KustoConsumerNode(consumer.Key, consumer.Cluster, consumer.Database, consumer.Name, consumer.Type, IsRemote: false));
                    edgeSet.Add(new KustoConsumerEdge(producerId, consumer.Key));
                    work.Push((consumer.Key, consumer.Key));
                }
            }

            return new KustoLineageResult(OrderNodes(nodes.Values), OrderEdges(edgeSet));
        }

        public static KustoUpstreamResult ComputeUpstream(IEnumerable<KustoEntityEdge> edges, IEnumerable<KustoJobOutput> focusJobs, IEnumerable<KustoJobOutput>? allJobs = null)
        {
            if (edges is null) throw new ArgumentNullException(nameof(edges));
            if (focusJobs is null) throw new ArgumentNullException(nameof(focusJobs));

            var focus = focusJobs.ToList();
            if (focus.Count == 0)
            {
                return KustoUpstreamResult.Empty;
            }

            var readsOf = new Dictionary<string, List<EntityRef>>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (string.IsNullOrWhiteSpace(edge.SourceName) || string.IsNullOrWhiteSpace(edge.DependencyName))
                {
                    continue;
                }

                var sourceKey = Key(edge.SourceCluster, edge.SourceDatabase, edge.SourceName);
                var dependency = new EntityRef(
                    Key(edge.DependencyCluster, edge.DependencyDatabase, edge.DependencyName),
                    edge.DependencyCluster,
                    edge.DependencyDatabase,
                    edge.DependencyName,
                    edge.DependencyType);
                AddTo(readsOf, sourceKey, dependency);
            }

            var jobByEntityKey = BuildJobEntityMap(allJobs ?? focus);

            var sourceNodes = new Dictionary<string, KustoConsumerNode>(StringComparer.Ordinal);
            var sourceEdges = new HashSet<KustoConsumerEdge>();
            var jobLinks = new HashSet<KustoJobLink>();

            foreach (var job in focus)
            {
                if (string.IsNullOrWhiteSpace(job.FunctionName))
                {
                    continue;
                }

                var functionKey = Key(job.Cluster, job.Database, job.FunctionName);
                if (!readsOf.TryGetValue(functionKey, out var reads))
                {
                    continue;
                }

                foreach (var read in reads)
                {
                    if (jobByEntityKey.TryGetValue(read.Key, out var upstreamJobId))
                    {
                        if (!StringComparer.Ordinal.Equals(upstreamJobId, job.JobId))
                        {
                            jobLinks.Add(new KustoJobLink(upstreamJobId, job.JobId));
                        }

                        continue;
                    }

                    var isRemote = !StringComparer.OrdinalIgnoreCase.Equals(read.Cluster, job.Cluster);
                    sourceNodes.TryAdd(read.Key, new KustoConsumerNode(read.Key, read.Cluster, read.Database, read.Name, read.Type, isRemote));
                    sourceEdges.Add(new KustoConsumerEdge(read.Key, job.JobId));
                }
            }

            return new KustoUpstreamResult(
                OrderNodes(sourceNodes.Values),
                OrderEdges(sourceEdges),
                jobLinks.OrderBy(link => link.UpstreamJobId, StringComparer.Ordinal).ThenBy(link => link.DownstreamJobId, StringComparer.Ordinal).ToList());
        }

        private static Dictionary<string, string> BuildJobEntityMap(IEnumerable<KustoJobOutput> jobs)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var job in jobs)
            {
                map[Key(job.Cluster, job.Database, job.OutputTable)] = job.JobId;
                if (!string.IsNullOrWhiteSpace(job.FunctionName))
                {
                    map[Key(job.Cluster, job.Database, job.FunctionName)] = job.JobId;
                }
            }

            return map;
        }

        private static void AddTo(Dictionary<string, List<EntityRef>> map, string key, EntityRef value)
        {
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<EntityRef>();
                map[key] = list;
            }

            list.Add(value);
        }

        private static IReadOnlyList<KustoConsumerNode> OrderNodes(IEnumerable<KustoConsumerNode> nodes) =>
            nodes
                .OrderBy(node => node.Cluster, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Database, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        private static IReadOnlyList<KustoConsumerEdge> OrderEdges(IEnumerable<KustoConsumerEdge> edges) =>
            edges
                .OrderBy(edge => edge.FromId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToId, StringComparer.Ordinal)
                .ToList();

        private static bool IsTable(string entityType) => string.Equals(entityType, "Table", StringComparison.OrdinalIgnoreCase);

        private static bool IsFunctionLike(string entityType) =>
            string.Equals(entityType, "Function", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "MaterializedView", StringComparison.OrdinalIgnoreCase);

        // Case-insensitive composite key (Kusto identifiers are case-insensitive). The separator is a
        // control character that cannot appear in a cluster, database, or entity name.
        private static string Key(string cluster, string database, string name) =>
            (cluster ?? string.Empty).ToLowerInvariant() + "\u0001" +
            (database ?? string.Empty).ToLowerInvariant() + "\u0001" +
            (name ?? string.Empty).ToLowerInvariant();

        private sealed record EntityRef(string Key, string Cluster, string Database, string Name, string Type);
    }
}
