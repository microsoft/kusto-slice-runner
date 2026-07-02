using KoLite.Local.Core.Graph;
using KoLite.Local.Sqlite.Observability;

namespace KoLite.LocalApp.Ui
{
    // Per-node slice counts shown in the details panel. Null on the view model when the job has no
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
        DependencyGraphCounts? Counts);

    public sealed record DependencyGraphEdgeViewModel(string FromId, string ToId, bool Implicit = false);

    public sealed record DependencyGraphLegendItem(string Status, string Label, string StatusCss);

    // Wraps the graph for the shared partial: the focal job ids are needed so the "Resolve Kusto
    // consumers" button can post them back to the enrichment endpoint.
    public sealed record DependencyGraphPanelViewModel(DependencyGraphViewModel Graph, IReadOnlyList<string> FocalJobIds);

    public sealed record DependencyGraphViewModel(
        IReadOnlyList<DependencyGraphNodeViewModel> Nodes,
        IReadOnlyList<DependencyGraphEdgeViewModel> Edges,
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
            Array.Empty<DependencyGraphLegendItem>());
    }

    // Builds the dependency graph view model for a focal set of jobs. It reuses the dashboard's
    // status projection for every job so node colors match the rest of the UI, derives dependency
    // edges from each job's stored dependsOn (upstream GUIDs), and asks the pure Core layout engine
    // for the connected component(s). Pixel layout is left to the Cytoscape client renderer.
    public sealed class DependencyGraphQuery
    {
        private static readonly IReadOnlyList<string> LegendStatusOrder = new[]
        {
            "Attention", "Borderline", "Healthy", "WaitingOnUpstream", "DependencyBlocked", "Paused", "Completed", "SoftDeleted", "Unknown",
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
                allJobs.Where(job => job.PrimaryState == "SoftDeleted").Select(job => job.Record.JobId),
                StringComparer.Ordinal);

            var focal = new HashSet<string>(
                requestedFocal.Where(id => !softDeletedIds.Contains(id)),
                StringComparer.Ordinal);
            if (focal.Count == 0)
            {
                return DependencyGraphViewModel.Empty;
            }

            var jobs = allJobs.Where(job => job.PrimaryState != "SoftDeleted").ToList();
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

            // Position-independent content per node (label, status, kind, counts). Cytoscape lays the
            // graph out client-side via dagre, so the server no longer computes pixel geometry.
            var nodes = layout.Nodes
                .Select(placement => BuildNodeContent(placement, byId, byKusto, focal, homePairs, homeClusters).ToViewModel())
                .ToList();

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
            DependencyGraphCounts? Counts)
        {
            public DependencyGraphNodeViewModel ToViewModel() =>
                new(Placement.Id, Label, Status, StatusText, StatusCss, Kind, Resolved, Focal, Href, Counts);
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
                status = job.PrimaryState;
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

            return new NodeContent(placement, label, status, statusText, statusCss, kind, resolved, isFocal, href, counts);
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
