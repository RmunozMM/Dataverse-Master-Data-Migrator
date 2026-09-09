using System;
using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Comparison
{
    /// <summary>Pure, in-memory: builds a <see cref="SchemaComparisonResult"/> from already-loaded
    /// Source/Target table metadata (the same <see cref="TableSummary"/> dictionaries Preflight's
    /// LoadEnabledTableMetadata already produces) — no I/O, no SDK calls of its own.</summary>
    public sealed class SchemaComparisonBuilder
    {
        public SchemaComparisonResult Build(
            IReadOnlyDictionary<string, TableSummary> sourceTables,
            IReadOnlyDictionary<string, TableSummary> targetTables,
            IEnumerable<string> logicalNamesInOrder)
        {
            if (sourceTables == null) throw new ArgumentNullException(nameof(sourceTables));
            if (targetTables == null) throw new ArgumentNullException(nameof(targetTables));
            if (logicalNamesInOrder == null) throw new ArgumentNullException(nameof(logicalNamesInOrder));

            var tables = new List<TableComparison>();

            foreach (var logicalName in logicalNamesInOrder)
            {
                sourceTables.TryGetValue(logicalName, out var sTable);
                targetTables.TryGetValue(logicalName, out var tTable);

                var table = new TableComparison
                {
                    LogicalName = logicalName,
                    DisplayName = sTable?.DisplayName ?? tTable?.DisplayName ?? logicalName,
                    ExistsInSource = sTable != null,
                    ExistsInTarget = tTable != null,
                    Attributes = BuildAttributeComparisons(sTable, tTable)
                };

                tables.Add(table);
            }

            return new SchemaComparisonResult { Tables = tables };
        }

        private static IReadOnlyList<AttributeComparison> BuildAttributeComparisons(TableSummary sTable, TableSummary tTable)
        {
            var sourceAttributes = new Dictionary<string, AttributeSummary>(StringComparer.OrdinalIgnoreCase);
            if (sTable != null)
            {
                foreach (var attr in sTable.Attributes) sourceAttributes[attr.LogicalName] = attr;
            }

            var targetAttributes = new Dictionary<string, AttributeSummary>(StringComparer.OrdinalIgnoreCase);
            if (tTable != null)
            {
                foreach (var attr in tTable.Attributes) targetAttributes[attr.LogicalName] = attr;
            }

            // Union of both sides' attribute logical names, case-insensitive.
            var allLogicalNames = new HashSet<string>(sourceAttributes.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var name in targetAttributes.Keys) allLogicalNames.Add(name);

            var rows = new List<AttributeComparison>();
            foreach (var logicalName in allLogicalNames)
            {
                sourceAttributes.TryGetValue(logicalName, out var sAttr);
                targetAttributes.TryGetValue(logicalName, out var tAttr);

                rows.Add(new AttributeComparison
                {
                    LogicalName = logicalName,
                    ExistsInSource = sAttr != null,
                    ExistsInTarget = tAttr != null,
                    SourceDisplayName = sAttr?.DisplayName,
                    TargetDisplayName = tAttr?.DisplayName,
                    SourceKind = sAttr?.Kind.ToString() ?? string.Empty,
                    TargetKind = tAttr?.Kind.ToString() ?? string.Empty,
                    SourceRequired = sAttr?.IsRequired ?? false,
                    TargetRequired = tAttr?.IsRequired ?? false
                });
            }

            // Same display-ordering convention used elsewhere in this codebase (see
            // PluginControl.cs's OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)):
            // order by whichever DisplayName is available, Source's if present, else Target's.
            return rows
                .OrderBy(r => r.SourceDisplayName ?? r.TargetDisplayName ?? r.LogicalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
