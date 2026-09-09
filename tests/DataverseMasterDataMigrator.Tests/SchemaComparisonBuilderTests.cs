using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Comparison;
using DataverseMasterDataMigrator.Core.Models;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class SchemaComparisonBuilderTests
    {
        private static AttributeSummary Attr(string logicalName, AttributeKind kind = AttributeKind.Primitive, bool required = false, string displayName = null) => new AttributeSummary
        {
            LogicalName = logicalName,
            DisplayName = displayName ?? logicalName,
            Kind = kind,
            RequiredLevel = required ? "ApplicationRequired" : "None"
        };

        private static TableSummary Table(string logicalName, params AttributeSummary[] attributes) => new TableSummary
        {
            LogicalName = logicalName,
            DisplayName = logicalName,
            Attributes = attributes.ToList()
        };

        [Fact]
        public void AttributeSameOnBothSides_IsNotMismatched_AndTableHasNoDifferences()
        {
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_name", AttributeKind.Primitive, required: true))
            };
            var targetTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_name", AttributeKind.Primitive, required: true))
            };

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_tema" });

            var table = Assert.Single(result.Tables);
            var attr = Assert.Single(table.Attributes);
            Assert.False(attr.IsMismatched);
            Assert.False(table.HasDifferences);
        }

        [Fact]
        public void AttributeOnlyInSource_ExistsInTargetIsFalse_TargetKindEmpty_AndIsMismatched()
        {
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_onlysource", AttributeKind.Primitive))
            };
            var targetTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema")
            };

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_tema" });

            var table = Assert.Single(result.Tables);
            var attr = Assert.Single(table.Attributes);
            Assert.True(attr.ExistsInSource);
            Assert.False(attr.ExistsInTarget);
            // Convention: TargetKind is empty string (not null) when the side doesn't have the attribute.
            Assert.Equal(string.Empty, attr.TargetKind);
            Assert.True(attr.IsMismatched);
            Assert.True(table.HasDifferences);
        }

        [Fact]
        public void TablePresentOnlyInSource_ExistsInTargetFalse_AttributesStillPopulatedFromSource()
        {
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_a"), Attr("wit_b"))
            };
            var targetTables = new Dictionary<string, TableSummary>();

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_tema" });

            var table = Assert.Single(result.Tables);
            Assert.True(table.ExistsInSource);
            Assert.False(table.ExistsInTarget);
            Assert.Equal(2, table.Attributes.Count);
            Assert.All(table.Attributes, a => Assert.False(a.ExistsInTarget));
            Assert.All(table.Attributes, a => Assert.True(a.ExistsInSource));
            Assert.True(table.HasDifferences);
        }

        [Fact]
        public void AttributeOnBothSidesWithDifferentKind_IsMismatched()
        {
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_flex", AttributeKind.Primitive))
            };
            var targetTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_flex", AttributeKind.Lookup))
            };

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_tema" });

            var attr = Assert.Single(Assert.Single(result.Tables).Attributes);
            Assert.True(attr.ExistsInSource);
            Assert.True(attr.ExistsInTarget);
            Assert.True(attr.IsMismatched);
        }

        [Fact]
        public void AttributeOnBothSidesWithDifferentRequiredness_IsMismatched()
        {
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_flex", AttributeKind.Primitive, required: true))
            };
            var targetTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema", Attr("wit_flex", AttributeKind.Primitive, required: false))
            };

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_tema" });

            var attr = Assert.Single(Assert.Single(result.Tables).Attributes);
            Assert.True(attr.IsMismatched);
        }

        [Fact]
        public void TableOrder_MatchesLogicalNamesInOrder_RegardlessOfDictionaryOrder()
        {
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_c"] = Table("wit_c"),
                ["wit_a"] = Table("wit_a"),
                ["wit_b"] = Table("wit_b")
            };
            var targetTables = new Dictionary<string, TableSummary>
            {
                ["wit_c"] = Table("wit_c"),
                ["wit_a"] = Table("wit_a"),
                ["wit_b"] = Table("wit_b")
            };

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_b", "wit_c", "wit_a" });

            Assert.Equal(new[] { "wit_b", "wit_c", "wit_a" }, result.Tables.Select(t => t.LogicalName).ToArray());
        }

        [Fact]
        public void TableMissingFromBothSides_StillProduced_AsMissingEverywhere()
        {
            var sourceTables = new Dictionary<string, TableSummary>();
            var targetTables = new Dictionary<string, TableSummary>();

            var result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, new[] { "wit_ghost" });

            var table = Assert.Single(result.Tables);
            Assert.Equal("wit_ghost", table.LogicalName);
            Assert.False(table.ExistsInSource);
            Assert.False(table.ExistsInTarget);
            Assert.Empty(table.Attributes);
        }
    }
}
