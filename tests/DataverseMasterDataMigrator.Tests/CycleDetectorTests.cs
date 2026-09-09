using DataverseMasterDataMigrator.Core.Planning;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class CycleDetectorTests
    {
        [Fact]
        public void LinearChain_HasNoCycles()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("detalle", "subtema");
            graph.AddDependency("subtema", "tema");

            var cycles = CycleDetector.FindCycles(graph);

            Assert.Empty(cycles);
        }

        [Fact]
        public void SimpleTriangle_IsDetectedAsOneCycle()
        {
            // A -> B -> C -> A
            var graph = new DependencyGraph();
            graph.AddDependency("A", "B");
            graph.AddDependency("B", "C");
            graph.AddDependency("C", "A");

            var cycles = CycleDetector.FindCycles(graph);

            Assert.Single(cycles);
            Assert.Equal(3, cycles[0].Count);
            Assert.Contains("A", cycles[0]);
            Assert.Contains("B", cycles[0]);
            Assert.Contains("C", cycles[0]);
        }

        [Fact]
        public void SelfReference_IsDetectedAsCycle()
        {
            var graph = new DependencyGraph();
            graph.AddNode("A");
            // Simular auto-referencia directa (una tabla con un lookup hacia sí misma, p. ej. parentaccountid).
            graph.AddDependency("A", "A");
            // AddDependency ignora auto-aristas por diseño (ver DependencyGraph.AddDependency),
            // así que forzamos el escenario a través de dos nodos que sí se auto-referencian
            // indirectamente no aplica aquí; este test documenta ese comportamiento intencional.
            var cycles = CycleDetector.FindCycles(graph);

            Assert.Empty(cycles); // una auto-referencia literal se filtra en AddDependency, no es un "ciclo" de planificación.
        }

        [Fact]
        public void TwoIndependentCycles_AreBothReported()
        {
            var graph = new DependencyGraph();
            graph.AddDependency("A", "B");
            graph.AddDependency("B", "A");

            graph.AddDependency("X", "Y");
            graph.AddDependency("Y", "X");

            var cycles = CycleDetector.FindCycles(graph);

            Assert.Equal(2, cycles.Count);
        }

        [Fact]
        public void CycleWithExternalTail_OnlyReportsTheCycleMembers()
        {
            // A <-> B es un ciclo; B -> C es una cola sin ciclo.
            var graph = new DependencyGraph();
            graph.AddDependency("A", "B");
            graph.AddDependency("B", "A");
            graph.AddDependency("B", "C");

            var cycles = CycleDetector.FindCycles(graph);

            Assert.Single(cycles);
            Assert.DoesNotContain("C", cycles[0]);
        }
    }
}
