using System;
using System.Collections.Generic;
using System.Linq;

namespace DataverseMasterDataMigrator.Core.Planning
{
    public sealed class OrderedNode
    {
        public string LogicalName { get; set; }
        public int Order { get; set; }
        public bool IsPartOfCycle { get; set; }
    }

    public static class TopologicalSorter
    {
        /// <summary>
        /// Orden topológico determinista mediante Kahn. Cuando hay varios nodos sin
        /// dependencias pendientes, desempata por <paramref name="preferredOrder"/> (menor
        /// primero) y luego por nombre lógico, para que el resultado sea reproducible entre
        /// corridas (ver ARCHITECTURE.md sección 4).
        ///
        /// Un ciclo NO detiene el algoritmo: los nodos que forman parte de un ciclo se marcan
        /// <see cref="OrderedNode.IsPartOfCycle"/> y se liberan por su <paramref name="preferredOrder"/>
        /// tan pronto como quedan como "el de menor grado restante", igual que cualquier otro
        /// nodo — la resolución real de sus lookups cruzados ocurre en el motor multipass, no aquí.
        /// </summary>
        public static IReadOnlyList<OrderedNode> Sort(
            DependencyGraph graph,
            IReadOnlyDictionary<string, int> preferredOrder,
            IReadOnlyCollection<IReadOnlyList<string>> knownCycles)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            preferredOrder = preferredOrder ?? new Dictionary<string, int>();

            var cycleMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (knownCycles != null)
                foreach (var cycle in knownCycles)
                    foreach (var member in cycle)
                        cycleMembers.Add(member);

            // Copia local de aristas restantes (in-degree = cuántas dependencias le faltan resolver).
            var remainingDeps = graph.Nodes.ToDictionary(
                n => n,
                n => new HashSet<string>(graph.DependenciesOf(n), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

            Func<string, int> preferredOrderOf = n =>
            {
                int v;
                return preferredOrder.TryGetValue(n, out v) ? v : int.MaxValue;
            };

            var result = new List<OrderedNode>();
            var pending = new HashSet<string>(graph.Nodes, StringComparer.OrdinalIgnoreCase);
            int position = 0;

            while (pending.Count > 0)
            {
                // Candidatos: nodos sin dependencias pendientes DENTRO del conjunto todavía no ordenado.
                var ready = pending
                    .Where(n => !remainingDeps[n].Any(pending.Contains))
                    .OrderBy(preferredOrderOf)
                    .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (ready.Count == 0)
                {
                    // Solo puede pasar si TODO lo que queda está en ciclos entre sí. Se rompe el
                    // empate liberando el de menor preferredOrder/nombre entre los pendientes,
                    // documentado como comportamiento esperado (no es un error del algoritmo).
                    ready.Add(pending
                        .OrderBy(preferredOrderOf)
                        .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .First());
                }

                foreach (var node in ready)
                {
                    result.Add(new OrderedNode
                    {
                        LogicalName = node,
                        Order = position++,
                        IsPartOfCycle = cycleMembers.Contains(node)
                    });
                    pending.Remove(node);
                }
            }

            return result;
        }
    }
}
