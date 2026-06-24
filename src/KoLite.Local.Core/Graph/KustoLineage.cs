namespace KoLite.Local.Core.Graph
{
    // A normalized "source reads dependency" edge within one Kusto cluster. Produced by the live
    // entity reader; consumed by the pure lineage engine here so the engine stays testable.
    public sealed record KustoEntityEdge(
        string SourceDatabase,
        string SourceName,
        string SourceType,
        string DependencyDatabase,
        string DependencyName,
        string DependencyType);

    // A KO Lite job whose Kusto output table we want to find downstream consumers of. FunctionName
    // and OutputTable let the engine recognize when a Kusto entity actually IS this job (so it maps
    // onto the existing job node instead of becoming a duplicate consumer node).
    public sealed record KustoJobOutput(string JobId, string Database, string OutputTable, string FunctionName);

    // A downstream consumer (Function or MaterializedView) to add to the graph.
    public sealed record KustoConsumerNode(string Key, string Database, string Name, string EntityType);

    // A produces->consumes edge. FromId is either a KO Lite job id (for a job's direct consumers)
    // or another consumer node Key; ToId is always a consumer node Key.
    public sealed record KustoConsumerEdge(string FromId, string ToId);

    public sealed record KustoLineageResult(IReadOnlyList<KustoConsumerNode> Nodes, IReadOnlyList<KustoConsumerEdge> Edges)
    {
        public static KustoLineageResult Empty { get; } = new(Array.Empty<KustoConsumerNode>(), Array.Empty<KustoConsumerEdge>());
    }

    // Pure downstream-consumer lineage. Given the cluster's entity edges and the jobs on that
    // cluster, it walks "who reads this" from each job's output table to surface the Functions and
    // MaterializedViews that consume job output (transitively, through function->function chains).
    // Pass-through tables (e.g. update-policy targets) are collapsed - bridged through but not drawn.
    // Consumers that are themselves KO Lite jobs are skipped here because the job->job relationship
    // is already shown by the job dependency graph.
    public static class KustoLineageEngine
    {
        public static KustoLineageResult ComputeConsumers(IEnumerable<KustoEntityEdge> edges, IEnumerable<KustoJobOutput> jobOutputs)
        {
            if (edges is null) throw new ArgumentNullException(nameof(edges));
            if (jobOutputs is null) throw new ArgumentNullException(nameof(jobOutputs));

            var jobs = jobOutputs.ToList();
            if (jobs.Count == 0)
            {
                return KustoLineageResult.Empty;
            }

            // Reverse adjacency: depKey -> the entities that read it. Entity types tracked per key.
            var consumersOf = new Dictionary<string, List<EntityRef>>(StringComparer.Ordinal);
            foreach (var edge in edges)
            {
                if (string.IsNullOrWhiteSpace(edge.SourceName) || string.IsNullOrWhiteSpace(edge.DependencyName))
                {
                    continue;
                }

                var depKey = Key(edge.DependencyDatabase, edge.DependencyName);
                var consumer = new EntityRef(Key(edge.SourceDatabase, edge.SourceName), edge.SourceDatabase, edge.SourceName, edge.SourceType);
                if (!consumersOf.TryGetValue(depKey, out var list))
                {
                    list = new List<EntityRef>();
                    consumersOf[depKey] = list;
                }

                list.Add(consumer);
            }

            // Which entity keys correspond to a KO Lite job (its output table or its function).
            var jobByEntityKey = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var job in jobs)
            {
                jobByEntityKey[Key(job.Database, job.OutputTable)] = job.JobId;
                if (!string.IsNullOrWhiteSpace(job.FunctionName))
                {
                    jobByEntityKey[Key(job.Database, job.FunctionName)] = job.JobId;
                }
            }

            var nodes = new Dictionary<string, KustoConsumerNode>(StringComparer.Ordinal);
            var edgeSet = new HashSet<KustoConsumerEdge>();
            var processed = new HashSet<(string ProducerId, string EntityKey)>();
            var work = new Stack<(string ProducerId, string EntityKey)>();

            foreach (var job in jobs)
            {
                work.Push((job.JobId, Key(job.Database, job.OutputTable)));
            }

            while (work.Count > 0)
            {
                var (producerId, entityKey) = work.Pop();
                if (!processed.Add((producerId, entityKey)))
                {
                    continue;
                }

                if (!consumersOf.TryGetValue(entityKey, out var consumers))
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
                        // Pass-through table (e.g. update-policy target): bridge through it.
                        work.Push((producerId, consumer.Key));
                        continue;
                    }

                    if (!IsFunctionLike(consumer.Type))
                    {
                        continue;
                    }

                    nodes.TryAdd(consumer.Key, new KustoConsumerNode(consumer.Key, consumer.Database, consumer.Name, consumer.Type));
                    edgeSet.Add(new KustoConsumerEdge(producerId, consumer.Key));
                    work.Push((consumer.Key, consumer.Key));
                }
            }

            var orderedNodes = nodes.Values
                .OrderBy(node => node.Database, StringComparer.OrdinalIgnoreCase)
                .ThenBy(node => node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var orderedEdges = edgeSet
                .OrderBy(edge => edge.FromId, StringComparer.Ordinal)
                .ThenBy(edge => edge.ToId, StringComparer.Ordinal)
                .ToList();

            return new KustoLineageResult(orderedNodes, orderedEdges);
        }

        private static bool IsTable(string entityType) => string.Equals(entityType, "Table", StringComparison.OrdinalIgnoreCase);

        private static bool IsFunctionLike(string entityType) =>
            string.Equals(entityType, "Function", StringComparison.OrdinalIgnoreCase)
            || string.Equals(entityType, "MaterializedView", StringComparison.OrdinalIgnoreCase);

        // Case-insensitive composite key (Kusto identifiers are case-insensitive). The separator is a
        // control character that cannot appear in a database or entity name.
        private static string Key(string database, string name) =>
            (database ?? string.Empty).ToLowerInvariant() + "\u0001" + (name ?? string.Empty).ToLowerInvariant();

        private sealed record EntityRef(string Key, string Database, string Name, string Type);
    }
}
