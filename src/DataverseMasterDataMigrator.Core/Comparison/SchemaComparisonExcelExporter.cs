using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;

namespace DataverseMasterDataMigrator.Core.Comparison
{
    /// <summary>Turns a <see cref="SchemaComparisonResult"/> into a real .xlsx workbook: one
    /// "Índice" worksheet plus one worksheet per table, with internal hyperlinks both ways and
    /// mismatch highlighting — the same information as <see cref="SchemaComparisonHtmlExporter"/>,
    /// as a workbook instead of a web page. All user-facing text is neutral Spanish.</summary>
    public sealed class SchemaComparisonExcelExporter
    {
        private const string IndexSheetName = "Índice";
        private static readonly char[] ForbiddenSheetNameChars = { '\\', '/', '?', '*', '[', ']', ':' };
        private static readonly XLColor DiffFillColor = XLColor.FromHtml("#FFF2CC");
        private static readonly XLColor MissingSideFontColor = XLColor.FromHtml("#A94442");

        /// <param name="sourceLabel">Human label for Source (e.g. "DEV").</param>
        /// <param name="targetLabel">Human label for Target (e.g. "QAS").</param>
        /// <returns>The raw bytes of a real .xlsx workbook — the caller decides where to save it.</returns>
        public byte[] Build(SchemaComparisonResult result, string sourceLabel, string targetLabel)
        {
            var tables = result?.Tables ?? new List<TableComparison>();
            sourceLabel = sourceLabel ?? string.Empty;
            targetLabel = targetLabel ?? string.Empty;

            using (var workbook = new XLWorkbook())
            {
                var indexSheet = workbook.Worksheets.Add(IndexSheetName);

                // Reserve every sheet name up front (index first) so a table's sanitized name can
                // never collide with it, then assign a unique, valid name to each table in order.
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { IndexSheetName };
                var tableSheets = new IXLWorksheet[tables.Count];
                var sheetNames = new string[tables.Count];

                for (var i = 0; i < tables.Count; i++)
                {
                    var sheetName = MakeUniqueSheetName(tables[i].LogicalName, i, usedNames);
                    sheetNames[i] = sheetName;
                    tableSheets[i] = workbook.Worksheets.Add(sheetName);
                }

                BuildIndexSheet(indexSheet, tables, tableSheets);

                for (var i = 0; i < tables.Count; i++)
                {
                    BuildTableSheet(tableSheets[i], indexSheet, tables[i], sourceLabel, targetLabel);
                }

                using (var stream = new MemoryStream())
                {
                    workbook.SaveAs(stream);
                    return stream.ToArray();
                }
            }
        }

        private static void BuildIndexSheet(IXLWorksheet indexSheet, IReadOnlyList<TableComparison> tables, IXLWorksheet[] tableSheets)
        {
            indexSheet.Cell(1, 1).Value = "Nombre para mostrar";
            indexSheet.Cell(1, 2).Value = "Nombre lógico";
            indexSheet.Cell(1, 3).Value = "Existe en Source";
            indexSheet.Cell(1, 4).Value = "Existe en Target";
            indexSheet.Cell(1, 5).Value = "Con diferencias";
            indexSheet.Range(1, 1, 1, 5).Style.Font.Bold = true;

            for (var i = 0; i < tables.Count; i++)
            {
                var table = tables[i];
                var row = i + 2;

                var displayCell = indexSheet.Cell(row, 1);
                displayCell.Value = table.DisplayName ?? string.Empty;
                displayCell.SetHyperlink(new XLHyperlink(tableSheets[i].Cell(1, 1)));

                indexSheet.Cell(row, 2).Value = table.LogicalName ?? string.Empty;
                indexSheet.Cell(row, 3).Value = SiNo(table.ExistsInSource);
                indexSheet.Cell(row, 4).Value = SiNo(table.ExistsInTarget);
                indexSheet.Cell(row, 5).Value = SiNo(table.HasDifferences);

                if (table.HasDifferences)
                {
                    indexSheet.Range(row, 1, row, 5).Style.Fill.BackgroundColor = DiffFillColor;
                }
            }

            indexSheet.Columns().AdjustToContents();
        }

        private static void BuildTableSheet(IXLWorksheet sheet, IXLWorksheet indexSheet, TableComparison table, string sourceLabel, string targetLabel)
        {
            var currentRow = 1;

            var backLinkCell = sheet.Cell(currentRow, 1);
            backLinkCell.Value = "← Volver al índice";
            backLinkCell.SetHyperlink(new XLHyperlink(indexSheet.Cell(1, 1)));
            currentRow++;

            var titleCell = sheet.Cell(currentRow, 1);
            titleCell.Value = string.Format(
                "{0} ({1}) — Source: {2} · Target: {3}",
                table.DisplayName ?? string.Empty,
                table.LogicalName ?? string.Empty,
                sourceLabel,
                targetLabel);
            titleCell.Style.Font.Bold = true;
            currentRow++;

            if (!table.ExistsInSource)
            {
                currentRow = AppendMissingSideMessage(sheet, currentRow, "Esta tabla no existe en Source.");
            }

            if (!table.ExistsInTarget)
            {
                currentRow = AppendMissingSideMessage(sheet, currentRow, "Esta tabla no existe en Target.");
            }

            currentRow++; // Blank row before the grid.

            var attributes = table.Attributes ?? new List<AttributeComparison>();

            if (attributes.Count == 0)
            {
                sheet.Cell(currentRow, 1).Value = "No hay atributos para comparar en esta tabla.";
            }
            else
            {
                var headerRow = currentRow;
                sheet.Cell(headerRow, 1).Value = "Atributo";
                sheet.Cell(headerRow, 2).Value = "Source: Nombre";
                sheet.Cell(headerRow, 3).Value = "Source: Tipo";
                sheet.Cell(headerRow, 4).Value = "Source: Obligatorio";
                sheet.Cell(headerRow, 5).Value = "Target: Nombre";
                sheet.Cell(headerRow, 6).Value = "Target: Tipo";
                sheet.Cell(headerRow, 7).Value = "Target: Obligatorio";
                sheet.Range(headerRow, 1, headerRow, 7).Style.Font.Bold = true;
                currentRow++;

                foreach (var attr in attributes)
                {
                    var row = currentRow;
                    sheet.Cell(row, 1).Value = attr.LogicalName ?? string.Empty;

                    if (attr.ExistsInSource)
                    {
                        sheet.Cell(row, 2).Value = attr.SourceDisplayName ?? string.Empty;
                        sheet.Cell(row, 3).Value = attr.SourceKind ?? string.Empty;
                        sheet.Cell(row, 4).Value = SiNo(attr.SourceRequired);
                    }

                    if (attr.ExistsInTarget)
                    {
                        sheet.Cell(row, 5).Value = attr.TargetDisplayName ?? string.Empty;
                        sheet.Cell(row, 6).Value = attr.TargetKind ?? string.Empty;
                        sheet.Cell(row, 7).Value = SiNo(attr.TargetRequired);
                    }

                    if (attr.IsMismatched)
                    {
                        sheet.Range(row, 1, row, 7).Style.Fill.BackgroundColor = DiffFillColor;
                    }

                    currentRow++;
                }
            }

            sheet.Columns().AdjustToContents();
        }

        private static int AppendMissingSideMessage(IXLWorksheet sheet, int row, string message)
        {
            var cell = sheet.Cell(row, 1);
            cell.Value = message;
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = MissingSideFontColor;
            return row + 1;
        }

        /// <summary>Sanitizes a logical name into a valid Excel sheet name (max 31 chars, none of
        /// <c>\ / ? * [ ] :</c>) and, if that collides with an already-used name, appends a short
        /// numeric suffix (trimming further as needed) until it is unique.</summary>
        private static string MakeUniqueSheetName(string logicalName, int fallbackIndex, HashSet<string> usedNames)
        {
            var sanitized = SanitizeSheetNameChars(logicalName);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "Tabla" + (fallbackIndex + 1);
            }

            if (sanitized.Length > 31)
            {
                sanitized = sanitized.Substring(0, 31);
            }

            if (usedNames.Add(sanitized))
            {
                return sanitized;
            }

            var suffixNumber = 2;
            while (true)
            {
                var suffix = "~" + suffixNumber;
                var maxBaseLength = 31 - suffix.Length;
                var baseName = sanitized.Length > maxBaseLength ? sanitized.Substring(0, maxBaseLength) : sanitized;
                var candidate = baseName + suffix;

                if (usedNames.Add(candidate))
                {
                    return candidate;
                }

                suffixNumber++;
            }
        }

        private static string SanitizeSheetNameChars(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(ForbiddenSheetNameChars, chars[i]) >= 0)
                {
                    chars[i] = '_';
                }
            }

            return new string(chars);
        }

        private static string SiNo(bool value) => value ? "Sí" : "No";
    }
}
