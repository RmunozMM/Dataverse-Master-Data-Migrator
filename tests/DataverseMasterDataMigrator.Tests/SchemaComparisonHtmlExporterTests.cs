using System.Collections.Generic;
using DataverseMasterDataMigrator.Core.Comparison;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class SchemaComparisonHtmlExporterTests
    {
        private static AttributeComparison Attr(
            string logicalName,
            bool existsInSource = true,
            bool existsInTarget = true,
            string sourceDisplayName = null,
            string targetDisplayName = null,
            string sourceKind = "Primitive",
            string targetKind = "Primitive",
            bool sourceRequired = false,
            bool targetRequired = false) => new AttributeComparison
        {
            LogicalName = logicalName,
            ExistsInSource = existsInSource,
            ExistsInTarget = existsInTarget,
            SourceDisplayName = existsInSource ? (sourceDisplayName ?? logicalName) : null,
            TargetDisplayName = existsInTarget ? (targetDisplayName ?? logicalName) : null,
            SourceKind = existsInSource ? sourceKind : string.Empty,
            TargetKind = existsInTarget ? targetKind : string.Empty,
            SourceRequired = sourceRequired,
            TargetRequired = targetRequired
        };

        private static TableComparison Table(
            string logicalName,
            string displayName = null,
            bool existsInSource = true,
            bool existsInTarget = true,
            params AttributeComparison[] attributes) => new TableComparison
        {
            LogicalName = logicalName,
            DisplayName = displayName ?? logicalName,
            ExistsInSource = existsInSource,
            ExistsInTarget = existsInTarget,
            Attributes = new List<AttributeComparison>(attributes)
        };

        [Fact]
        public void Build_ContainsTableAnchorAndIndexLink_ForEveryTable()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_name")),
                    Table("wit_persona", attributes: Attr("wit_apellido"))
                }
            };

            var html = new SchemaComparisonHtmlExporter().Build(result, "DEV", "PROD");

            Assert.Contains("id=\"table-wit_tema\"", html);
            Assert.Contains("id=\"table-wit_persona\"", html);
            Assert.Contains("href=\"#table-wit_tema\"", html);
            Assert.Contains("href=\"#table-wit_persona\"", html);
        }

        [Fact]
        public void Build_HasIndexAnchor_AndBackToIndexLinks()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_name"))
                }
            };

            var html = new SchemaComparisonHtmlExporter().Build(result, "DEV", "PROD");

            Assert.Contains("id=\"index\"", html);
            Assert.Contains("href=\"#index\"", html);
        }

        [Fact]
        public void Build_EscapesHostileDisplayNames()
        {
            const string hostile = "Tabla <script>&Test</script>";
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_hostile", displayName: hostile, attributes: Attr("wit_field", sourceDisplayName: hostile))
                }
            };

            var html = new SchemaComparisonHtmlExporter().Build(result, "DEV", "PROD");

            Assert.DoesNotContain("<script>&Test</script>", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("&amp;Test", html);
        }

        [Fact]
        public void Build_TableMissingFromTarget_ProducesClearIndication()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_soloSource", existsInSource: true, existsInTarget: false, attributes: Attr("wit_a", existsInTarget: false))
                }
            };

            var html = new SchemaComparisonHtmlExporter().Build(result, "DEV", "PROD");

            Assert.Contains("Esta tabla no existe en Target", html);
        }

        [Fact]
        public void Build_EmptyResult_DoesNotThrow_AndProducesValidHtmlSkeleton()
        {
            var result = new SchemaComparisonResult { Tables = new List<TableComparison>() };

            var html = new SchemaComparisonHtmlExporter().Build(result, "DEV", "PROD");

            Assert.False(string.IsNullOrWhiteSpace(html));
            Assert.Contains("<html", html);
            Assert.Contains("</html>", html);
            Assert.Contains("<body>", html);
            Assert.Contains("</body>", html);
            Assert.DoesNotContain("id=\"table-", html);
        }
    }
}
