using KoLite.Local.Core.Graph;
using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp.Ui
{
    // Per-node slice counts shown in the hover tooltip. Null on the view model when the job has no
    // recorded slices yet (or for an unresolved placeholder node).
    public sealed record DependencyGraphCounts(
        int Missing,
        int Queued,
        int Running,
        int Completed,
        int Failed,
        int DeadLettered,
        int DependencyBlocked,
        int Total);

    // A laid-out node. Resolved is false for an id that is referenced as a dependency but is not in
    // the catalog (e.g. a deleted upstream), so the UI can render it as a muted placeholder. Focal
    // marks the jobs the user explicitly selected/opened, so they can be highlighted. Kind is "Job"
    // for KO Lite jobs, or "KustoFunction" / "KustoMaterializedView" for resolved downstream Kusto
    // consumers.
    public sealed record DependencyGraphNodeViewModel(
        string Id,
        string Label,
        string Status,
        string StatusText,
        string StatusCss,
        string Kind,
        bool Resolved,
        bool Focal,
        string? Href,
        int Layer,
        int Order,
        double X,
        double Y,
        double Width,
        double Height,
        DependencyGraphCounts? Counts,
        IReadOnlyList<string> Lines);

    public sealed record DependencyGraphEdgeViewModel(string FromId, string ToId, bool Implicit = false);

    public sealed record DependencyGraphLegendItem(string Status, string Label, string StatusCss);

    // Wraps the graph for the shared partial: the focal job ids are needed so the "Resolve Kusto
    // consumers" button can post them back to the enrichment endpoint.
    public sealed record DependencyGraphPanelViewModel(DependencyGraphViewModel Graph, IReadOnlyList<string> FocalJobIds);

    public sealed record DependencyGraphViewModel(
        IReadOnlyList<DependencyGraphNodeViewModel> Nodes,
        IReadOnlyList<DependencyGraphEdgeViewModel> Edges,
        double Width,
        double Height,
        int FocalCount,
        int ResolvedFocalCount,
        IReadOnlyList<DependencyGraphLegendItem> Legend)
    {
        public bool IsEmpty => Nodes.Count == 0;

        public static DependencyGraphViewModel Empty { get; } = new(
            Array.Empty<DependencyGraphNodeViewModel>(),
            Array.Empty<DependencyGraphEdgeViewModel>(),
            0,
            0,
            0,
            0,
            Array.Empty<DependencyGraphLegendItem>());
    }

    // Builds the dependency graph view model for a focal set of jobs. It reuses the dashboard's
    // status projection for every job so node colors match the rest of the UI, derives dependency
    // edges from each job's stored dependsOn (upstream GUIDs), and asks the pure Core layout engine
    // for the connected component(s) and their topological placement before mapping to pixels.
    public sealed class DependencyGraphQuery
    {
        // SVG geometry. Kept here (not in Core) because pixels are a presentation concern. Node height
        // is variable - it grows to fit a wrapped, multi-line label - so each layer is spaced by the
        // tallest node it contains.
        private const double NodeWidth = 230;
        private const double HorizontalGap = 30;
        private const double VerticalGap = 52;
        private const double Padding = 28;
        private const double LabelLineHeight = 16;
        private const double StatusLineHeight = 14;
        private const double NodeVerticalPadding = 11;
        private const double MinNodeHeight = 48;

        // Label wrap width, tuned to NodeWidth (230) and the Segoe UI 13px bold label font.
        // Measured glyph widths show the longest real entity names fit one line within the box
        // (e.g. a 32-char name is ~221px < 230px), so 32 keeps them on one line - shrinking the
        // effective horizontal padding toward the ~11px vertical padding - while still hard-splitting
        // any single chunk longer than a line. Going higher risks overflow for wide-glyph names.
        private const int WrapCharsPerLine = 32;
        private const int MaxLabelLines = 3;

        private static readonly IReadOnlyList<string> LegendStatusOrder = new[]
        {
            "Healthy", "Running", "DependencyBlocked", "Failed", "Paused", "Completed", "SoftDeleted", "Unknown",
            "KustoFunction", "KustoMaterializedView", "KustoTable", "KustoExternal"
        };

        private readonly DashboardPageQuery dashboard;

        public DependencyGraphQuery(DashboardPageQuery dashboard) => this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));

        public DependencyGraphViewModel Build(IEnumerable<string> focalJobIds) =>
            Build(focalJobIds, Array.Empty<KustoConsumerNode>(), Array.Empty<KustoConsumerEdge>(), Array.Empty<KustoConsumerEdge>());

        public DependencyGraphViewModel Build(
            IEnumerable<string> focalJobIds,
            IReadOnlyList<KustoConsumerNode> kustoNodes,
            IReadOnlyList<KustoConsumerEdge> kustoEdges) =>
            Build(focalJobIds, kustoNodes, kustoEdges, Array.Empty<KustoConsumerEdge>());

        // Builds the graph, optionally enriched with downstream Kusto consumer nodes/edges, upstream
        // source nodes/edges, and implicit job->job edges (a job's function reads another KO job's
        // output but doesn't declare it). Kusto/source edges connect a job id or a node key to a node
        // key or job id; implicit job edges connect two job ids and are flagged so the UI can draw them
        // distinctly from declared dependsOn edges. All extend the same connected-component layout.
        public DependencyGraphViewModel Build(
            IEnumerable<string> focalJobIds,
            IReadOnlyList<KustoConsumerNode> kustoNodes,
            IReadOnlyList<KustoConsumerEdge> kustoEdges,
            IReadOnlyList<KustoConsumerEdge> implicitJobEdges)
        {
            if (focalJobIds is null) throw new ArgumentNullException(nameof(focalJobIds));
            if (kustoNodes is null) throw new ArgumentNullException(nameof(kustoNodes));
            if (kustoEdges is null) throw new ArgumentNullException(nameof(kustoEdges));
            if (implicitJobEdges is null) throw new ArgumentNullException(nameof(implicitJobEdges));

            var requestedFocal = focalJobIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
            if (requestedFocal.Count == 0)
            {
                return DependencyGraphViewModel.Empty;
            }

            var allJobs = dashboard.GetAllJobs();

            // Soft-deleted jobs are excluded from the graph entirely: they are not drawn, their
            // edges are dropped, and a soft-deleted focal selection is ignored (rather than shown as
            // an unresolved placeholder). Genuinely unknown upstream ids are still surfaced as
            // placeholders below so a dangling dependency stays visible.
            var softDeletedIds = new HashSet<string>(
                allJobs.Where(job => job.LifecycleStatus == "SoftDeleted").Select(job => job.Record.JobId),
                StringComparer.Ordinal);

            var focal = new HashSet<string>(
                requestedFocal.Where(id => !softDeletedIds.Contains(id)),
                StringComparer.Ordinal);
            if (focal.Count == 0)
            {
                return DependencyGraphViewModel.Empty;
            }

            var jobs = allJobs.Where(job => job.LifecycleStatus != "SoftDeleted").ToList();
            var byId = jobs.ToDictionary(job => job.Record.JobId, StringComparer.Ordinal);
            var byKusto = kustoNodes
                .GroupBy(node => node.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var edges = new List<DependencyEdge>();
            foreach (var job in jobs)
            {
                foreach (var dependency in job.Definition.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dependency.Id) || softDeletedIds.Contains(dependency.Id!))
                    {
                        continue;
                    }

                    edges.Add(new DependencyEdge(dependency.Id!, job.Record.JobId));
                }
            }

            bool IsRenderable(string id) => byId.ContainsKey(id) || byKusto.ContainsKey(id);

            // Consumer (job->node), source (node->job), and node->node edges. Both endpoints must be
            // renderable so a stale reference never injects an unresolved placeholder.
            foreach (var edge in kustoEdges)
            {
                if (IsRenderable(edge.FromId) && IsRenderable(edge.ToId))
                {
                    edges.Add(new DependencyEdge(edge.FromId, edge.ToId));
                }
            }

            // Implicit (undeclared) job->job edges connect two live job nodes; flag them so they can be
            // drawn distinctly from declared dependsOn edges.
            var implicitEdgeKeys = new HashSet<(string, string)>();
            foreach (var edge in implicitJobEdges)
            {
                if (byId.ContainsKey(edge.FromId) && byId.ContainsKey(edge.ToId))
                {
                    edges.Add(new DependencyEdge(edge.FromId, edge.ToId));
                    implicitEdgeKeys.Add((edge.FromId, edge.ToId));
                }
            }

            var nodeUniverse = byId.Keys.Concat(byKusto.Keys);
            var layout = DependencyGraphLayoutEngine.Build(nodeUniverse, edges, focal);
            if (layout.Nodes.Count == 0)
            {
                return DependencyGraphViewModel.Empty;
            }

            // A reference is shown qualified unless it lives in a graph job's own cluster+database.
            var graphJobTargets = layout.Nodes
                .Where(placement => byId.ContainsKey(placement.Id))
                .Select(placement => byId[placement.Id].Definition.Target)
                .ToList();
            var homePairs = new HashSet<string>(graphJobTargets.Select(target => HomeKey(HostOf(target.ClusterUri), target.Database)), StringComparer.Ordinal);
            var homeClusters = new HashSet<string>(graphJobTargets.Select(target => HostOf(target.ClusterUri).ToLowerInvariant()), StringComparer.Ordinal);

            // 1. Position-independent content per node (label, wrapped lines, height, status, counts).
            var contents = layout.Nodes.ToDictionary(
                placement => placement.Id,
                placement => BuildNodeContent(placement, byId, byKusto, focal, homePairs, homeClusters),
                StringComparer.Ordinal);

            // 2. Layer geometry: width from node counts, height from the tallest node in each layer.
            var layerWidths = layout.Nodes
                .GroupBy(node => node.Layer)
                .ToDictionary(group => group.Key, group => group.Count());
            var layerHeights = layout.Nodes
                .GroupBy(node => node.Layer)
                .ToDictionary(group => group.Key, group => group.Max(node => contents[node.Id].Height));
            var contentWidth = layout.MaxLayerWidth <= 0
                ? NodeWidth
                : (layout.MaxLayerWidth * NodeWidth) + ((layout.MaxLayerWidth - 1) * HorizontalGap);

            var layerTop = new Dictionary<int, double>();
            var cursorY = Padding;
            for (var layer = 0; layer < layout.LayerCount; layer++)
            {
                layerTop[layer] = cursorY;
                cursorY += layerHeights.GetValueOrDefault(layer, MinNodeHeight) + VerticalGap;
            }

            var contentBottom = layout.LayerCount == 0
                ? Padding + MinNodeHeight
                : layerTop[layout.LayerCount - 1] + layerHeights.GetValueOrDefault(layout.LayerCount - 1, MinNodeHeight);

            // 3. Final view models: centre each node within its layer band, horizontally and vertically.
            var nodes = layout.Nodes.Select(placement =>
            {
                var content = contents[placement.Id];
                var layerWidth = layerWidths[placement.Layer];
                var layerSpan = layerWidth <= 0
                    ? NodeWidth
                    : (layerWidth * NodeWidth) + ((layerWidth - 1) * HorizontalGap);
                var startX = Padding + ((contentWidth - layerSpan) / 2);
                var x = startX + (placement.Order * (NodeWidth + HorizontalGap));
                var bandHeight = layerHeights.GetValueOrDefault(placement.Layer, MinNodeHeight);
                var y = layerTop.GetValueOrDefault(placement.Layer, Padding) + ((bandHeight - content.Height) / 2);
                return content.ToViewModel(x, y, NodeWidth, content.Height);
            }).ToList();

            var edgeViewModels = layout.Edges
                .Select(edge => new DependencyGraphEdgeViewModel(
                    edge.UpstreamId,
                    edge.DownstreamId,
                    implicitEdgeKeys.Contains((edge.UpstreamId, edge.DownstreamId))))
                .ToList();

            var resolvedFocal = focal.Count(id => byId.ContainsKey(id));

            return new DependencyGraphViewModel(
                nodes,
                edgeViewModels,
                contentWidth + (Padding * 2),
                contentBottom + Padding,
                focal.Count,
                resolvedFocal,
                BuildLegend(nodes));
        }

        private sealed record NodeContent(
            DependencyGraphPlacement Placement,
            string Label,
            string Status,
            string StatusText,
            string StatusCss,
            string Kind,
            bool Resolved,
            bool Focal,
            string? Href,
            DependencyGraphCounts? Counts,
            IReadOnlyList<string> Lines,
            double Height)
        {
            public DependencyGraphNodeViewModel ToViewModel(double x, double y, double width, double height) =>
                new(Placement.Id, Label, Status, StatusText, StatusCss, Kind, Resolved, Focal, Href,
                    Placement.Layer, Placement.Order, x, y, width, height, Counts, Lines);
        }

        private static NodeContent BuildNodeContent(
            DependencyGraphPlacement placement,
            IReadOnlyDictionary<string, JobListItem> byId,
            IReadOnlyDictionary<string, KustoConsumerNode> byKusto,
            IReadOnlySet<string> focal,
            IReadOnlySet<string> homePairs,
            IReadOnlySet<string> homeClusters)
        {
            string label;
            string status;
            string statusText;
            string statusCss;
            string kind;
            bool resolved;
            bool isFocal;
            string? href;
            DependencyGraphCounts? counts;

            if (byId.TryGetValue(placement.Id, out var job))
            {
                label = job.Record.ActivityId;
                status = job.LifecycleStatus;
                statusText = job.StatusText;
                statusCss = job.StatusCss;
                kind = "Job";
                resolved = true;
                isFocal = focal.Contains(placement.Id);
                href = $"/jobs/{Uri.EscapeDataString(placement.Id)}";
                counts = BuildCounts(job.Summary);
            }
            else if (byKusto.TryGetValue(placement.Id, out var entity))
            {
                kind = ResolveKustoKind(entity.EntityType, entity.Name);
                label = QualifyKustoReference(entity, homePairs, homeClusters);
                status = kind;
                statusText = KustoTypeLabel(entity.EntityType, entity.Name);
                statusCss = "badge-neutral";
                resolved = true;
                isFocal = false;
                href = null;
                counts = null;
            }
            else
            {
                label = "Unknown upstream";
                status = "Unknown";
                statusText = "Unknown upstream";
                statusCss = "badge-neutral";
                kind = "Job";
                resolved = false;
                isFocal = focal.Contains(placement.Id);
                href = null;
                counts = null;
            }

            var lines = WrapLabel(label);
            var hasStatusLine = !string.IsNullOrEmpty(statusText) && !StringComparer.Ordinal.Equals(statusText, label);
            var height = Math.Max(
                MinNodeHeight,
                (NodeVerticalPadding * 2) + (lines.Count * LabelLineHeight) + (hasStatusLine ? StatusLineHeight : 0));

            return new NodeContent(placement, label, status, statusText, statusCss, kind, resolved, isFocal, href, counts, lines, height);
        }

        // Wraps a label into at most MaxLabelLines lines by packing dot-delimited chunks (keeping each
        // '.' with its chunk so qualified references such as cluster('x').database('y').Foo break at
        // the segment boundaries), hard-splitting any chunk longer than a line and ellipsizing overflow.
        private static IReadOnlyList<string> WrapLabel(string label)
        {
            if (string.IsNullOrEmpty(label))
            {
                return new[] { string.Empty };
            }

            var lines = new List<string>();
            var current = new System.Text.StringBuilder();

            void Flush()
            {
                if (current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
            }

            foreach (var chunk in ChunkByDot(label))
            {
                var remaining = chunk;
                while (remaining.Length > WrapCharsPerLine)
                {
                    Flush();
                    lines.Add(remaining[..WrapCharsPerLine]);
                    remaining = remaining[WrapCharsPerLine..];
                }

                if (current.Length > 0 && current.Length + remaining.Length > WrapCharsPerLine)
                {
                    Flush();
                }

                current.Append(remaining);
            }

            Flush();

            if (lines.Count == 0)
            {
                lines.Add(label);
            }

            if (lines.Count > MaxLabelLines)
            {
                var kept = lines.Take(MaxLabelLines).ToList();
                var last = kept[^1];
                kept[^1] = (last.Length > WrapCharsPerLine - 1 ? last[..(WrapCharsPerLine - 1)] : last) + "\u2026";
                return kept;
            }

            return lines;
        }

        private static IEnumerable<string> ChunkByDot(string label)
        {
            var chunks = new List<string>();
            var start = 0;
            for (var i = 0; i < label.Length; i++)
            {
                if (label[i] == '.')
                {
                    chunks.Add(label[start..(i + 1)]);
                    start = i + 1;
                }
            }

            if (start < label.Length)
            {
                chunks.Add(label[start..]);
            }

            return chunks;
        }

        // Shows a Kusto entity the way it would be referenced from a graph job: bare when it lives in
        // a graph job's own cluster+database, otherwise prefixed with database('db') (cross-database,
        // same cluster) or cluster('short').database('db') (cross-cluster). A known function gets a
        // trailing () in the qualified form to read like a call; a wildcard ('*') is shown verbatim.
        private static string QualifyKustoReference(KustoConsumerNode entity, IReadOnlySet<string> homePairs, IReadOnlySet<string> homeClusters)
        {
            var name = entity.Name;
            if (homePairs.Contains(HomeKey(entity.Cluster, entity.Database)))
            {
                return name;
            }

            var display = string.Equals(entity.EntityType, "Function", StringComparison.OrdinalIgnoreCase) && name != "*"
                ? name + "()"
                : name;

            return homeClusters.Contains((entity.Cluster ?? string.Empty).ToLowerInvariant())
                ? $"database('{entity.Database}').{display}"
                : $"cluster('{ShortCluster(entity.Cluster)}').database('{entity.Database}').{display}";
        }

        // A wildcard ('*') or an unresolved cross-cluster reference (RemoteEntity) is not a function -
        // it is an external/whole-database reference, so it gets a neutral "external" kind.
        private static string ResolveKustoKind(string entityType, string entityName)
        {
            if (entityName == "*") return "KustoExternal";
            if (string.Equals(entityType, "MaterializedView", StringComparison.OrdinalIgnoreCase)) return "KustoMaterializedView";
            if (string.Equals(entityType, "Table", StringComparison.OrdinalIgnoreCase)) return "KustoTable";
            if (string.Equals(entityType, "Function", StringComparison.OrdinalIgnoreCase)) return "KustoFunction";
            return "KustoExternal";
        }

        private static string KustoTypeLabel(string entityType, string entityName)
        {
            if (entityName == "*") return "All entities";
            if (string.Equals(entityType, "MaterializedView", StringComparison.OrdinalIgnoreCase)) return "Materialized view";
            if (string.Equals(entityType, "Table", StringComparison.OrdinalIgnoreCase)) return "Table";
            if (string.Equals(entityType, "Function", StringComparison.OrdinalIgnoreCase)) return "Function";
            return "External";
        }

        private static string HomeKey(string cluster, string database) =>
            (cluster ?? string.Empty).ToLowerInvariant() + "|" + (database ?? string.Empty).ToLowerInvariant();

        private static string HostOf(string clusterUri) =>
            Uri.TryCreate(clusterUri, UriKind.Absolute, out var uri) ? uri.Host : (clusterUri ?? string.Empty);

        // The first label of the cluster host (e.g. "sample-fleet" from
        // "sample-fleet.centralus.kusto.windows.net") - enough to disambiguate a remote reference.
        private static string ShortCluster(string? clusterHost)
        {
            if (string.IsNullOrWhiteSpace(clusterHost))
            {
                return "remote";
            }

            var dot = clusterHost.IndexOf('.');
            return dot > 0 ? clusterHost[..dot] : clusterHost;
        }

        private static DependencyGraphCounts? BuildCounts(JobStatusSummary? summary)
        {
            if (summary is null)
            {
                return null;
            }

            var total = summary.MissingCount
                + summary.QueuedCount
                + summary.RunningCount
                + summary.CompletedCount
                + summary.FailedCount
                + summary.DeadLetteredCount
                + summary.DependencyBlockedCount;

            return new DependencyGraphCounts(
                summary.MissingCount,
                summary.QueuedCount,
                summary.RunningCount,
                summary.CompletedCount,
                summary.FailedCount,
                summary.DeadLetteredCount,
                summary.DependencyBlockedCount,
                total);
        }

        private static IReadOnlyList<DependencyGraphLegendItem> BuildLegend(IReadOnlyList<DependencyGraphNodeViewModel> nodes)
        {
            var present = nodes
                .GroupBy(node => node.Status, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            var ordered = new List<DependencyGraphLegendItem>();
            foreach (var status in LegendStatusOrder)
            {
                if (present.TryGetValue(status, out var node))
                {
                    ordered.Add(new DependencyGraphLegendItem(status, node.StatusText, node.StatusCss));
                    present.Remove(status);
                }
            }

            foreach (var node in present.Values.OrderBy(node => node.Status, StringComparer.Ordinal))
            {
                ordered.Add(new DependencyGraphLegendItem(node.Status, node.StatusText, node.StatusCss));
            }

            return ordered;
        }
    }
}
