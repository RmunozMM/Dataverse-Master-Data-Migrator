using System;
using System.Collections.Generic;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Planning
{
    public static class DependencyGraphBuilder
    {
        /// <summary>
        /// Construye el grafo SOLO con aristas entre tablas incluidas en <paramref name="selectedTables"/>.
        /// Un lookup hacia una tabla fuera de esa lista no genera arista: esa referencia se
        /// resuelve en runtime contra Target, no en la planificación (sección 12 del
        /// requerimiento; ver también ARCHITECTURE.md sección 4).
        /// </summary>
        public static DependencyGraph Build(IReadOnlyDictionary<string, TableSummary> selectedTables)
        {
            if (selectedTables == null) throw new ArgumentNullException(nameof(selectedTables));

            var graph = new DependencyGraph();
            foreach (var logicalName in selectedTables.Keys)
                graph.AddNode(logicalName);

            foreach (var table in selectedTables.Values)
            {
                foreach (var attribute in table.Attributes)
                {
                    if (attribute.Kind != AttributeKind.Lookup)
                        continue;

                    foreach (var target in attribute.LookupTargets)
                    {
                        if (selectedTables.ContainsKey(target))
                            graph.AddDependency(table.LogicalName, target);
                    }
                }
            }

            return graph;
        }
    }
}
