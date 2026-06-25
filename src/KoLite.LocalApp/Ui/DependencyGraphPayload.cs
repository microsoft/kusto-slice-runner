namespace KoLite.LocalApp.Ui
{
    // Single source of truth for the JSON the dependency graph client renderer consumes. Both the
    // server-rendered partial and the live Kusto-enrichment endpoint serialize this exact shape, so
    // an initial render and an AJAX redraw are byte-compatible. Property names are written
    // lower-camel so System.Text.Json (with or without the web naming policy) produces identical keys.
    // The Cytoscape client computes layout (dagre) and sizes nodes to their label, so no pixel
    // geometry is emitted.
    public static class DependencyGraphPayload
    {
        public static object Build(DependencyGraphViewModel model)
        {
            ArgumentNullException.ThrowIfNull(model);

            return new
            {
                nodes = model.Nodes.Select(node => new
                {
                    id = node.Id,
                    label = node.Label,
                    status = node.Status,
                    statusKey = node.Status.ToLowerInvariant(),
                    statusText = node.StatusText,
                    kind = node.Kind,
                    resolved = node.Resolved,
                    focal = node.Focal,
                    href = node.Href,
                    counts = node.Counts is null
                        ? null
                        : new
                        {
                            total = node.Counts.Total,
                            missing = node.Counts.Missing,
                            queued = node.Counts.Queued,
                            running = node.Counts.Running,
                            completed = node.Counts.Completed,
                            failed = node.Counts.Failed,
                            deadLettered = node.Counts.DeadLettered,
                            dependencyBlocked = node.Counts.DependencyBlocked
                        }
                }).ToList(),
                edges = model.Edges.Select(edge => new { from = edge.FromId, to = edge.ToId, @implicit = edge.Implicit }).ToList(),
                legend = model.Legend.Select(item => new
                {
                    status = item.Status,
                    statusKey = item.Status.ToLowerInvariant(),
                    label = item.Label
                }).ToList()
            };
        }
    }
}
