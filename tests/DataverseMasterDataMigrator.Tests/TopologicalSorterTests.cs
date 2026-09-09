using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Planning;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class TopologicalSorterTests
    {
        [Fact]
        public void LinearChain_OrdersDependenciesFirst()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("detalle", "subtema");
            graph.AddDependency("subtema", "tema");

            var ordered = TopologicalSorter.Sort(graph, new Dictionary<string, int>(), new List<IReadOnlyList<string>>());

            var names = ordered.Select(o => o.LogicalName).ToList();
            Assert.True(names.IndexOf("tema") < names.IndexOf("subtema"));
            Assert.True(names.IndexOf("subtema") < names.IndexOf("detalle"));
        }

        [Fact]
        public void NoDependencies_TieBreaksByPreferredOrder()
        {
            var graph = new DependencyGraph();
            graph.AddNode("b");
            graph.AddNode("a");
            graph.AddNode("c");

            var preferred = new Dictionary<string, int> { ["c"] = 1, ["a"] = 2, ["b"] = 3 };

            var ordered = TopologicalSorter.Sort(graph, preferred, new List<IReadOnlyList<string>>());

            Assert.Equal(new[] { "c", "a", "b" }, ordered.Select(o => o.LogicalName));
        }

        [Fact]
        public void NoDependenciesAndNoPreferredOrder_TieBreaksByName()
        {
            var graph = new DependencyGraph();
            graph.AddNode("zeta");
            graph.AddNode("alfa");

            var ordered = TopologicalSorter.Sort(graph, new Dictionary<string, int>(), new List<IReadOnlyList<string>>());

            Assert.Equal(new[] { "alfa", "zeta" }, ordered.Select(o => o.LogicalName));
        }

        [Fact]
        public void Cycle_DoesNotThrow_AndMarksMembers()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("A", "B");
            graph.AddDependency("B", "C");
            graph.AddDependency("C", "A");

            var cycles = CycleDetector.FindCycles(graph);
            var ordered = TopologicalSorter.Sort(graph, new Dictionary<string, int>(), cycles);

            Assert.Equal(3, ordered.Count);
            Assert.All(ordered, o => Assert.True(o.IsPartOfCycle));
        }

        [Fact]
        public void MixedGraph_CycleNodesMarked_LinearNodesNotMarked()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("A", "B");
            graph.AddDependency("B", "A");
            graph.AddDependency("D", "A"); // D depende del ciclo pero no es parte de él

            var cycles = CycleDetector.FindCycles(graph);
            var ordered = TopologicalSorter.Sort(graph, new Dictionary<string, int>(), cycles);

            var byName = ordered.ToDictionary(o => o.LogicalName);
            Assert.True(byName["A"].IsPartOfCycle);
            Assert.True(byName["B"].IsPartOfCycle);
            Assert.False(byName["D"].IsPartOfCycle);

            // D depende de A, así que A debe ir antes que D en el resultado.
            Assert.True(byName["A"].Order < byName["D"].Order);
        }

        [Fact]
        public void ResultOrder_IsStableAcrossMultipleRuns()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("detalle", "subtema");
            graph.AddDependency("subtema", "tema");
            graph.AddNode("independiente");

            var run1 = TopologicalSorter.Sort(graph, new Dictionary<string, int>(), new List<IReadOnlyList<string>>())
                .Select(o => o.LogicalName).ToList();
            var run2 = TopologicalSorter.Sort(graph, new Dictionary<string, int>(), new List<IReadOnlyList<string>>())
                .Select(o => o.LogicalName).ToList();

            Assert.Equal(run1, run2);
        }
    }
}
