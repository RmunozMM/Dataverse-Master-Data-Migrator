using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using DataverseMasterDataMigrator.Core.Comparison;
using Xunit;
using Xunit.Abstractions;

namespace DataverseMasterDataMigrator.Tests
{
    public class SchemaComparisonExcelExporterTests
    {
        private readonly ITestOutputHelper _output;

        public SchemaComparisonExcelExporterTests(ITestOutputHelper output)
        {
            _output = output;
        }

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
        public void Build_TwoTables_ProducesIndexPlusOneSheetPerTable()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_name")),
                    Table("wit_persona", attributes: Attr("wit_apellido"))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                Assert.Equal(3, wb.Worksheets.Count);
                Assert.True(wb.Worksheets.Contains("Índice"));
                Assert.True(wb.Worksheets.Contains("wit_tema"));
                Assert.True(wb.Worksheets.Contains("wit_persona"));

                var index = wb.Worksheet("Índice");
                // Header row + 2 data rows.
                Assert.Equal("wit_tema", index.Cell(2, 2).GetString());
                Assert.Equal("wit_persona", index.Cell(3, 2).GetString());
            }
        }

        [Fact]
        public void Build_LongOrForbiddenCharLogicalName_ProducesValidUniqueSheetName()
        {
            var longName = "wit_" + new string('a', 40) + "/end"; // way over 31 chars, has '/'
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table(longName, attributes: Attr("wit_name"))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                Assert.Equal(2, wb.Worksheets.Count);
                var tableSheet = wb.Worksheets.First(ws => ws.Name != "Índice");
                Assert.True(tableSheet.Name.Length <= 31);
                foreach (var forbidden in new[] { '\\', '/', '?', '*', '[', ']', ':' })
                {
                    Assert.DoesNotContain(forbidden, tableSheet.Name);
                }
            }
        }

        [Fact]
        public void Build_TwoTablesCollidingAfterTruncation_ProduceDistinctSheetNames()
        {
            var baseName = "wit_" + new string('x', 30); // 34 chars, identical in the first 31
            var nameA = baseName + "AAAA";
            var nameB = baseName + "BBBB";

            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table(nameA, attributes: Attr("wit_name")),
                    Table(nameB, attributes: Attr("wit_name"))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                Assert.Equal(3, wb.Worksheets.Count);
                var tableSheetNames = new List<string>();
                foreach (var ws in wb.Worksheets)
                {
                    if (ws.Name != "Índice") tableSheetNames.Add(ws.Name);
                }

                Assert.Equal(2, tableSheetNames.Count);
                Assert.NotEqual(tableSheetNames[0], tableSheetNames[1]);
                foreach (var name in tableSheetNames)
                {
                    Assert.True(name.Length <= 31);
                }
            }
        }

        [Fact]
        public void Build_AttributeMissingFromTarget_LeavesTargetKindCellBlank()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_soloSource", existsInTarget: false))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                var sheet = wb.Worksheet("wit_tema");
                // Row layout: 1 back-link, 2 title, 3 blank separator, 4 header, 5 first attribute.
                var attrRow = 5;
                Assert.Equal("wit_soloSource", sheet.Cell(attrRow, 1).GetString());
                // The attribute is missing from Target: the Target columns must stay untouched
                // (a real blank cell), not carry a literal "" string or placeholder.
                Assert.True(sheet.Cell(attrRow, 5).IsEmpty());
                Assert.True(sheet.Cell(attrRow, 6).IsEmpty());
                Assert.True(sheet.Cell(attrRow, 7).IsEmpty());
                Assert.Equal(string.Empty, sheet.Cell(attrRow, 5).GetString());
            }
        }

        [Fact]
        public void Build_TableMissingFromTarget_ShowsMessageOnItsSheet()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_soloSource", existsInSource: true, existsInTarget: false, attributes: Attr("wit_a", existsInTarget: false))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                var sheet = wb.Worksheet("wit_soloSource");
                var found = false;
                foreach (var cell in sheet.CellsUsed())
                {
                    if (cell.GetString().Contains("no existe en Target")) found = true;
                }
                Assert.True(found);
            }
        }

        [Fact]
        public void Build_IndexHyperlink_TargetsTheTableOwnWorksheet()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_name"))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                var index = wb.Worksheet("Índice");
                var cell = index.Cell(2, 1);
                Assert.True(cell.HasHyperlink);

                var link = cell.GetHyperlink();
                _output.WriteLine("InternalAddress=" + link.InternalAddress);
                _output.WriteLine("IsExternal=" + link.IsExternal);

                Assert.False(link.IsExternal);
                Assert.Contains("wit_tema", link.InternalAddress);
            }
        }

        [Fact]
        public void Build_TableSheet_HasBackLinkHyperlinkToIndex()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_name"))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                var sheet = wb.Worksheet("wit_tema");
                var backLinkCell = sheet.Cell(1, 1);
                Assert.True(backLinkCell.HasHyperlink);
                Assert.Contains("Volver al índice", backLinkCell.GetString());

                var link = backLinkCell.GetHyperlink();
                Assert.False(link.IsExternal);
                Assert.Contains("Índice", link.InternalAddress);
            }
        }

        [Fact]
        public void Build_EmptyResult_ProducesOnlyIndexSheet()
        {
            var result = new SchemaComparisonResult { Tables = new List<TableComparison>() };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                Assert.Single(wb.Worksheets);
                Assert.True(wb.Worksheets.Contains("Índice"));
            }
        }

        [Fact]
        public void Build_MismatchedAttributeRow_IsFilledWithHighlightColor()
        {
            var result = new SchemaComparisonResult
            {
                Tables = new List<TableComparison>
                {
                    Table("wit_tema", attributes: Attr("wit_mismatch", sourceRequired: true, targetRequired: false))
                }
            };

            var bytes = new SchemaComparisonExcelExporter().Build(result, "DEV", "PROD");

            using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            {
                var sheet = wb.Worksheet("wit_tema");
                var attrRow = 5;
                var fillColor = sheet.Cell(attrRow, 1).Style.Fill.BackgroundColor;
                Assert.NotEqual(XLColor.NoColor, fillColor);
            }
        }
    }
}
