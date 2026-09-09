using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using DataverseMasterDataMigrator.Core.Validation;

namespace DataverseMasterDataMigrator.XrmToolBox.UI
{
    /// <summary>
    /// Shows what Execute would actually do — a FileZilla-style dual-pane comparison (real
    /// feedback: "show me both environments at the same time, like an FTP client, with
    /// differences marked in color") instead of a single New/Update-flagged grid. Left pane is
    /// Source, right pane is Target — both bound to the SAME underlying row list, so they always
    /// show the same records in the same order and scroll/select in lockstep. Both panes show the
    /// same columns (Id, Name, Status) — real feedback: they didn't before, which was confusing
    /// when comparing side by side.
    ///
    /// Lets the user manually curate the selection before running Execute: uncheck individual
    /// records (left pane only — a Target row isn't something you "select"), or bulk-select "New
    /// only" / "Update only" (real feedback: "let me pick record by record, or just the new ones,
    /// or just the updates").
    ///
    /// Outer chrome is a single-column TableLayoutPanel (`root`, 5 rows: 4 AutoSize bars —
    /// tableBar, legend, bulkBar, _truncatedNotice — then one Percent(100) row for `split`, the
    /// SplitContainer holding the grids), each control added to an explicit (0, row) cell — the
    /// same pattern already used by PluginControl.Layout.cs's BuildTablesTab(). This replaced an
    /// earlier version built entirely from plain Dock=Top/Fill assignments on the Form's own
    /// Controls collection, relying on an add-order convention for same-Dock siblings that was
    /// easy to get subtly wrong (real feedback, most recently: rows rendering correctly but
    /// visually occluded behind the top toolbar). With explicit per-cell placement there is no
    /// Z-order/add-order ambiguity left to get wrong.
    /// </summary>
    internal sealed class PreviewResultsForm : Form
    {
        private static readonly Color NewColor = Color.Honeydew;
        private static readonly Color UpdateSameColor = Color.WhiteSmoke;
        private static readonly Color UpdateDiffersColor = Color.MistyRose;
        private static readonly Color MissingColor = Color.Gainsboro;

        private readonly Dictionary<string, BindingList<PreviewRowViewModel>> _rowsByTable =
            new Dictionary<string, BindingList<PreviewRowViewModel>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _truncatedByTable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _totalByTable = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private readonly ComboBox _tableCombo;
        private readonly DataGridView _sourceGrid;
        private readonly DataGridView _targetGrid;
        private readonly Label _truncatedNotice;
        private bool _syncing;

        /// <summary>Per table, the ids the user unchecked — populated only when OK is clicked.
        /// A table never opened, or with nothing unchecked, is simply absent (meaning "migrate
        /// every record", the unchanged default).</summary>
        public IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> ExcludedRecordIds { get; private set; } =
            new Dictionary<string, IReadOnlyCollection<Guid>>();

        private sealed class PreviewRowViewModel
        {
            public bool Included { get; set; } = true;
            public Guid Id { get; set; }
            public string SourceName { get; set; }
            public string TargetName { get; set; }
            public string State { get; set; }

            /// <summary>True only for Update rows whose name actually differs between Source and
            /// Target — the closest single-field proxy we have for "this record will really
            /// change", without fetching and diffing every attribute of every record.</summary>
            public bool NameDiffers =>
                State == nameof(RecordPreviewState.Update) && !string.Equals(SourceName, TargetName, StringComparison.Ordinal);
        }

        private sealed class TableComboEntry
        {
            public TableDataPreview Table { get; }
            public TableComboEntry(TableDataPreview table) => Table = table;
            public override string ToString()
            {
                var label = string.IsNullOrEmpty(Table.DisplayName)
                    ? Table.LogicalName
                    : $"{Table.DisplayName}  ({Table.LogicalName})";
                return $"{label} — {Table.SourceRecordCount} total, {Table.ToCreate} new, {Table.ToUpdate} update";
            }
        }

        public PreviewResultsForm(IReadOnlyList<TableDataPreview> tables)
        {
            Text = "Data Preview";
            StartPosition = FormStartPosition.CenterParent;
            Width = 1050;
            Height = 680;
            MinimumSize = new Size(760, 420);
            MinimizeBox = false;
            ShowIcon = false;

            foreach (var table in tables)
            {
                var rows = new BindingList<PreviewRowViewModel>(table.Records.Select(r => new PreviewRowViewModel
                {
                    Included = true,
                    Id = r.Id,
                    SourceName = r.DisplayName,
                    TargetName = r.State == RecordPreviewState.Update ? (r.TargetDisplayName ?? r.Id.ToString()) : "(not in Target)",
                    State = r.State.ToString()
                }).ToList());
                _rowsByTable[table.LogicalName] = rows;
                _truncatedByTable[table.LogicalName] = table.Truncated;
                _totalByTable[table.LogicalName] = table.SourceRecordCount;
            }

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10) };
            var okButton = new Button { Text = "Apply Selection", DialogResult = DialogResult.OK, Width = 120, Height = 30 };
            var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
            okButton.Click += (s, e) => ExcludedRecordIds = BuildExclusionSet();
            buttons.Controls.Add(okButton);
            buttons.Controls.Add(cancelButton);

            var tableBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(10, 10, 10, 4) };
            tableBar.Controls.Add(new Label { Text = "Table:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _tableCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 520 };
            foreach (var table in tables)
                _tableCombo.Items.Add(new TableComboEntry(table));
            _tableCombo.SelectedIndexChanged += (s, e) => ShowSelectedTable();
            tableBar.Controls.Add(_tableCombo);

            var legend = new Label
            {
                Text = "🟩 New (not yet in Target)   ⬜ Update, no change   🟥 Update, name will change   ☐ Uncheck to exclude from Execute",
                AutoSize = false,
                Height = 34,
                Dock = DockStyle.Top,
                Padding = new Padding(10, 0, 10, 4)
            };

            var bulkBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Height = 40, Padding = new Padding(10, 0, 10, 4) };
            bulkBar.Controls.Add(MakeSmallButton("Select All", (s, e) => SetIncludedForCurrentTable(_ => true)));
            bulkBar.Controls.Add(MakeSmallButton("Select None", (s, e) => SetIncludedForCurrentTable(_ => false)));
            bulkBar.Controls.Add(MakeSmallButton("New Only", (s, e) => SetIncludedForCurrentTable(r => r.State == nameof(RecordPreviewState.New))));
            bulkBar.Controls.Add(MakeSmallButton("Updates Only", (s, e) => SetIncludedForCurrentTable(r => r.State == nameof(RecordPreviewState.Update))));
            bulkBar.Controls.Add(MakeSmallButton("Skip Unchanged", (s, e) => SetIncludedForCurrentTable(r => !(r.State == nameof(RecordPreviewState.Update) && !r.NameDiffers))));

            _truncatedNotice = new Label
            {
                Dock = DockStyle.Top,
                ForeColor = Color.DarkOrange,
                AutoSize = false,
                Height = 30,
                Visible = false,
                Padding = new Padding(10, 0, 10, 4)
            };

            // --- Fill content: a resizable split container, source pane left / target pane right
            // (SplitContainer, not a percent-column TableLayoutPanel, so the divider is also
            // draggable — one step closer to an actual FTP client's dual-pane feel).
            _sourceGrid = BuildGrid(includeCheckbox: true);
            _sourceGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", HeaderText = "Id", DataPropertyName = "Id", Width = 220, ReadOnly = true });
            _sourceGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SourceName", HeaderText = "Name", DataPropertyName = "SourceName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _sourceGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Status", DataPropertyName = "State", Width = 90, ReadOnly = true });

            _targetGrid = BuildGrid(includeCheckbox: false);
            _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", HeaderText = "Id", DataPropertyName = "Id", Width = 220, ReadOnly = true });
            _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "TargetName", HeaderText = "Name", DataPropertyName = "TargetName", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
            _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "Status", DataPropertyName = "State", Width = 90, ReadOnly = true });

            WireRowColoring(_sourceGrid);
            WireRowColoring(_targetGrid);
            WireScrollSync(_sourceGrid, _targetGrid);
            WireScrollSync(_targetGrid, _sourceGrid);
            WireSelectionSync(_sourceGrid, _targetGrid);
            WireSelectionSync(_targetGrid, _sourceGrid);

            // Checkbox cells don't commit their new value until the cell loses focus — without
            // this, clicking a checkbox once and then a bulk button (or closing the dialog)
            // would silently keep the old value.
            _sourceGrid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (_sourceGrid.IsCurrentCellDirty) _sourceGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };

            // Deliberately NOT setting Panel1MinSize/Panel2MinSize here: assigning either one
            // immediately validates/adjusts SplitterDistance against the control's CURRENT Width
            // (Fixed: this was the real, actual crash — "SplitterDistance must be between
            // Panel1MinSize and Width - Panel2MinSize" was thrown from set_Panel2MinSize itself,
            // not from the SplitterDistance line — because at construction time, before the form
            // has laid out its docked children, Width is still a tiny pre-layout default, smaller
            // than Panel1MinSize + Panel2MinSize). Both MinSize and SplitterDistance are set
            // together below, once Load fires and Width is the real, final one.
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 6
            };
            split.Panel1.Controls.Add(BuildPane("SOURCE", _sourceGrid));
            split.Panel2.Controls.Add(BuildPane("TARGET", _targetGrid));

            // Structural fix, not another Dock-ordering patch: this dialog's outer chrome had
            // THREE separate real bugs traced back to relying on plain Dock=Top/Fill stacking and
            // its "last-added-wins-the-true-edge" ordering rule for same-Dock siblings — a rule
            // that's easy to get subtly wrong (real feedback, most recently: rows rendering
            // correctly but visually occluded behind the top toolbar by roughly the toolbar's own
            // combined height). A TableLayoutPanel with one row per bar — the SAME pattern already
            // working in PluginControl.Layout.cs's BuildTablesTab — assigns each control to an
            // EXPLICIT (row, column) cell. There is no Z-order/add-order ambiguity to get wrong,
            // and the Fill row (the SplitContainer) can never overlap the AutoSize rows above it.
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // tableBar
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // legend
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // bulkBar
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // _truncatedNotice
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // split (the grids)
            root.Controls.Add(tableBar, 0, 0);
            root.Controls.Add(legend, 0, 1);
            root.Controls.Add(bulkBar, 0, 2);
            root.Controls.Add(_truncatedNotice, 0, 3);
            root.Controls.Add(split, 0, 4);

            Controls.Add(buttons);
            Controls.Add(root);

            Load += (s, e) =>
            {
                try
                {
                    split.SplitterDistance = split.Width / 2;
                    split.Panel1MinSize = 150;
                    split.Panel2MinSize = 150;
                }
                catch (InvalidOperationException) { /* keep SplitContainer's own defaults if something is still off */ }

                // Moved here from the constructor (same lesson as the SplitterDistance fix above):
                // selecting the first table — which synchronously binds the grids' DataSource via
                // ShowSelectedTable() — before the Form's first real layout pass left the entire
                // grid area blank (real feedback: header showed "2 total, 2 new" but no rows, no
                // SOURCE/TARGET headers, nothing visible below the top bars). Doing it after Load
                // fires means the SplitContainer and grids already have their real, final size.
                if (_tableCombo.Items.Count > 0)
                    _tableCombo.SelectedIndex = 0;
            };

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        /// <summary>A header label (Dock.Top, added first) over a grid (Dock.Fill, added second) —
        /// the header reliably claims a thin strip and the grid fills everything else beneath it.</summary>
        private static Panel BuildPane(string title, DataGridView grid)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var header = new Label
            {
                Text = title,
                Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold),
                AutoSize = false,
                Height = 24,
                Dock = DockStyle.Top,
                Padding = new Padding(4, 4, 0, 0)
            };
            panel.Controls.Add(header);
            panel.Controls.Add(grid);
            return panel;
        }

        private static DataGridView BuildGrid(bool includeCheckbox)
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                MinimumSize = new Size(0, 200)
            };
            if (includeCheckbox)
                grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Included", HeaderText = "", DataPropertyName = "Included", Width = 36 });
            return grid;
        }

        private static Button MakeSmallButton(string text, EventHandler onClick)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
            button.Click += onClick;
            return button;
        }

        private void ShowSelectedTable()
        {
            if (!(_tableCombo.SelectedItem is TableComboEntry entry))
            {
                _sourceGrid.DataSource = null;
                _targetGrid.DataSource = null;
                _truncatedNotice.Visible = false;
                return;
            }

            var rows = _rowsByTable[entry.Table.LogicalName];
            _sourceGrid.DataSource = rows;
            _targetGrid.DataSource = rows;

            // Switching tables must always start scrolled to the top. Without this, a grid's
            // scroll position / current-row selection from a PREVIOUS (often larger) table can
            // carry over onto a new, smaller table's binding, leaving its first several rows
            // scrolled out of view with no visible scrollbar hint that anything is hidden above
            // (real feedback: an 8-row table only showed its last 4 rows after switching to it
            // from a bigger one). Wrapped in try/catch the same way WireScrollSync already does
            // for FirstDisplayedScrollingRowIndex — DataGridView can throw if asked to scroll to
            // a row index before it's actually laid out its rows yet.
            _sourceGrid.ClearSelection();
            _targetGrid.ClearSelection();
            try
            {
                if (_sourceGrid.RowCount > 0)
                {
                    _sourceGrid.FirstDisplayedScrollingRowIndex = 0;
                    _targetGrid.FirstDisplayedScrollingRowIndex = 0;
                }
            }
            catch (InvalidOperationException) { /* harmless — grid not tall enough to scroll into view yet */ }

            var truncated = _truncatedByTable[entry.Table.LogicalName];
            _truncatedNotice.Visible = truncated;
            _truncatedNotice.Text = truncated
                ? $"Showing the first {rows.Count} of {_totalByTable[entry.Table.LogicalName]} records — " +
                  "records beyond this list are not shown here and will still be included as-is."
                : string.Empty;
        }

        private void SetIncludedForCurrentTable(Func<PreviewRowViewModel, bool> included)
        {
            if (!(_tableCombo.SelectedItem is TableComboEntry entry)) return;
            foreach (var row in _rowsByTable[entry.Table.LogicalName])
                row.Included = included(row);
            _sourceGrid.Refresh();
        }

        private static void WireRowColoring(DataGridView grid)
        {
            grid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.RowIndex >= grid.Rows.Count) return;
                if (!(grid.Rows[e.RowIndex].DataBoundItem is PreviewRowViewModel row)) return;

                Color color;
                if (row.State == nameof(RecordPreviewState.New)) color = NewColor;
                else if (row.NameDiffers) color = UpdateDiffersColor;
                else color = UpdateSameColor;

                // The Target pane additionally dims a New row's placeholder text, since there's
                // genuinely nothing there yet — distinct from "there's a real record and it just
                // happens to match".
                if (row.State == nameof(RecordPreviewState.New) && grid.Columns[e.ColumnIndex].Name == "TargetName")
                    color = MissingColor;

                grid.Rows[e.RowIndex].DefaultCellStyle.BackColor = color;
            };
        }

        /// <summary>Keeps both panes scrolled to the same row, so "see both places at the same
        /// time" (real feedback, referencing FileZilla's dual-pane view) actually holds even for
        /// tables with more rows than fit on screen.</summary>
        private void WireScrollSync(DataGridView source, DataGridView target)
        {
            source.Scroll += (s, e) =>
            {
                if (_syncing) return;
                _syncing = true;
                try
                {
                    if (target.RowCount > 0 && source.FirstDisplayedScrollingRowIndex >= 0)
                        target.FirstDisplayedScrollingRowIndex = Math.Min(source.FirstDisplayedScrollingRowIndex, target.RowCount - 1);
                }
                catch (InvalidOperationException) { /* row not tall enough to scroll into view yet — harmless, next scroll tick corrects it */ }
                finally { _syncing = false; }
            };
        }

        private void WireSelectionSync(DataGridView source, DataGridView target)
        {
            source.SelectionChanged += (s, e) =>
            {
                if (_syncing || source.CurrentRow == null) return;
                _syncing = true;
                try
                {
                    var rowIndex = source.CurrentRow.Index;
                    if (rowIndex >= 0 && rowIndex < target.RowCount)
                    {
                        target.ClearSelection();
                        target.Rows[rowIndex].Selected = true;
                    }
                }
                finally { _syncing = false; }
            };
        }

        private IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> BuildExclusionSet()
        {
            var result = new Dictionary<string, IReadOnlyCollection<Guid>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _rowsByTable)
            {
                var excluded = kvp.Value.Where(r => !r.Included).Select(r => r.Id).ToList();
                if (excluded.Count > 0)
                    result[kvp.Key] = excluded;
            }
            return result;
        }
    }
}
