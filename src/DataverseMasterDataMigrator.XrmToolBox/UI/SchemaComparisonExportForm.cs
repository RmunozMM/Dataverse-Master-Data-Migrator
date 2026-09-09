using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using DataverseMasterDataMigrator.Core.Comparison;

namespace DataverseMasterDataMigrator.XrmToolBox.UI
{
    /// <summary>
    /// Shown after "Compare Structure": summarizes the schema comparison result and lets the user
    /// export it to HTML or Excel on demand. No grids of its own — the actual report content lives
    /// in the exported file, built lazily by <see cref="SchemaComparisonHtmlExporter"/> /
    /// <see cref="SchemaComparisonExcelExporter"/> only when the user picks a save location.
    /// </summary>
    internal sealed class SchemaComparisonExportForm : Form
    {
        private readonly SchemaComparisonResult _result;
        private readonly string _sourceLabel;
        private readonly string _targetLabel;

        public SchemaComparisonExportForm(SchemaComparisonResult result, string sourceLabel, string targetLabel)
        {
            _result = result;
            _sourceLabel = sourceLabel;
            _targetLabel = targetLabel;

            Text = "Structure Comparison";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Width = 420;
            Height = 220;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowIcon = false;

            int tableCount = _result.Tables.Count;
            int mismatchedCount = _result.Tables.Count(t => t.HasDifferences);

            var summaryLabel = new Label
            {
                Text = $"{tableCount} table(s) compared, {mismatchedCount} with differences.",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 50,
                Padding = new Padding(15, 15, 15, 0)
            };

            var exportButtons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(15, 5, 15, 5) };
            var btnExportHtml = new Button { Text = "Export to HTML", Width = 150, Height = 32 };
            btnExportHtml.Click += (s, e) => ExportToHtml();
            var btnExportExcel = new Button { Text = "Export to Excel", Width = 150, Height = 32, Margin = new Padding(10, 0, 0, 0) };
            btnExportExcel.Click += (s, e) => ExportToExcel();
            exportButtons.Controls.Add(btnExportHtml);
            exportButtons.Controls.Add(btnExportExcel);

            var closePanel = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(15) };
            var btnClose = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            closePanel.Controls.Add(btnClose);

            Controls.Add(exportButtons);
            Controls.Add(summaryLabel);
            Controls.Add(closePanel);

            CancelButton = btnClose;
        }

        private void ExportToHtml()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "HTML files (*.html)|*.html",
                FileName = $"SchemaComparison_{DateTime.Now:yyyyMMdd_HHmmss}.html"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var html = new SchemaComparisonHtmlExporter().Build(_result, _sourceLabel, _targetLabel);
                    File.WriteAllText(dialog.FileName, html, Encoding.UTF8);
                    System.Diagnostics.Process.Start(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not export to HTML: " + ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportToExcel()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "Excel files (*.xlsx)|*.xlsx",
                FileName = $"SchemaComparison_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    var bytes = new SchemaComparisonExcelExporter().Build(_result, _sourceLabel, _targetLabel);
                    File.WriteAllBytes(dialog.FileName, bytes);
                    System.Diagnostics.Process.Start(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not export to Excel: " + ex.Message, "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
