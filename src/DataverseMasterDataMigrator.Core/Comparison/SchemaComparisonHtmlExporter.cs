using System.Net;
using System.Text;

namespace DataverseMasterDataMigrator.Core.Comparison
{
    /// <summary>Turns a <see cref="SchemaComparisonResult"/> into one self-contained HTML document
    /// (inline CSS, no external resources, no JavaScript) that can be opened directly in a browser —
    /// an index of every table jumping to a per-table attribute comparison section. All user-facing
    /// text is neutral Spanish (this report specifically; the rest of the plugin's UI stays English).
    /// Built with plain StringBuilder concatenation, matching this Core project's existing style.</summary>
    public sealed class SchemaComparisonHtmlExporter
    {
        /// <param name="sourceLabel">Human label for the Source connection (e.g. "DEV"), shown in
        /// column headers/titles — the report has no other way to know which environment is which.</param>
        /// <param name="targetLabel">Same, for Target.</param>
        public string Build(SchemaComparisonResult result, string sourceLabel, string targetLabel)
        {
            var tables = result?.Tables ?? new System.Collections.Generic.List<TableComparison>();
            var sourceLabelHtml = Encode(sourceLabel);
            var targetLabelHtml = Encode(targetLabel);

            var tableCount = tables.Count;
            var mismatchCount = 0;
            foreach (var t in tables)
            {
                if (t.HasDifferences) mismatchCount++;
            }

            var sb = new StringBuilder();

            sb.Append("<!DOCTYPE html>\n");
            sb.Append("<html lang=\"es\">\n<head>\n");
            sb.Append("<meta charset=\"utf-8\" />\n");
            sb.Append("<title>Reporte de comparacion de esquema</title>\n");
            sb.Append("<style>\n");
            sb.Append(Css);
            sb.Append("\n</style>\n</head>\n<body>\n");

            sb.Append("<div class=\"content\">\n");
            sb.Append("<h1>Reporte de comparacion de esquema</h1>\n");
            sb.Append("<p class=\"summary\">Source: <strong>").Append(sourceLabelHtml)
              .Append("</strong> &mdash; Target: <strong>").Append(targetLabelHtml).Append("</strong></p>\n");
            sb.Append("<p class=\"summary\">").Append(tableCount).Append(" tabla(s) comparada(s), ")
              .Append(mismatchCount).Append(" con diferencias.</p>\n");

            AppendIndex(sb, tables);

            foreach (var table in tables)
            {
                AppendTableSection(sb, table, sourceLabelHtml, targetLabelHtml);
            }

            sb.Append("</div>\n</body>\n</html>\n");

            return sb.ToString();
        }

        private static void AppendIndex(StringBuilder sb, System.Collections.Generic.IReadOnlyList<TableComparison> tables)
        {
            sb.Append("<h2 id=\"index\">Indice</h2>\n");

            if (tables.Count == 0)
            {
                sb.Append("<p>No hay tablas en esta comparacion.</p>\n");
                return;
            }

            sb.Append("<table class=\"index-table\">\n<thead><tr>");
            sb.Append("<th>Tabla</th><th>Existe en Source</th><th>Existe en Target</th><th>Diferencias</th>");
            sb.Append("</tr></thead>\n<tbody>\n");

            foreach (var table in tables)
            {
                var anchor = AnchorId(table.LogicalName);
                var rowClass = table.HasDifferences ? " class=\"has-diff\"" : "";

                sb.Append("<tr").Append(rowClass).Append(">\n");
                sb.Append("<td><a href=\"#table-").Append(anchor).Append("\">")
                  .Append(Encode(table.DisplayName)).Append(" (").Append(Encode(table.LogicalName)).Append(")</a></td>\n");
                sb.Append("<td>").Append(SiNo(table.ExistsInSource)).Append("</td>\n");
                sb.Append("<td>").Append(SiNo(table.ExistsInTarget)).Append("</td>\n");
                sb.Append("<td>").Append(table.HasDifferences ? "Si" : "No").Append("</td>\n");
                sb.Append("</tr>\n");
            }

            sb.Append("</tbody>\n</table>\n");
        }

        private static void AppendTableSection(StringBuilder sb, TableComparison table, string sourceLabelHtml, string targetLabelHtml)
        {
            var anchor = AnchorId(table.LogicalName);
            var displayName = Encode(table.DisplayName);
            var logicalName = Encode(table.LogicalName);

            sb.Append("<h2 id=\"table-").Append(anchor).Append("\">")
              .Append(displayName).Append(" (").Append(logicalName).Append(")</h2>\n");

            if (!table.ExistsInSource)
            {
                sb.Append("<p class=\"missing-side\">Esta tabla no existe en Source.</p>\n");
            }

            if (!table.ExistsInTarget)
            {
                sb.Append("<p class=\"missing-side\">Esta tabla no existe en Target.</p>\n");
            }

            if (table.Attributes.Count == 0)
            {
                sb.Append("<p>No hay atributos para comparar en esta tabla.</p>\n");
            }
            else
            {
                sb.Append("<table class=\"attr-table\">\n<thead><tr>");
                sb.Append("<th colspan=\"3\">Source (").Append(sourceLabelHtml).Append(")</th>");
                sb.Append("<th colspan=\"3\">Target (").Append(targetLabelHtml).Append(")</th>");
                sb.Append("</tr><tr>");
                sb.Append("<th>Nombre</th><th>Tipo</th><th>Obligatorio</th>");
                sb.Append("<th>Nombre</th><th>Tipo</th><th>Obligatorio</th>");
                sb.Append("</tr></thead>\n<tbody>\n");

                foreach (var attr in table.Attributes)
                {
                    var rowClass = attr.IsMismatched ? " class=\"has-diff\"" : "";
                    sb.Append("<tr").Append(rowClass).Append(">\n");

                    sb.Append("<td>").Append(attr.ExistsInSource ? Encode(attr.SourceDisplayName) : "").Append("</td>\n");
                    sb.Append("<td>").Append(attr.ExistsInSource ? Encode(attr.SourceKind) : "").Append("</td>\n");
                    sb.Append("<td>").Append(attr.ExistsInSource ? SiNo(attr.SourceRequired) : "").Append("</td>\n");

                    sb.Append("<td>").Append(attr.ExistsInTarget ? Encode(attr.TargetDisplayName) : "").Append("</td>\n");
                    sb.Append("<td>").Append(attr.ExistsInTarget ? Encode(attr.TargetKind) : "").Append("</td>\n");
                    sb.Append("<td>").Append(attr.ExistsInTarget ? SiNo(attr.TargetRequired) : "").Append("</td>\n");

                    sb.Append("</tr>\n");
                }

                sb.Append("</tbody>\n</table>\n");
            }

            sb.Append("<p><a href=\"#index\">&uarr; Volver al indice</a></p>\n");
        }

        private static string AnchorId(string logicalName)
        {
            // Logical names are already safe identifier-like tokens (letters/digits/underscore),
            // but encode defensively in case metadata ever surprises us with odd characters.
            return Encode(logicalName);
        }

        private static string SiNo(bool value) => value ? "Si" : "No";

        private static string Encode(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private const string Css =
@"body { background: #f5f5f5; color: #222; }
.content { font-family: Segoe UI, Arial, sans-serif; max-width: 1100px; margin: 0 auto; padding: 24px; background: #fff; }
h1 { font-size: 1.6em; margin-bottom: 4px; }
h2 { margin-top: 2em; border-bottom: 2px solid #ccc; padding-bottom: 4px; }
.summary { color: #444; margin: 4px 0; }
table { border-collapse: collapse; width: 100%; margin: 12px 0; }
th, td { border: 1px solid #ccc; padding: 6px 10px; text-align: left; font-size: 0.92em; }
th { background: #eef1f5; }
tr.has-diff { background: #fff2cc; }
.missing-side { font-weight: bold; color: #a94442; background: #f2dede; padding: 8px 12px; border: 1px solid #ebccd1; }
a { color: #2a5db0; }";
    }
}
