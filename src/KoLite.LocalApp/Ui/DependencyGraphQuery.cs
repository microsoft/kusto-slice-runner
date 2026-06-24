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
        DependencyGraphCounts? Counts);

    public sealed record DependencyGraphEdgeViewModel(string FromId, string ToId);

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
        // SVG geometry. Kept here (not in Core) because pixels are a presentation concern.
        private const double NodeWidth = 196;
        private const double NodeHeight = 56;
        private const double HorizontalGap = 32;
        private const double VerticalGap = 72;
        private const double Padding = 28;

        private static readonly IReadOnlyList<string> LegendStatusOrder = new[]
        {
            "Healthy", "Running", "DependencyBlocked", "Failed", "Paused", "Completed", "SoftDeleted", "Unknown",
            "KustoFunction", "KustoMaterializedView"
        };

        private readonly DashboardPageQuery dashboard;

        public DependencyGraphQuery(DashboardPageQuery dashboard) => this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));

        public DependencyGraphViewModel Build(IEnumerable<string> focalJobIds) =>
            Build(focalJobIds, Array.Empty<KustoConsumerNode>(), Array.Empty<KustoConsumerEdge>());

        // Builds the graph, optionally enriched with downstream Kusto consumer nodes/edges (computed
        // live elsewhere). Kusto edges connect a producing job id (or another consumer key) to a
        // consumer key; they extend the same connected-component layout as the job dependsOn edges.
        public DependencyGraphViewModel Build(
            IEnumerable<string> focalJobIds,
            IReadOnlyList<KustoConsumerNode> kustoNodes,
            IReadOnlyList<KustoConsumerEdge> kustoEdges)
        {
            if (focalJobIds is null) throw new ArgumentNullException(nameof(focalJobIds));
            if (kustoNodes is null) throw new ArgumentNullException(nameof(kustoNodes));
            if (kustoEdges is null) throw new ArgumentNullException(nameof(kustoEdges));

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

            // Only keep Kusto edges whose endpoints we can render (a live job or a supplied consumer),
            // so a stale producer reference never injects an unresolved placeholder.
            foreach (var edge in kustoEdges)
            {
                if ((byId.ContainsKey(edge.FromId) || byKusto.ContainsKey(edge.FromId)) && byKusto.ContainsKey(edge.ToId))
                {
                    edges.Add(new DependencyEdge(edge.FromId, edge.ToId));
                }
            }

            var nodeUniverse = byId.Keys.Concat(byKusto.Keys);
            var layout = DependencyGraphLayoutEngine.Build(nodeUniverse, edges, focal);
            if (layout.Nodes.Count == 0)
            {
                return DependencyGraphViewModel.Empty;
            }

            var layerWidths = layout.Nodes
                .GroupBy(node => node.Layer)
                .ToDictionary(group => group.Key, group => group.Count());
            var contentWidth = layout.MaxLayerWidth <= 0
                ? NodeWidth
                : (layout.MaxLayerWidth * NodeWidth) + ((layout.MaxLayerWidth - 1) * HorizontalGap);
            var contentHeight = layout.LayerCount <= 0
                ? NodeHeight
                : (layout.LayerCount * NodeHeight) + ((layout.LayerCount - 1) * VerticalGap);

            var nodes = layout.Nodes
                .Select(placement => BuildNode(placement, byId, byKusto, focal, layerWidths[placement.Layer], contentWidth))
                .ToList();

            var edgeViewModels = layout.Edges
                .Select(edge => new DependencyGraphEdgeViewModel(edge.UpstreamId, edge.DownstreamId))
                .ToList();

            var resolvedFocal = focal.Count(id => byId.ContainsKey(id));

            return new DependencyGraphViewModel(
                nodes,
                edgeViewModels,
                contentWidth + (Padding * 2),
                contentHeight + (Padding * 2),
                focal.Count,
                resolvedFocal,
                BuildLegend(nodes));
        }

        private static DependencyGraphNodeViewModel BuildNode(
            DependencyGraphPlacement placement,
            IReadOnlyDictionary<string, JobListItem> byId,
            IReadOnlyDictionary<string, KustoConsumerNode> byKusto,
            IReadOnlySet<string> focal,
            int layerWidth,
            double contentWidth)
        {
            var layerSpan = layerWidth <= 0
                ? NodeWidth
                : (layerWidth * NodeWidth) + ((layerWidth - 1) * HorizontalGap);
            var startX = Padding + ((contentWidth - layerSpan) / 2);
            var x = startX + (placement.Order * (NodeWidth + HorizontalGap));
            var y = Padding + (placement.Layer * (NodeHeight + VerticalGap));
            var isFocal = focal.Contains(placement.Id);

            if (byId.TryGetValue(placement.Id, out var job))
            {
                return new DependencyGraphNodeViewModel(
                    placement.Id,
                    job.Record.ActivityId,
                    job.LifecycleStatus,
                    job.StatusText,
                    job.StatusCss,
                    "Job",
                    Resolved: true,
                    isFocal,
                    $"/jobs/{Uri.EscapeDataString(placement.Id)}",
                    placement.Layer,
                    placement.Order,
                    x,
                    y,
                    NodeWidth,
                    NodeHeight,
                    BuildCounts(job.Summary));
            }

            if (byKusto.TryGetValue(placement.Id, out var consumer))
            {
                var isView = string.Equals(consumer.EntityType, "MaterializedView", StringComparison.OrdinalIgnoreCase);
                var kind = isView ? "KustoMaterializedView" : "KustoFunction";
                return new DependencyGraphNodeViewModel(
                    placement.Id,
                    consumer.Name,
                    kind,
                    isView ? "Materialized view" : "Function",
                    "badge-neutral",
                    kind,
                    Resolved: true,
                    Focal: false,
                    Href: null,
                    placement.Layer,
                    placement.Order,
                    x,
                    y,
                    NodeWidth,
                    NodeHeight,
                    Counts: null);
            }

            return new DependencyGraphNodeViewModel(
                placement.Id,
                "Unknown upstream",
                "Unknown",
                "Unknown upstream",
                "badge-neutral",
                "Job",
                Resolved: false,
                isFocal,
                Href: null,
                placement.Layer,
                placement.Order,
                x,
                y,
                NodeWidth,
                NodeHeight,
                Counts: null);
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
