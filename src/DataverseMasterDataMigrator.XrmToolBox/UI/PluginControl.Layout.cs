using System.Drawing;
using System.Windows.Forms;
using XrmToolBox.Extensibility;

namespace DataverseMasterDataMigrator.XrmToolBox.UI
{
    public partial class PluginControl
    {
        /// <summary>
        /// Builds the four-section layout (Connections / Profiles / Tables / Migration) entirely
        /// in code. This intentionally avoids a Designer-generated .designer.cs file, which
        /// can't be meaningfully authored or previewed outside Visual Studio's Designer surface.
        /// Functionally complete; treat this as a first pass to reshape visually in the
        /// Designer once it compiles in your environment (the WinForms/XrmToolBox pattern
        /// followed here mirrors MetadataDataverseDocument's UI, e.g. its own use of
        /// TableLayoutPanel and a left-panel/right-panel split).
        /// </summary>
        private void BuildLayout()
        {
            Dock = DockStyle.Fill;

            // A persistent top bar (not tied to any one tab) instead of relying on XrmToolBox's
            // own host-level "About Plugin" menu — real feedback: that host menu is easy to miss,
            // and the reference plugin (Metadata Dataverse Document) puts About as a plain button
            // in its own toolbar instead.
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var topBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(5)
            };
            topBar.Controls.Add(MakeButton("About", (s, e) => ShowAboutDialog()));
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            topBar.Controls.Add(new Label
            {
                Text = $"v{version.Major}.{version.Minor}.{version.Build}",
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(0, 10, 10, 0)
            });

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildConnectionsTab());
            _tabs.TabPages.Add(BuildProfilesTab());
            _tabs.TabPages.Add(BuildTablesTab());
            _tabs.TabPages.Add(BuildMigrationTab());

            root.Controls.Add(topBar, 0, 0);
            root.Controls.Add(_tabs, 0, 1);
            Controls.Add(root);
        }

        private TabPage BuildConnectionsTab()
        {
            var page = new TabPage("Connections");
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(20) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));

            var sourceCard = BuildConnectionCard(
                "SOURCE", out _sourceLabel, out _sourceStatusDot, out _btnChangeSource, "Change Source Connection");
            // Triggers XrmToolBox's own connection picker for this plugin's primary connection —
            // the same mechanism the host uses when a tool first requires a connection, rather
            // than a message box telling the user to go find a toolbar button themselves.
            _btnChangeSource.Click += (s, e) => RaiseRequestConnectionEvent(new RequestConnectionEventArgs());

            var targetCard = BuildConnectionCard(
                "TARGET", out _targetLabel, out _targetStatusDot, out _btnChangeTarget, "Select Target Connection");
            _btnChangeTarget.Click += (s, e) => AddAdditionalOrganization();

            var arrow = new Label
            {
                Text = "→",
                Font = new Font(FontFamily.GenericSansSerif, 28, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver
            };

            root.Controls.Add(sourceCard, 0, 0);
            root.Controls.Add(arrow, 1, 0);
            root.Controls.Add(targetCard, 2, 0);

            page.Controls.Add(root);
            return page;
        }

        /// <summary>Builds one Source/Target "card": a titled panel with a status dot, the
        /// connection's details, and one action button — used to give Connections a side-by-side
        /// layout instead of a single thin row per connection.</summary>
        private static Panel BuildConnectionCard(
            string title, out Label detailLabel, out Panel statusDot, out Button actionButton, string buttonText)
        {
            var card = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(15),
                BackColor = Color.White
            };

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var dot = new Panel { Size = new Size(12, 12), BackColor = Color.Silver, Margin = new Padding(2, 6, 0, 0) };
            var titleLabel = new Label
            {
                Text = title,
                Font = new Font(FontFamily.GenericSansSerif, 12, FontStyle.Bold),
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var detail = new Label
            {
                Text = "Not connected",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(0, 10, 0, 10),
                ForeColor = Color.DimGray
            };

            var button = new Button { Text = buttonText, Dock = DockStyle.Fill, Height = 32 };

            layout.Controls.Add(dot, 0, 0);
            layout.Controls.Add(titleLabel, 1, 0);
            layout.Controls.Add(detail, 0, 1);
            layout.SetColumnSpan(detail, 2);
            layout.Controls.Add(button, 0, 2);
            layout.SetColumnSpan(button, 2);

            card.Controls.Add(layout);

            detailLabel = detail;
            statusDot = dot;
            actionButton = button;
            return card;
        }

        private TabPage BuildProfilesTab()
        {
            var page = new TabPage("Profiles");
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10) };
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _profilesCombo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
            _profilesCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_profilesCombo.SelectedItem is ProfileListItem item && item.Profile != null)
                    TrySwitchProfile(item);
            };

            var namePanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 30 };
            namePanel.Controls.Add(new Label { Text = "Name:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _profileNameBox = new TextBox { Dock = DockStyle.Fill };
            namePanel.Controls.Add(_profileNameBox, 1, 0);

            var descPanel = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = 30 };
            descPanel.Controls.Add(new Label { Text = "Description:", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            _profileDescriptionBox = new TextBox { Dock = DockStyle.Fill };
            descPanel.Controls.Add(_profileDescriptionBox, 1, 0);

            _skipSilentlyOptionalLookupsCheckBox = new CheckBox
            {
                Text = "Skip unresolved optional lookups silently (don't write them, don't warn)",
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 4, 0, 4)
            };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            _btnNewProfile = MakeButton("New Profile", OnNewProfile);
            _btnSaveProfile = MakeButton("Save", OnSaveProfile);
            _btnSaveAsProfile = MakeButton("Save As", (s, e) =>
            {
                if (_currentProfile == null) OnNewProfile(this, System.EventArgs.Empty);
                _currentProfile.Id = System.Guid.NewGuid();
                OnSaveProfile(s, e);
            });
            _btnDeleteProfile = MakeButton("Delete", OnDeleteProfile);
            var btnReload = MakeButton("Reload", (s, e) => ReloadProfilesList());
            _btnOpenProfilesFolder = MakeButton("Open Profiles Folder", OnOpenProfilesFolder);
            var btnEditTables = MakeButton("Edit Tables →", (s, e) => SwitchToTablesTab());
            buttons.Controls.AddRange(new Control[] { _btnNewProfile, _btnSaveProfile, _btnSaveAsProfile, _btnDeleteProfile, btnReload, _btnOpenProfilesFolder, btnEditTables });

            // Real feedback: selecting a profile showed its name/description but gave no clue
            // what tables it actually contains — had to switch to Tables and cross-check the
            // checkboxes by eye.
            _profileTablesHeader = new Label
            {
                Text = "Tables in this profile: 0",
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 10, 0, 4),
                Font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold)
            };
            _profileTablesList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

            layout.Controls.Add(_profilesCombo);
            layout.Controls.Add(namePanel);
            layout.Controls.Add(descPanel);
            layout.Controls.Add(_skipSilentlyOptionalLookupsCheckBox);
            layout.Controls.Add(buttons);
            layout.Controls.Add(_profileTablesHeader);
            layout.Controls.Add(_profileTablesList);

            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildTablesTab()
        {
            var page = new TabPage("Tables");
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1, Padding = new Padding(10) };

            // Always-visible "which profile am I editing" bar — this tab is now the only place
            // table membership is edited, so it must never be ambiguous which profile the
            // checkboxes below belong to (real feedback: profiles were created in one tab, tables
            // picked in another, easy to lose track of which profile you were actually editing).
            var profileBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            profileBar.Controls.Add(new System.Windows.Forms.Label { Text = "Profile:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _tablesProfileCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, Margin = new Padding(0, 2, 10, 0) };
            _tablesProfileCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_tablesProfileCombo.SelectedItem is ProfileListItem item && item.Profile != null)
                    TrySwitchProfile(item);
            };
            profileBar.Controls.Add(_tablesProfileCombo);
            profileBar.Controls.Add(MakeButton("New Profile", OnNewProfile));
            _btnSaveFromTables = MakeButton("Save Changes", (s, e) => SaveCurrentProfile());
            profileBar.Controls.Add(_btnSaveFromTables);
            _profileEditingLabel = new System.Windows.Forms.Label
            {
                Text = "No profile loaded",
                AutoSize = true,
                Margin = new Padding(15, 6, 0, 0),
                Font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold)
            };
            profileBar.Controls.Add(_profileEditingLabel);

            var topBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            _btnLoadTables = MakeButton("Load Tables from Source", OnLoadTables);
            _tableSearchBox = new TextBox { Width = 250 };
            _tableSearchBox.TextChanged += (s, e) => ApplyTableFilter();
            topBar.Controls.Add(_btnLoadTables);
            topBar.Controls.Add(_tableSearchBox);

            var filterBar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            _filterAll = new RadioButton { Text = "All", Checked = true };
            _filterCustom = new RadioButton { Text = "Custom" };
            _filterStandard = new RadioButton { Text = "Standard" };
            _filterSelected = new RadioButton { Text = "Selected" };
            foreach (var rb in new[] { _filterAll, _filterCustom, _filterStandard, _filterSelected })
            {
                rb.CheckedChanged += (s, e) => ApplyTableFilter();
                filterBar.Controls.Add(rb);
            }
            _btnSelectAllVisible = MakeButton("Select All Visible", OnSelectAllVisible);
            _btnClearVisible = MakeButton("Clear Visible", OnClearVisible);
            _btnClearAll = MakeButton("Clear All", OnClearAll);
            _btnFindRelatedTables = MakeButton("Find Related Tables", OnFindRelatedTables);
            filterBar.Controls.Add(_btnSelectAllVisible);
            filterBar.Controls.Add(_btnClearVisible);
            filterBar.Controls.Add(_btnClearAll);
            filterBar.Controls.Add(_btnFindRelatedTables);

            // Always-visible count, independent of which quick filter is active — with a couple
            // thousand tables in a real tenant, "click Selected to see what you picked" wasn't
            // discoverable enough on its own (real feedback).
            _selectedCountLabel = new System.Windows.Forms.Label
            {
                Text = "0 table(s) selected",
                AutoSize = true,
                Margin = new Padding(15, 6, 0, 0),
                Font = new Font(FontFamily.GenericSansSerif, 9, FontStyle.Bold)
            };
            filterBar.Controls.Add(_selectedCountLabel);

            _tablesList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
            _tablesList.ItemCheck += (s, e) =>
            {
                if (!(_tablesList.Items[e.Index] is TableListItem item)) return;
                if (e.NewValue == CheckState.Checked) _checkedTableNames.Add(item.Table.LogicalName);
                else _checkedTableNames.Remove(item.Table.LogicalName);
                RefreshSelectedCountLabel();
            };

            root.Controls.Add(profileBar);
            root.Controls.Add(topBar);
            root.Controls.Add(filterBar);
            root.Controls.Add(_tablesList);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            page.Controls.Add(root);
            return page;
        }

        private TabPage BuildMigrationTab()
        {
            var page = new TabPage("Migration");
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(10) };

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            _btnPreflight = MakeButton("Analyze / Preflight", OnPreflight);
            _btnPreviewData = MakeButton("Preview Data", OnPreviewData);
            _btnPreviewData.Enabled = false;
            _btnCompareStructure = MakeButton("Compare Structure", OnCompareStructure);
            _btnExecute = MakeButton("Execute", OnExecute);
            _btnExecute.Enabled = false;
            _btnCancel = MakeButton("Cancel", OnCancel);
            _btnCancel.Enabled = false;
            _btnRetryFailed = MakeButton("Retry Failed", OnRetryFailed);
            _btnViewLog = MakeButton("View Log", OnViewLog);
            _btnClearLog = MakeButton("Clear Log", OnClearLog);
            _preflightStatusDot = new Panel { Size = new Size(14, 14), BackColor = Color.Silver, Margin = new Padding(24, 9, 4, 0) };
            _preflightStatusLabel = new Label { Text = "Sin analizar", AutoSize = true, Margin = new Padding(2, 11, 0, 0), ForeColor = Color.DimGray };
            buttons.Controls.AddRange(new Control[] { _btnPreflight, _btnPreviewData, _btnCompareStructure, _btnExecute, _btnCancel, _btnRetryFailed, _btnViewLog, _btnClearLog, _preflightStatusDot, _preflightStatusLabel });

            _logBox = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font(FontFamily.GenericMonospace, 9)
            };

            root.Controls.Add(buttons);
            root.Controls.Add(_logBox);
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            page.Controls.Add(root);
            return page;
        }

        private static Button MakeButton(string text, System.EventHandler onClick)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
            button.Click += onClick;
            return button;
        }
    }
}
