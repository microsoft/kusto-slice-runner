// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using KoLite.Local.Core.Graph;

namespace KoLite.Local.Core.Tests
{
    public sealed class DependencyGraphLayoutEngineTests
    {
        [Fact]
        public void Linear_chain_layers_upstream_above_downstream()
        {
            // C depends on B depends on A.
            var layout = Build(
                nodes: new[] { "A", "B", "C" },
                edges: new[] { Edge("A", "B"), Edge("B", "C") },
                focal: new[] { "C" });

            // Focal C must pull in the whole transitive upstream chain.
            Assert.Equal(new[] { "A", "B", "C" }, LayoutIds(layout));
            Assert.Equal(0, LayerOf(layout, "A"));
            Assert.Equal(1, LayerOf(layout, "B"));
            Assert.Equal(2, LayerOf(layout, "C"));
            Assert.Equal(3, layout.LayerCount);
            Assert.Equal(1, layout.MaxLayerWidth);
        }

        [Fact]
        public void Focal_pulls_in_both_transitive_upstream_and_downstream()
        {
            // A -> B -> C; focusing on the middle node must include A (upstream) and C (downstream).
            var layout = Build(
                nodes: new[] { "A", "B", "C" },
                edges: new[] { Edge("A", "B"), Edge("B", "C") },
                focal: new[] { "B" });

            Assert.Equal(new[] { "A", "B", "C" }, LayoutIds(layout));
        }

        [Fact]
        public void Diamond_assigns_shared_layers()
        {
            // A -> B, A -> C, B -> D, C -> D.
            var layout = Build(
                nodes: new[] { "A", "B", "C", "D" },
                edges: new[] { Edge("A", "B"), Edge("A", "C"), Edge("B", "D"), Edge("C", "D") },
                focal: new[] { "A" });

            Assert.Equal(0, LayerOf(layout, "A"));
            Assert.Equal(1, LayerOf(layout, "B"));
            Assert.Equal(1, LayerOf(layout, "C"));
            Assert.Equal(2, LayerOf(layout, "D"));
            Assert.Equal(2, layout.MaxLayerWidth);
        }

        [Fact]
        public void Longest_path_wins_for_layering()
        {
            // D depends on A directly and also via B; the longest path (A -> B -> D) decides D's layer.
            var layout = Build(
                nodes: new[] { "A", "B", "D" },
                edges: new[] { Edge("A", "B"), Edge("A", "D"), Edge("B", "D") },
                focal: new[] { "A" });

            Assert.Equal(0, LayerOf(layout, "A"));
            Assert.Equal(1, LayerOf(layout, "B"));
            Assert.Equal(2, LayerOf(layout, "D"));
        }

        [Fact]
        public void Within_layer_orders_are_contiguous_from_zero()
        {
            var layout = Build(
                nodes: new[] { "A", "B", "C", "D" },
                edges: new[] { Edge("A", "B"), Edge("A", "C"), Edge("B", "D"), Edge("C", "D") },
                focal: new[] { "A" });

            foreach (var layerGroup in layout.Nodes.GroupBy(n => n.Layer))
            {
                var orders = layerGroup.Select(n => n.Order).OrderBy(o => o).ToArray();
                Assert.Equal(Enumerable.Range(0, orders.Length).ToArray(), orders);
            }
        }

        [Fact]
        public void Multiple_focal_nodes_in_separate_components_are_unioned()
        {
            // Two independent chains plus an unrelated chain that must stay out.
            var layout = Build(
                nodes: new[] { "A", "B", "X", "Y", "Q", "R" },
                edges: new[] { Edge("A", "B"), Edge("X", "Y"), Edge("Q", "R") },
                focal: new[] { "A", "Y" });

            Assert.Equal(new[] { "A", "B", "X", "Y" }, LayoutIds(layout));
            Assert.DoesNotContain("Q", LayoutIds(layout));
            Assert.DoesNotContain("R", LayoutIds(layout));
        }

        [Fact]
        public void Unrelated_component_is_excluded()
        {
            var layout = Build(
                nodes: new[] { "A", "B", "C", "D" },
                edges: new[] { Edge("A", "B"), Edge("C", "D") },
                focal: new[] { "A" });

            Assert.Equal(new[] { "A", "B" }, LayoutIds(layout));
        }

        [Fact]
        public void Unknown_edge_endpoint_becomes_a_node()
        {
            // "ghost" is referenced as an upstream but is not in the declared node set.
            var layout = Build(
                nodes: new[] { "A" },
                edges: new[] { Edge("ghost", "A") },
                focal: new[] { "A" });

            Assert.Contains("ghost", LayoutIds(layout));
            Assert.Equal(0, LayerOf(layout, "ghost"));
            Assert.Equal(1, LayerOf(layout, "A"));
            Assert.Single(layout.Edges);
        }

        [Fact]
        public void Self_edge_is_dropped()
        {
            var layout = Build(
                nodes: new[] { "A" },
                edges: new[] { Edge("A", "A") },
                focal: new[] { "A" });

            Assert.Equal(new[] { "A" }, LayoutIds(layout));
            Assert.Empty(layout.Edges);
            Assert.Equal(0, LayerOf(layout, "A"));
        }

        [Fact]
        public void Cycle_is_broken_and_does_not_throw()
        {
            // A -> B -> C -> A is a cycle; layout must terminate and place every node.
            var layout = Build(
                nodes: new[] { "A", "B", "C" },
                edges: new[] { Edge("A", "B"), Edge("B", "C"), Edge("C", "A") },
                focal: new[] { "A" });

            Assert.Equal(new[] { "A", "B", "C" }, LayoutIds(layout));
            Assert.Equal(3, layout.Edges.Count);
            Assert.All(layout.Nodes, node => Assert.True(node.Layer >= 0));
        }

        [Fact]
        public void Isolated_focal_node_is_a_single_root()
        {
            var layout = Build(
                nodes: new[] { "A", "B" },
                edges: new[] { Edge("A", "B") },
                focal: new[] { "lonely" });

            Assert.Equal(new[] { "lonely" }, LayoutIds(layout));
            Assert.Empty(layout.Edges);
            Assert.Equal(0, LayerOf(layout, "lonely"));
            Assert.Equal(1, layout.LayerCount);
        }

        [Fact]
        public void Empty_focal_set_yields_empty_layout()
        {
            var layout = Build(
                nodes: new[] { "A", "B" },
                edges: new[] { Edge("A", "B") },
                focal: Array.Empty<string>());

            Assert.Empty(layout.Nodes);
            Assert.Empty(layout.Edges);
            Assert.Equal(0, layout.LayerCount);
            Assert.Equal(0, layout.MaxLayerWidth);
        }

        [Fact]
        public void Layout_is_deterministic_across_runs()
        {
            string[] nodes = { "A", "B", "C", "D", "E" };
            DependencyEdge[] edges =
            {
                Edge("A", "C"), Edge("B", "C"), Edge("C", "D"), Edge("C", "E")
            };

            var first = DependencyGraphLayoutEngine.Build(nodes, edges, new[] { "C" });
            var second = DependencyGraphLayoutEngine.Build(nodes.Reverse(), edges.Reverse(), new[] { "C" });

            Assert.Equal(
                first.Nodes.Select(n => (n.Id, n.Layer, n.Order)),
                second.Nodes.Select(n => (n.Id, n.Layer, n.Order)));
        }

        private static DependencyGraphLayout Build(string[] nodes, DependencyEdge[] edges, string[] focal) =>
            DependencyGraphLayoutEngine.Build(nodes, edges, focal);

        private static DependencyEdge Edge(string upstream, string downstream) => new(upstream, downstream);

        private static string[] LayoutIds(DependencyGraphLayout layout) =>
            layout.Nodes.Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        private static int LayerOf(DependencyGraphLayout layout, string id) =>
            layout.Nodes.Single(n => n.Id == id).Layer;
    }
}
