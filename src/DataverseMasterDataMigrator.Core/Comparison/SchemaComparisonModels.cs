using System.Collections.Generic;
using System.Linq;

namespace DataverseMasterDataMigrator.Core.Comparison
{
    /// <summary>Structure-only comparison (no data/records) between Source and Target for every
    /// table in a migration profile — lets the user spot schema drift (missing tables/attributes,
    /// or a mismatched type/required-ness) before running a real migration.</summary>
    public sealed class SchemaComparisonResult
    {
        public IReadOnlyList<TableComparison> Tables { get; set; } = new List<TableComparison>();
    }

    public sealed class TableComparison
    {
        public string LogicalName { get; set; }

        /// <summary>Whichever side has it — Source's if both do.</summary>
        public string DisplayName { get; set; }

        public bool ExistsInSource { get; set; }
        public bool ExistsInTarget { get; set; }

        public IReadOnlyList<AttributeComparison> Attributes { get; set; } = new List<AttributeComparison>();

        /// <summary>True if the table itself, or any of its attributes, differ between sides —
        /// the single flag a report/summary would use to highlight "needs attention".</summary>
        public bool HasDifferences =>
            ExistsInSource != ExistsInTarget || Attributes.Count(a => a.IsMismatched) > 0;
    }

    public sealed class AttributeComparison
    {
        public string LogicalName { get; set; }

        public bool ExistsInSource { get; set; }
        public bool ExistsInTarget { get; set; }

        public string SourceDisplayName { get; set; }
        public string TargetDisplayName { get; set; }

        /// <summary>The attribute's <c>AttributeKind</c> as text (e.g. "String", "Lookup") — text,
        /// not the enum itself, so this model stays independent of exactly which Core namespace
        /// defines AttributeKind. Empty string when the side doesn't have the attribute.</summary>
        public string SourceKind { get; set; } = string.Empty;
        public string TargetKind { get; set; } = string.Empty;

        public bool SourceRequired { get; set; }
        public bool TargetRequired { get; set; }

        /// <summary>True when the attribute is missing from one side, or present on both but with
        /// a different Kind or a different Required-ness.</summary>
        public bool IsMismatched =>
            ExistsInSource != ExistsInTarget ||
            (ExistsInSource && ExistsInTarget &&
             (!string.Equals(SourceKind, TargetKind, System.StringComparison.OrdinalIgnoreCase) ||
              SourceRequired != TargetRequired));
    }
}
