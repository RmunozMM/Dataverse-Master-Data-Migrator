using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.XrmToolBox.UI
{
    /// <summary>
    /// Shown by "Find Related Tables": lets the user confirm which of the discovered custom-table
    /// dependencies to add to the current selection, pre-checked since adding all of them is the
    /// common case.
    /// </summary>
    internal sealed class RelatedTablesForm : Form
    {
        private readonly CheckedListBox _list;

        public IReadOnlyList<TableSummary> SelectedTables { get; private set; } = new List<TableSummary>();

        private sealed class Entry
        {
            public TableSummary Table { get; }
            public Entry(TableSummary table) => Table = table;
            public override string ToString() => $"{Table.DisplayName}  ({Table.LogicalName})";
        }

        public RelatedTablesForm(IReadOnlyList<TableSummary> candidates)
        {
            Text = "Related Tables Found";
            StartPosition = FormStartPosition.CenterParent;
            Width = 480;
            Height = 420;
            MinimizeBox = false;
            ShowIcon = false;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(15) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var intro = new Label
            {
                Text = "These custom tables are referenced by lookups from your currently selected tables, " +
                       "but aren't selected yet. Add them to your selection?",
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 50
            };

            _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            foreach (var table in candidates)
            {
                int index = _list.Items.Add(new Entry(table));
                _list.SetItemChecked(index, true);
            }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            var okButton = new Button { Text = "Add Selected", DialogResult = DialogResult.OK, Width = 110, Height = 30 };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            okButton.Click += (s, e) => SelectedTables = _list.CheckedItems.OfType<Entry>().Select(x => x.Table).ToList();
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);

            root.Controls.Add(intro, 0, 0);
            root.Controls.Add(_list, 0, 1);
            root.Controls.Add(buttons, 0, 2);

            Controls.Add(root);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
}
