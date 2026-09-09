using System;
using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Planning
{
    public sealed class PlanStep
    {
        public string LogicalName { get; set; }
        public int Order { get; set; }
        public bool IsPartOfCycle { get; set; }
    }

    public sealed class MigrationPlan
    {
        public IReadOnlyList<PlanStep> Steps { get; set; } = new List<PlanStep>();
        public IReadOnlyList<IReadOnlyList<string>> Cycles { get; set; } = new List<IReadOnlyList<string>>();

        public bool HasCycles => Cycles.Count > 0;
    }

    public static class MigrationPlanner
    {
        /// <summary>
        /// Construye el plan a partir del perfil y la metadata real de las tablas seleccionadas
        /// (obtenida previamente vía <see cref="Abstractions.IDataverseMetadataProvider"/>).
        /// No accede a Dataverse directamente: es lógica pura, por eso es testeable sin conexión.
        /// </summary>
        public static MigrationPlan CreatePlan(
            MigrationProfile profile,
            IReadOnlyDictionary<string, TableSummary> tableMetadataByLogicalName)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (tableMetadataByLogicalName == null) throw new ArgumentNullException(nameof(tableMetadataByLogicalName));

            var enabledEntities = profile.Entities.Where(e => e.Enabled).ToList();

            var selected = enabledEntities
                .Where(e => tableMetadataByLogicalName.ContainsKey(e.LogicalName))
                .ToDictionary(
                    e => e.LogicalName,
                    e => tableMetadataByLogicalName[e.LogicalName],
                    StringComparer.OrdinalIgnoreCase);

            var graph = DependencyGraphBuilder.Build(selected);
            var cycles = CycleDetector.FindCycles(graph);

            var preferredOrder = enabledEntities.ToDictionary(
                e => e.LogicalName,
                e => e.PreferredOrder,
                StringComparer.OrdinalIgnoreCase);

            var ordered = TopologicalSorter.Sort(graph, preferredOrder, cycles);

            return new MigrationPlan
            {
                Steps = ordered.Select(o => new PlanStep
                {
                    LogicalName = o.LogicalName,
                    Order = o.Order,
                    IsPartOfCycle = o.IsPartOfCycle
                }).ToList(),
                Cycles = cycles
            };
        }
    }
}
