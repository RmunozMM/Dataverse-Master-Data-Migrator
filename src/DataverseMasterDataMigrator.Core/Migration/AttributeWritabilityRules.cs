using System;
using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Migration
{
    /// <summary>
    /// Single source of truth for "which of a table's Source attributes would actually be written
    /// to Target" — used both by <see cref="MigrationExecutor"/> (to build write payloads) and by
    /// Preflight (<c>PreflightValidator.CheckAttributeSchemaDrift</c>, to warn about a Target
    /// schema gap before it causes a real per-record failure instead of after). Keeping this in
    /// one place means the two can never quietly drift apart.
    /// </summary>
    public static class AttributeWritabilityRules
    {
        public static List<AttributeSummary> GetWritableAttributes(TableSummary sourceTable, ProfileEntity entityConfig, bool restoreState)
        {
            var excluded = new HashSet<string>(entityConfig.ExcludedAttributes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return sourceTable.Attributes
                .Where(a => !string.Equals(a.LogicalName, sourceTable.PrimaryIdAttribute, StringComparison.OrdinalIgnoreCase))
                .Where(a => a.Kind != AttributeKind.Virtual)
                .Where(a => !a.IsOwnerLookup)
                .Where(a => a.IsValidForCreate || a.IsValidForUpdate)
                .Where(a => !excluded.Contains(a.LogicalName))
                .Where(a => restoreState || a.Kind != AttributeKind.StateStatus)
                .ToList();
        }
    }
}
