namespace KoLite.Local.Core.Graph
{
    // A directed dependency edge. The downstream job declares the upstream job in its dependsOn,
    // so data flows upstream -> downstream and the layout draws the upstream above the downstream.
    public readonly record struct DependencyEdge(string UpstreamId, string DownstreamId);

    // A node after layout. Layer is the topological rank (0 = a root with no upstream inside the
    // component, drawn at the top); Order is the deterministic left-to-right slot within the layer.
    public sealed record DependencyGraphPlacement(string Id, int Layer, int Order);

    public sealed record DependencyGraphLayout(
        IReadOnlyList<DependencyGraphPlacement> Nodes,
        IReadOnlyList<DependencyEdge> Edges,
        int LayerCount,
        int MaxLayerWidth);

    // Pure, UI-agnostic layout for the job dependency graph. Given the whole catalog's nodes and
    // edges plus a focal set, it extracts the connected component(s) that contain the focal nodes
    // (undirected reachability, so both transitive upstream and downstream are pulled in) and lays
    // them out in topological layers. The algorithm is defensive: unknown edge endpoints become
    // nodes, self-edges are dropped, and cycles are broken so a malformed catalog never throws.
    public static class DependencyGraphLayoutEngine
    {
        public static DependencyGraphLayout Build(
            IEnumerable<string> allNodeIds,
            IEnumerable<DependencyEdge> allEdges,
            IEnumerable<string> focalNodeIds)
        {
            if (allNodeIds is null) throw new ArgumentNullException(nameof(allNodeIds));
            if (allEdges is null) throw new ArgumentNullException(nameof(allEdges));
            if (focalNodeIds is null) throw new ArgumentNullException(nameof(focalNodeIds));

            var focal = new HashSet<string>(focalNodeIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);

            // Universe of nodes: declared nodes, both endpoints of every edge, and the focal set.
            var universe = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in allNodeIds)
            {
                if (!string.IsNullOrWhiteSpace(id)) universe.Add(id);
            }

            var edges = new HashSet<DependencyEdge>();
            foreach (var edge in allEdges)
            {
                if (string.IsNullOrWhiteSpace(edge.UpstreamId) || string.IsNullOrWhiteSpace(edge.DownstreamId)) continue;
                if (StringComparer.Ordinal.Equals(edge.UpstreamId, edge.DownstreamId)) continue; // drop self-edges
                universe.Add(edge.UpstreamId);
                universe.Add(edge.DownstreamId);
                edges.Add(edge);
            }

            foreach (var id in focal) universe.Add(id);

            // Undirected adjacency for component discovery; directed upstream adjacency for layering.
            var undirected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var upstreamsOf = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var node in universe)
            {
                undirected[node] = new HashSet<string>(StringComparer.Ordinal);
                upstreamsOf[node] = new HashSet<string>(StringComparer.Ordinal);
            }

            foreach (var edge in edges)
            {
                undirected[edge.UpstreamId].Add(edge.DownstreamId);
                undirected[edge.DownstreamId].Add(edge.UpstreamId);
                upstreamsOf[edge.DownstreamId].Add(edge.UpstreamId);
            }

            var component = DiscoverComponent(focal, universe, undirected);
            if (component.Count == 0)
            {
                return new DependencyGraphLayout(Array.Empty<DependencyGraphPlacement>(), Array.Empty<DependencyEdge>(), 0, 0);
            }

            var componentEdges = edges
                .Where(edge => component.Contains(edge.UpstreamId) && component.Contains(edge.DownstreamId))
                .OrderBy(edge => edge.UpstreamId, StringComparer.Ordinal)
                .ThenBy(edge => edge.DownstreamId, StringComparer.Ordinal)
                .ToList();

            var layers = AssignLayers(component, upstreamsOf);
            var placements = OrderWithinLayers(component, layers, upstreamsOf);

            var layerCount = placements.Count == 0 ? 0 : placements.Max(p => p.Layer) + 1;
            var maxLayerWidth = placements.Count == 0
                ? 0
                : placements.GroupBy(p => p.Layer).Max(group => group.Count());

            return new DependencyGraphLayout(placements, componentEdges, layerCount, maxLayerWidth);
        }

        private static HashSet<string> DiscoverComponent(
            HashSet<string> focal,
            HashSet<string> universe,
            IReadOnlyDictionary<string, HashSet<string>> undirected)
        {
            var component = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (var seed in focal.Where(universe.Contains).OrderBy(id => id, StringComparer.Ordinal))
            {
                if (component.Add(seed)) queue.Enqueue(seed);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var neighbor in undirected[current])
                {
                    if (component.Add(neighbor)) queue.Enqueue(neighbor);
                }
            }

            return component;
        }

        // Longest-path layering: layer(node) = 1 + max(layer(upstream)), with roots at layer 0.
        // Cycle-safe: a back-edge to a node still on the recursion stack contributes nothing, which
        // breaks the cycle without throwing or looping forever.
        private static Dictionary<string, int> AssignLayers(
            HashSet<string> component,
            IReadOnlyDictionary<string, HashSet<string>> upstreamsOf)
        {
            var layers = new Dictionary<string, int>(StringComparer.Ordinal);
            var onStack = new HashSet<string>(StringComparer.Ordinal);

            int Layer(string node)
            {
                if (layers.TryGetValue(node, out var cached)) return cached;
                if (!onStack.Add(node)) return 0; // back-edge: break the cycle

                var layer = 0;
                foreach (var upstream in upstreamsOf[node])
                {
                    if (!component.Contains(upstream)) continue;
                    layer = Math.Max(layer, Layer(upstream) + 1);
                }

                onStack.Remove(node);
                layers[node] = layer;
                return layer;
            }

            foreach (var node in component.OrderBy(id => id, StringComparer.Ordinal))
            {
                Layer(node);
            }

            return layers;
        }

        // Deterministic crossing reduction via a single downward barycenter sweep. Layer 0 is sorted
        // by id; every later node sorts by the average Order of its already-placed upstreams (every
        // non-root has at least one upstream in an earlier layer), with id as the stable tie-break.
        private static List<DependencyGraphPlacement> OrderWithinLayers(
            HashSet<string> component,
            IReadOnlyDictionary<string, int> layers,
            IReadOnlyDictionary<string, HashSet<string>> upstreamsOf)
        {
            var byLayer = component
                .GroupBy(node => layers[node])
                .OrderBy(group => group.Key)
                .ToList();

            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            var placements = new List<DependencyGraphPlacement>(component.Count);

            foreach (var group in byLayer)
            {
                var layer = group.Key;
                var ordered = group
                    .OrderBy(node => Barycenter(node, order, upstreamsOf, component))
                    .ThenBy(node => node, StringComparer.Ordinal)
                    .ToList();

                for (var slot = 0; slot < ordered.Count; slot++)
                {
                    order[ordered[slot]] = slot;
                    placements.Add(new DependencyGraphPlacement(ordered[slot], layer, slot));
                }
            }

            return placements;
        }

        private static double Barycenter(
            string node,
            IReadOnlyDictionary<string, int> order,
            IReadOnlyDictionary<string, HashSet<string>> upstreamsOf,
            HashSet<string> component)
        {
            var placedUpstreamOrders = upstreamsOf[node]
                .Where(component.Contains)
                .Where(order.ContainsKey)
                .Select(upstream => order[upstream])
                .ToList();

            return placedUpstreamOrders.Count == 0 ? 0d : placedUpstreamOrders.Average();
        }
    }
}
