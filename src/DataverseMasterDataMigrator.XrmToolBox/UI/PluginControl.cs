using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using DataverseMasterDataMigrator.Core.Comparison;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;
using DataverseMasterDataMigrator.Core.Profiles;
using DataverseMasterDataMigrator.Core.Validation;
using DataverseMasterDataMigrator.XrmToolBox.Services;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace DataverseMasterDataMigrator.XrmToolBox.UI
{
    /// <summary>
    /// NOTE ON XrmToolBox.Extensibility API SURFACE: MultipleConnectionsPluginControlBase's
    /// exact members (AdditionalConnectionsLabels, UpdateConnection overload, etc.) can differ
    /// slightly between XrmToolBox.Extensibility versions. This was written against the pattern
    /// documented for that base class as of early-2026 releases; verify member names against
    /// the XrmToolBox.Extensibility.dll already sitting in
    /// MetadataDataverseDocument-Source/lib/ when this fails to compile, and adjust signatures
    /// to match — the business logic underneath does not depend on the exact override shape.
    /// </summary>
    public partial class PluginControl : MultipleConnectionsPluginControlBase, IAboutPlugin
    {
        private const string TargetConnectionName = "AdditionalOrganization";

        private Settings _settings;
        private MigrationProfileRepository _profileRepository;
        private MigrationProfile _currentProfile;

        private IOrganizationService _sourceService;
        private IOrganizationService _targetService;
        private DataverseMetadataProviderAdapter _sourceMetadata;
        private DataverseMetadataProviderAdapter _targetMetadata;
        private DataverseRecordServiceAdapter _sourceRecords;
        private DataverseRecordServiceAdapter _targetRecords;

        private IReadOnlyList<TableSummary> _sourceTables = new List<TableSummary>();

        /// <summary>
        /// Single source of truth for "which tables are selected" — independent of what's
        /// currently visible in <see cref="_tablesList"/>. A <see cref="CheckedListBox"/> has no
        /// per-item visibility flag, so real search/filtering means rebuilding its Items on every
        /// keystroke; without this set, that rebuild would silently drop the checked state of any
        /// table not currently matching the filter.
        /// </summary>
        private readonly HashSet<string> _checkedTableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource _currentOperationCts;
        private ExecutionManifestStore _manifestStore;
        private ExecutionManifest _lastManifest;

        /// <summary>Per-table record ids the user unchecked in the last Data Preview review —
        /// consumed by the next Execute, then left as-is until Preview Data is run again (which
        /// rebuilds it fresh, defaulting back to "everything included").</summary>
        private IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> _recordSelectionOverrides;

        // --- UI controls (built in code, see BuildLayout) -----------------------------------
        private TabControl _tabs;
        private System.Windows.Forms.Label _sourceLabel, _targetLabel;
        private Panel _sourceStatusDot, _targetStatusDot;
        private Panel _preflightStatusDot;
        private System.Windows.Forms.Label _preflightStatusLabel;
        private Button _btnChangeSource, _btnChangeTarget;

        private ComboBox _profilesCombo;
        private Button _btnNewProfile, _btnSaveProfile, _btnSaveAsProfile, _btnDeleteProfile, _btnOpenProfilesFolder;
        private TextBox _profileNameBox, _profileDescriptionBox;
        private CheckBox _skipSilentlyOptionalLookupsCheckBox;
        private System.Windows.Forms.Label _profileTablesHeader;
        private ListBox _profileTablesList;

        private TextBox _tableSearchBox;
        private CheckedListBox _tablesList;
        private Button _btnSelectAllVisible, _btnClearVisible, _btnClearAll, _btnLoadTables, _btnFindRelatedTables;
        private ComboBox _tablesProfileCombo;
        private System.Windows.Forms.Label _profileEditingLabel;
        private Button _btnSaveFromTables;
        private bool _syncingProfileCombos;
        private RadioButton _filterAll, _filterCustom, _filterStandard, _filterSelected;
        private System.Windows.Forms.Label _selectedCountLabel;

        private Button _btnPreflight, _btnPreviewData, _btnCompareStructure, _btnExecute, _btnCancel, _btnRetryFailed, _btnViewLog, _btnClearLog;
        private TextBox _logBox;
        private PreflightResult _lastPreflight;

        public PluginControl()
        {
            BuildLayout();
        }

        /// <summary>
        /// Required override: the base class raises this whenever
        /// <c>AdditionalConnectionDetails</c> changes (i.e. whenever a Target connection is
        /// added/removed via <see cref="MultipleConnectionsPluginControlBase.AddAdditionalOrganization"/>).
        /// The actual service/label wiring happens in <see cref="UpdateConnection"/> once
        /// XrmToolBox resolves the connection into a real <see cref="IOrganizationService"/>;
        /// this hook is left as a no-op since there's nothing additional to react to at the
        /// "detail added to the list" stage.
        /// </summary>
        protected override void ConnectionDetailsUpdated(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
        }

        public string HelpUrl => "https://github.com/";

        public void ShowAboutDialog()
        {
            using (var dialog = new AboutForm())
            {
                dialog.ShowDialog(this);
            }
        }

        // --- Connection lifecycle -----------------------------------------------------------

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            SettingsManager.Instance.TryLoad(GetType(), out _settings);
            _settings = _settings ?? new Settings();

            var profilesFolder = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(typeof(PluginControl).Assembly.Location) ?? ".",
                "Profiles");
            // Prefer XrmToolBox's own AppData root when available; fall back defensively so a
            // corrupt path never crashes the plugin at load time (section 6 requirement spirit
            // extended to path resolution).
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                profilesFolder = System.IO.Path.Combine(appData, "MscrmTools", "XrmToolBox", "DataverseMasterDataMigrator", "Profiles");
            }
            catch { /* keep the fallback computed above */ }

            _profileRepository = new MigrationProfileRepository(profilesFolder);

            var executionsFolder = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(profilesFolder) ?? ".", "Executions");
            _manifestStore = new ExecutionManifestStore(executionsFolder);

            ReloadProfilesList();
            UpdateExecuteButtonState();
        }

        /// <summary>
        /// Called by XrmToolBox once for the primary connection (Source) and once for the
        /// additional connection requested via <see cref="MultipleConnectionsPluginControlBase.AddAdditionalOrganization"/>
        /// (Target) — the base class identifies the latter case by <paramref name="actionName"/>
        /// equal to <see cref="TargetConnectionName"/> ("AdditionalOrganization").
        /// </summary>
        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            bool isTarget = string.Equals(actionName, TargetConnectionName, StringComparison.OrdinalIgnoreCase);

            if (isTarget)
            {
                _targetService = newService;
                _targetMetadata = newService != null ? new DataverseMetadataProviderAdapter(newService) : null;
                _targetRecords = newService != null ? new DataverseRecordServiceAdapter(newService) : null;
                _targetLabel.Text = FormatConnectionLabel(detail);
                _targetStatusDot.BackColor = newService != null ? Color.MediumSeaGreen : Color.Silver;
            }
            else
            {
                _sourceService = newService;
                _sourceMetadata = newService != null ? new DataverseMetadataProviderAdapter(newService) : null;
                _sourceRecords = newService != null ? new DataverseRecordServiceAdapter(newService) : null;
                _sourceLabel.Text = FormatConnectionLabel(detail);
                _sourceStatusDot.BackColor = newService != null ? Color.MediumSeaGreen : Color.Silver;
            }

            // Keeps the base class's own AdditionalConnectionDetails bookkeeping consistent
            // (it's what tracks the Target ConnectionDetail internally for actionName ==
            // "AdditionalOrganization"); harmless no-op for the Source/primary case.
            base.UpdateConnection(newService, detail, actionName, parameter);

            UpdateExecuteButtonState();
        }

        private static string FormatConnectionLabel(ConnectionDetail detail)
        {
            if (detail == null) return "Not connected";
            return $"{detail.ConnectionName}\n{detail.WebApplicationUrl}\n{detail.Organization}";
        }

        // --- Profiles -------------------------------------------------------------------------

        private void ReloadProfilesList()
        {
            var loaded = _profileRepository.LoadAll();
            _profilesCombo.Items.Clear();
            _tablesProfileCombo.Items.Clear();

            foreach (var item in loaded)
            {
                var label = item.IsValid ? item.Profile.Name : $"⚠ {item.FileName} (could not load: {item.Error})";
                var listItem = new ProfileListItem { Label = label, Profile = item.IsValid ? item.Profile : null };
                _profilesCombo.Items.Add(listItem);
                _tablesProfileCombo.Items.Add(listItem);
            }

            if (_settings.LastProfileId != null)
            {
                var match = _profilesCombo.Items.Cast<ProfileListItem>()
                    .FirstOrDefault(p => p.Profile != null && p.Profile.Id.ToString() == _settings.LastProfileId);
                if (match != null)
                {
                    LoadProfileIntoUi(match.Profile);
                    SyncProfileCombosToCurrent();
                }
            }
        }

        private void LoadProfileIntoUi(MigrationProfile profile)
        {
            _currentProfile = profile;
            _profileNameBox.Text = profile?.Name ?? string.Empty;
            _profileDescriptionBox.Text = profile?.Description ?? string.Empty;
            _skipSilentlyOptionalLookupsCheckBox.Checked = profile?.Options?.OptionalLookupPolicy == LookupPolicy.SkipSilently;

            if (profile == null) return;

            _checkedTableNames.Clear();
            foreach (var e in profile.Entities.Where(x => x.Enabled))
                _checkedTableNames.Add(e.LogicalName);
            ApplyTableFilter();
            RefreshProfileTablesList();

            if (profile.Id != Guid.Empty)
            {
                _settings.LastProfileId = profile.Id.ToString();
                SettingsManager.Instance.Save(GetType(), _settings);
            }
        }

        /// <summary>Populates the Profiles tab's own table list from <see cref="_currentProfile"/>
        /// — otherwise selecting a profile shows its name/description but gives no clue what
        /// tables it actually contains without switching to Tables and cross-checking checkboxes
        /// by eye (real feedback).</summary>
        private void RefreshProfileTablesList()
        {
            _profileTablesList.Items.Clear();
            var entities = (_currentProfile?.Entities ?? new List<ProfileEntity>())
                .OrderBy(en => en.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var entity in entities)
                _profileTablesList.Items.Add($"{entity.DisplayName}  ({entity.LogicalName})");
            _profileTablesHeader.Text = $"Tables in this profile: {entities.Count}";
        }

        /// <summary>True when the checked tables in the Tables tab differ from what's actually
        /// persisted in <see cref="_currentProfile"/> — the single source of truth for "would
        /// switching profiles right now silently discard something".</summary>
        private bool HasUnsavedTableSelectionChanges()
        {
            if (_currentProfile == null) return _checkedTableNames.Count > 0;
            var saved = new HashSet<string>(
                _currentProfile.Entities.Where(x => x.Enabled).Select(x => x.LogicalName),
                StringComparer.OrdinalIgnoreCase);
            return !saved.SetEquals(_checkedTableNames);
        }

        /// <summary>Keeps the Tables tab's "which profile am I editing" bar in sync — called from
        /// every path that can change <see cref="_currentProfile"/> or <see cref="_checkedTableNames"/>
        /// via <see cref="RefreshSelectedCountLabel"/>.</summary>
        private void RefreshProfileEditingBar()
        {
            var name = _currentProfile?.Name;
            var dirty = HasUnsavedTableSelectionChanges();
            _profileEditingLabel.Text = string.IsNullOrEmpty(name)
                ? "No profile loaded — check tables below, then Save Changes"
                : $"Editing profile: {name}" + (dirty ? "  •  unsaved changes" : "");
            _profileEditingLabel.ForeColor = dirty ? Color.DarkOrange : Color.DimGray;
            _btnSaveFromTables.Enabled = _currentProfile != null && dirty;
        }

        /// <summary>Single entry point for switching the active profile from either combo (Tables
        /// tab or Profiles tab) — guards against silently discarding unsaved table-selection
        /// changes by asking first.</summary>
        private void TrySwitchProfile(ProfileListItem item)
        {
            if (_syncingProfileCombos) return;

            if (HasUnsavedTableSelectionChanges())
            {
                var result = MessageBox.Show(
                    "The current profile's table selection has unsaved changes. Discard them and switch profile?",
                    "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                {
                    SyncProfileCombosToCurrent();
                    return;
                }
            }

            LoadProfileIntoUi(item?.Profile);
            SyncProfileCombosToCurrent();
        }

        /// <summary>Re-selects the current profile in both combos without re-entering
        /// <see cref="TrySwitchProfile"/> (guarded by <see cref="_syncingProfileCombos"/>) — needed
        /// because a brand-new profile that hasn't been saved yet may not appear in either combo,
        /// in which case both are simply left with no selection.</summary>
        private void SyncProfileCombosToCurrent()
        {
            _syncingProfileCombos = true;
            try
            {
                SelectMatchingItem(_profilesCombo);
                SelectMatchingItem(_tablesProfileCombo);
            }
            finally { _syncingProfileCombos = false; }
        }

        private void SelectMatchingItem(ComboBox combo)
        {
            var match = _currentProfile == null
                ? null
                : combo.Items.Cast<ProfileListItem>().FirstOrDefault(p => p.Profile != null && p.Profile.Id == _currentProfile.Id);
            combo.SelectedItem = match;
        }

        private void OnNewProfile(object sender, EventArgs e)
        {
            _currentProfile = new MigrationProfile { Name = "New profile" };
            _profileNameBox.Text = _currentProfile.Name;
            _profileDescriptionBox.Text = string.Empty;
            _skipSilentlyOptionalLookupsCheckBox.Checked = false;
            _checkedTableNames.Clear();
            _profilesCombo.SelectedIndex = -1;
            _tablesProfileCombo.SelectedIndex = -1;
            ApplyTableFilter();
            RefreshProfileTablesList();
        }

        /// <summary>Shared save logic used by both the Profiles tab's "Save" button and the Tables
        /// tab's "Save Changes" button — kept as a single method so the two surfaces can never
        /// drift apart. Returns false (and shows why) when the profile fails structural validation.</summary>
        private bool SaveCurrentProfile()
        {
            if (_currentProfile == null) OnNewProfile(this, EventArgs.Empty);

            _currentProfile.Name = string.IsNullOrWhiteSpace(_profileNameBox.Text) ? _currentProfile.Name : _profileNameBox.Text;
            _currentProfile.Description = _profileDescriptionBox.Text;
            _currentProfile.Options.OptionalLookupPolicy = _skipSilentlyOptionalLookupsCheckBox.Checked
                ? LookupPolicy.SkipSilently
                : LookupPolicy.WarnAndContinue;

            SyncProfileEntitiesFromTableSelection();
            RefreshProfileTablesList();

            var structuralErrors = _currentProfile.ValidateStructure();
            if (structuralErrors.Count > 0)
            {
                MessageBox.Show("Cannot save this profile:\n" + string.Join("\n", structuralErrors),
                    "Invalid profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            _profileRepository.Save(_currentProfile);

            // Make sure ReloadProfilesList's "restore last profile" branch re-selects THIS
            // profile, not whatever was loaded before — otherwise saving a brand-new profile
            // (created via New Profile, never previously loaded through a combo) would silently
            // vanish from the UI right after saving it.
            _settings.LastProfileId = _currentProfile.Id.ToString();
            SettingsManager.Instance.Save(GetType(), _settings);

            ReloadProfilesList();
            RefreshProfileEditingBar();
            AppendLog($"Profile '{_currentProfile.Name}' saved.");
            return true;
        }

        private void OnSaveProfile(object sender, EventArgs e)
        {
            if (SaveCurrentProfile())
                SwitchToMigrationTab();
        }

        private void SyncProfileEntitiesFromTableSelection()
        {
            var existingByName = _currentProfile.Entities.ToDictionary(e => e.LogicalName, StringComparer.OrdinalIgnoreCase);
            var sourceByName = _sourceTables.ToDictionary(t => t.LogicalName, StringComparer.OrdinalIgnoreCase);

            // Reads from _checkedTableNames (not _tablesList.CheckedItems) so a table selected
            // while a different search/filter was active — and therefore not currently visible
            // in the list — is still saved into the profile.
            string DisplayNameFor(string logicalName) =>
                sourceByName.TryGetValue(logicalName, out var t) ? t.DisplayName
                : existingByName.TryGetValue(logicalName, out var e) ? e.DisplayName
                : logicalName;

            var newList = new List<ProfileEntity>();
            int order = 10;

            foreach (var logicalName in _checkedTableNames.OrderBy(DisplayNameFor, StringComparer.OrdinalIgnoreCase))
            {
                if (existingByName.TryGetValue(logicalName, out var existing))
                {
                    existing.Enabled = true;
                    existing.DisplayName = DisplayNameFor(logicalName);
                    newList.Add(existing);
                }
                else
                {
                    newList.Add(new ProfileEntity
                    {
                        LogicalName = logicalName,
                        DisplayName = DisplayNameFor(logicalName),
                        Enabled = true,
                        PreferredOrder = order
                    });
                }
                order += 10;
            }

            _currentProfile.Entities = newList;
        }

        private void OnDeleteProfile(object sender, EventArgs e)
        {
            if (_currentProfile == null || _currentProfile.Id == Guid.Empty) return;

            if (MessageBox.Show($"Delete profile '{_currentProfile.Name}'?", "Confirm",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _profileRepository.Delete(_currentProfile.Id);
            _currentProfile = null;
            _profileNameBox.Text = string.Empty;
            _profileDescriptionBox.Text = string.Empty;
            _skipSilentlyOptionalLookupsCheckBox.Checked = false;
            _checkedTableNames.Clear();
            ApplyTableFilter();
            RefreshProfileTablesList();
            ReloadProfilesList();
        }

        private void OnOpenProfilesFolder(object sender, EventArgs e)
        {
            _profileRepository.EnsureFoldersExist();
            System.Diagnostics.Process.Start("explorer.exe", _profileRepository.ProfilesFolder);
        }

        // --- Tables ---------------------------------------------------------------------------

        private void OnLoadTables(object sender, EventArgs e)
        {
            if (_sourceMetadata == null)
            {
                MessageBox.Show("Connect to a Source environment first.", "No connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Loading tables from Source...",
                Work = (worker, args) =>
                {
                    args.Result = _sourceMetadata.ListTablesAsync(CancellationToken.None).GetAwaiter().GetResult();
                },
                PostWorkCallBack = args =>
                {
                    if (args.Error != null)
                    {
                        MessageBox.Show("Could not load tables: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    _sourceTables = (IReadOnlyList<TableSummary>)args.Result;
                    PopulateTablesList(_sourceTables);
                    AppendLog($"Loaded {_sourceTables.Count} tables from Source.");
                }
            });
        }

        private void PopulateTablesList(IReadOnlyList<TableSummary> tables)
        {
            _sourceTables = tables;
            ApplyTableFilter();
        }

        /// <summary>
        /// Rebuilds <see cref="_tablesList"/>'s visible items from <see cref="_sourceTables"/>,
        /// keeping only the ones matching the current search text and quick filter. Checked state
        /// for each visible item comes from <see cref="_checkedTableNames"/> — the persisted set
        /// — not the other way around, so re-filtering never loses a selection made under a
        /// different search/filter.
        /// </summary>
        private void ApplyTableFilter()
        {
            var search = (_tableSearchBox.Text ?? string.Empty).Trim();

            var filtered = _sourceTables.Where(t =>
            {
                bool matchesSearch = string.IsNullOrEmpty(search) ||
                    t.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.LogicalName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (t.SchemaName ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;

                bool matchesQuickFilter =
                    _filterAll.Checked ||
                    (_filterCustom.Checked && t.IsCustomEntity) ||
                    (_filterStandard.Checked && !t.IsCustomEntity) ||
                    (_filterSelected.Checked && _checkedTableNames.Contains(t.LogicalName));

                return matchesSearch && matchesQuickFilter;
            })
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

            _tablesList.Items.Clear();
            foreach (var table in filtered)
            {
                int index = _tablesList.Items.Add(new TableListItem { Table = table });
                if (_checkedTableNames.Contains(table.LogicalName))
                    _tablesList.SetItemChecked(index, true);
            }

            RefreshSelectedCountLabel();
        }

        private void RefreshSelectedCountLabel()
        {
            _selectedCountLabel.Text = $"{_checkedTableNames.Count} table(s) selected";
            RefreshProfileEditingBar();
        }

        private void OnSelectAllVisible(object sender, EventArgs e)
        {
            for (int i = 0; i < _tablesList.Items.Count; i++)
                _tablesList.SetItemChecked(i, true);
        }

        private void OnClearVisible(object sender, EventArgs e)
        {
            for (int i = 0; i < _tablesList.Items.Count; i++)
                _tablesList.SetItemChecked(i, false);
        }

        private void OnClearAll(object sender, EventArgs e)
        {
            // Unlike OnClearVisible, this must clear every selection regardless of the current
            // search/filter — otherwise "Clear All" would silently leave filtered-out tables
            // checked, which used to be invisible from the user's own bug report ("table search
            // doesn't work") but would now be a real, confusing behavior mismatch with the label.
            _checkedTableNames.Clear();
            ApplyTableFilter();
        }

        /// <summary>
        /// Fetches full metadata for every currently checked table and looks for lookup targets
        /// that aren't selected yet — real feedback: with ~2500 tables in a tenant, manually
        /// figuring out "this table needs these 6 others too" wasn't practical. Only suggests
        /// custom tables: a lookup toward a standard/system table (systemuser, transactioncurrency,
        /// organization, ...) is deliberately left as an external lookup resolved at runtime
        /// (ARCHITECTURE.md sección 12), not something you'd normally add to a profile.
        /// </summary>
        private void OnFindRelatedTables(object sender, EventArgs e)
        {
            if (_sourceMetadata == null)
            {
                MessageBox.Show("Connect to a Source environment first.", "No connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_checkedTableNames.Count == 0)
            {
                MessageBox.Show("Select at least one table first.", "Nothing selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var selectedNames = _checkedTableNames.ToList();
            var alreadySelected = new HashSet<string>(selectedNames, StringComparer.OrdinalIgnoreCase);

            _currentOperationCts = new CancellationTokenSource();
            var token = _currentOperationCts.Token;
            _btnFindRelatedTables.Enabled = false;
            _btnCancel.Enabled = true;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Looking for related tables...",
                Work = (worker, args) =>
                {
                    var missing = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase);

                    // Recursive (transitive closure), not one level deep: a newly-discovered
                    // dependency can itself depend on further custom tables not yet selected —
                    // real feedback: adding 1 related table surfaced a brand-new unresolved
                    // required lookup on Preflight, requiring the user to run this repeatedly by
                    // hand. `scanned` prevents re-scanning the same table twice and guards against
                    // an infinite loop on a genuine dependency cycle (e.g. A -> B -> A).
                    var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var toScan = new Queue<string>(selectedNames);
                    int checkedCount = 0;

                    while (toScan.Count > 0)
                    {
                        token.ThrowIfCancellationRequested();
                        var logicalName = toScan.Dequeue();
                        if (!scanned.Add(logicalName)) continue;

                        checkedCount++;
                        SetWorkingMessage($"Checking {logicalName} for related tables... ({checkedCount} table(s) checked, {missing.Count} found so far)");

                        TableSummary detail;
                        try { detail = _sourceMetadata.GetTableDetailAsync(logicalName, CancellationToken.None).GetAwaiter().GetResult(); }
                        catch { continue; }

                        foreach (var attr in detail.Attributes.Where(a => a.Kind == AttributeKind.Lookup))
                        {
                            foreach (var target in attr.LookupTargets)
                            {
                                if (alreadySelected.Contains(target) || missing.ContainsKey(target)) continue;

                                var candidate = _sourceTables.FirstOrDefault(t => string.Equals(t.LogicalName, target, StringComparison.OrdinalIgnoreCase));
                                // IsCustomEntity alone isn't enough — Dataverse also flags tables
                                // installed by Microsoft's own first-party managed solutions
                                // (Power Pages, Copilot/AI Builder, Dynamics Marketing, ...) as
                                // "custom", even though they're product infrastructure, not this
                                // org's business data. A first attempt excluded by IsManaged, but
                                // that was WRONG (confirmed live): IsManaged only reflects the
                                // CURRENT solution layer, not who owns the table — an org that
                                // deploys its OWN customizations as a managed solution (a normal,
                                // valid ALM pattern) has its own genuinely-custom tables excluded
                                // too. These specific prefixes are globally reserved by Microsoft
                                // across every Dataverse environment — no customer solution can
                                // use them — so checking the LogicalName's prefix is a safe,
                                // org-independent signal that IsManaged is not.
                                if (candidate != null && candidate.IsCustomEntity && !IsKnownMicrosoftManagedTable(candidate.LogicalName))
                                {
                                    missing[target] = candidate;
                                    toScan.Enqueue(target); // recurse into the newly-found dependency's own lookups too
                                }
                            }
                        }
                    }

                    args.Result = missing.Values.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
                },
                PostWorkCallBack = args =>
                {
                    _btnFindRelatedTables.Enabled = true;
                    _btnCancel.Enabled = false;

                    if (args.Error is OperationCanceledException)
                    {
                        AppendLog("Find Related Tables cancelled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        MessageBox.Show("Could not check dependencies: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var missing = (List<TableSummary>)args.Result;
                    if (missing.Count == 0)
                    {
                        MessageBox.Show(
                            "No additional related tables found — every lookup from your selected tables either " +
                            "targets a standard/system table or a table that's already selected.",
                            "Find Related Tables", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }

                    using (var dialog = new RelatedTablesForm(missing))
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.SelectedTables.Count > 0)
                        {
                            foreach (var table in dialog.SelectedTables)
                                _checkedTableNames.Add(table.LogicalName);
                            ApplyTableFilter();
                            AppendLog($"Added {dialog.SelectedTables.Count} related table(s) to the selection.");
                        }
                    }
                }
            });
        }

        /// <summary>Publisher prefixes reserved by Microsoft across every Dataverse environment —
        /// Power Pages (adx_/mspp_), Copilot/AI Builder (msdyn_), Dynamics Marketing (msdynmkt_),
        /// Dynamics 365 Customer Insights - Journeys (msdyncrm_). No customer solution can ever
        /// use these, which is what makes this a reliable "this is Microsoft's own product
        /// infrastructure, not the org's business data" signal for automatic suggestions — unlike
        /// IsManaged, which just reflects each org's own deployment/ALM choices.</summary>
        private static readonly string[] KnownMicrosoftManagedTablePrefixes =
            { "adx_", "mspp_", "msdyn_", "msdynmkt_", "msdyncrm_" };

        private static bool IsKnownMicrosoftManagedTable(string logicalName) =>
            KnownMicrosoftManagedTablePrefixes.Any(prefix => logicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        // --- Migration --------------------------------------------------------------------

        private void UpdateExecuteButtonState()
        {
            bool connected = _sourceService != null && _targetService != null;
            _btnPreflight.Enabled = connected;
            _btnPreviewData.Enabled = connected;
            _btnExecute.Enabled = connected && _lastPreflight != null && _lastPreflight.ReadyToExecute;
            _btnRetryFailed.Enabled = connected && _lastManifest != null && _lastManifest.AllFailures().Any();
        }

        /// <summary>
        /// Full metadata (attributes + relationships) for every enabled table of
        /// <see cref="_currentProfile"/>, from both Source and Target. Shared by Preflight (which
        /// needs it to report TABLE_NOT_IN_SOURCE/TARGET) and Execute (which needs it to build the
        /// real dependency-ordered <see cref="MigrationPlan"/> and to split lookup attributes across
        /// passes) so both stay consistent with a single source of truth.
        /// </summary>
        private void LoadEnabledTableMetadata(out Dictionary<string, TableSummary> sourceTables, out Dictionary<string, TableSummary> targetTables, CancellationToken token = default(CancellationToken))
        {
            sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase);
            targetTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase);

            var entities = _currentProfile.Entities.Where(x => x.Enabled).ToList();
            for (int i = 0; i < entities.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var entity = entities[i];
                SetWorkingMessage($"Loading metadata: {entity.DisplayName ?? entity.LogicalName} ({i + 1}/{entities.Count})...");

                try { sourceTables[entity.LogicalName] = _sourceMetadata.GetTableDetailAsync(entity.LogicalName, CancellationToken.None).GetAwaiter().GetResult(); }
                catch { /* left out of sourceTables => reported as TABLE_NOT_IN_SOURCE by Preflight */ }

                try { targetTables[entity.LogicalName] = _targetMetadata.GetTableDetailAsync(entity.LogicalName, CancellationToken.None).GetAwaiter().GetResult(); }
                catch { /* left out of targetTables => reported as TABLE_NOT_IN_TARGET by Preflight */ }
            }
        }

        private void OnPreflight(object sender, EventArgs e)
        {
            if (_currentProfile == null || _sourceMetadata == null || _targetMetadata == null)
            {
                MessageBox.Show("Load a profile and connect both Source and Target first.", "Cannot run Preflight",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SyncProfileEntitiesFromTableSelection();

            _currentOperationCts = new CancellationTokenSource();
            var token = _currentOperationCts.Token;
            _btnPreflight.Enabled = false;
            _btnCancel.Enabled = true;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Running Preflight...",
                Work = (worker, args) =>
                {
                    var sourceOrgId = _sourceMetadata.GetOrganizationIdAsync(CancellationToken.None).GetAwaiter().GetResult();
                    var targetOrgId = _targetMetadata.GetOrganizationIdAsync(CancellationToken.None).GetAwaiter().GetResult();

                    LoadEnabledTableMetadata(out var sourceTables, out var targetTables, token);
                    token.ThrowIfCancellationRequested();

                    SetWorkingMessage("Checking external lookups against Target...");
                    var unresolvedExternalLookups = new ExternalLookupSampler()
                        .SampleAsync(_currentProfile, sourceTables, _sourceRecords, _targetRecords, pageSize: 500, token,
                            msg => SetWorkingMessage(msg))
                        .GetAwaiter().GetResult();

                    var context = new PreflightContext
                    {
                        Profile = _currentProfile,
                        SourceOrganizationId = sourceOrgId,
                        TargetOrganizationId = targetOrgId,
                        SourceTables = sourceTables,
                        TargetTables = targetTables,
                        UnresolvedExternalLookups = unresolvedExternalLookups
                    };

                    args.Result = PreflightValidator.Validate(context);
                },
                PostWorkCallBack = args =>
                {
                    _btnPreflight.Enabled = true;
                    _btnCancel.Enabled = false;

                    if (args.Error is OperationCanceledException)
                    {
                        AppendLog("Preflight cancelled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        MessageBox.Show("Preflight failed: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    _lastPreflight = (PreflightResult)args.Result;
                    ShowPreflightSummary(_lastPreflight);
                    UpdateExecuteButtonState();
                }
            });
        }

        private void ShowPreflightSummary(PreflightResult result)
        {
            AppendLog("=== PREFLIGHT ===");
            foreach (var issue in result.Issues)
                AppendLog(issue.ToString());

            AppendLog(result.ReadyToExecute ? "RESULT: READY TO EXECUTE" : "RESULT: BLOCKED (fix errors above)");

            if (!result.ReadyToExecute)
            {
                _preflightStatusDot.BackColor = Color.Firebrick;
                _preflightStatusLabel.Text = "Bloqueado";
                _preflightStatusLabel.ForeColor = Color.Firebrick;
            }
            else if (result.HasWarnings)
            {
                _preflightStatusDot.BackColor = Color.Goldenrod;
                _preflightStatusLabel.Text = "Con advertencias";
                _preflightStatusLabel.ForeColor = Color.DarkGoldenrod;
            }
            else
            {
                _preflightStatusDot.BackColor = Color.MediumSeaGreen;
                _preflightStatusLabel.Text = "Listo para migrar";
                _preflightStatusLabel.ForeColor = Color.SeaGreen;
            }

            if (result.HasWarnings && result.ReadyToExecute)
            {
                MessageBox.Show(
                    $"Preflight completed with {result.Issues.Count(i => i.Severity == IssueSeverity.Warning)} warning(s). Review the log before executing.",
                    "Preflight", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Read-only count of what Execute would actually do to Target, given the current profile
        /// (V1 is always Upsert with preserveSourceGuid: a record is a create if its Source id
        /// doesn't already exist in Target, an update otherwise). Separate from Preflight because
        /// it necessarily reads every record's id from Source and Target — worth an explicit,
        /// deliberate click rather than folding into the fast metadata-only Preflight pass.
        /// </summary>
        private void OnPreviewData(object sender, EventArgs e)
        {
            if (_currentProfile == null || _sourceMetadata == null || _targetMetadata == null)
            {
                MessageBox.Show("Load a profile and connect both Source and Target first.", "Cannot preview",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SyncProfileEntitiesFromTableSelection();

            _currentOperationCts = new CancellationTokenSource();
            var token = _currentOperationCts.Token;
            _btnPreviewData.Enabled = false;
            _btnCancel.Enabled = true;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Previewing data (reading records from Source and Target)...",
                Work = (worker, args) =>
                {
                    LoadEnabledTableMetadata(out var sourceTables, out var targetTables, token);
                    token.ThrowIfCancellationRequested();

                    var plan = MigrationPlanner.CreatePlan(_currentProfile, sourceTables);
                    var entityFilters = _currentProfile.Entities
                        .Where(pe => pe.Enabled)
                        .ToDictionary(pe => pe.LogicalName, pe => pe.Filter, StringComparer.OrdinalIgnoreCase);

                    args.Result = new MigrationPreviewBuilder()
                        .BuildAsync(plan, sourceTables, _sourceRecords, _targetRecords, pageSize: 1000, maxRecordsPerTable: 2000, token,
                            msg => SetWorkingMessage(msg), entityFilters)
                        .GetAwaiter().GetResult();
                },
                PostWorkCallBack = args =>
                {
                    _btnPreviewData.Enabled = true;
                    _btnCancel.Enabled = false;

                    if (args.Error is OperationCanceledException)
                    {
                        AppendLog("Preview cancelled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        MessageBox.Show("Preview failed: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var preview = (IReadOnlyList<TableDataPreview>)args.Result;
                    AppendLog("=== DATA PREVIEW (Upsert, preserving Source GUIDs) ===");
                    foreach (var t in preview)
                        AppendLog($"{t.LogicalName}: {t.SourceRecordCount} in Source -> {t.ToCreate} to create, {t.ToUpdate} to update.");
                    SwitchToMigrationTab();

                    using (var dialog = new PreviewResultsForm(preview))
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            _recordSelectionOverrides = dialog.ExcludedRecordIds;
                            var excludedCount = _recordSelectionOverrides.Sum(kvp => kvp.Value.Count);
                            AppendLog(excludedCount == 0
                                ? "Record selection confirmed: every record will be migrated."
                                : $"Record selection updated: {excludedCount} record(s) across {_recordSelectionOverrides.Count} table(s) excluded from the next Execute.");
                        }
                    }
                }
            });
        }

        /// <summary>
        /// Structure-only comparison (no data/records) between Source and Target for every
        /// enabled table of <see cref="_currentProfile"/> — lets the user spot schema drift
        /// (missing tables/attributes, mismatched type/required-ness) before running a real
        /// migration. Modeled closely on <see cref="OnPreflight"/>: same guard clauses and
        /// cancellation wiring, but reports through a standalone export dialog instead of the log.
        /// </summary>
        private void OnCompareStructure(object sender, EventArgs e)
        {
            if (_currentProfile == null || _sourceMetadata == null || _targetMetadata == null)
            {
                MessageBox.Show("Load a profile and connect both Source and Target first.", "Cannot run Compare Structure",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SyncProfileEntitiesFromTableSelection();

            _currentOperationCts = new CancellationTokenSource();
            var token = _currentOperationCts.Token;
            _btnCompareStructure.Enabled = false;
            _btnCancel.Enabled = true;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Comparing structure...",
                Work = (worker, args) =>
                {
                    LoadEnabledTableMetadata(out var sourceTables, out var targetTables, token);
                    token.ThrowIfCancellationRequested();

                    var orderedLogicalNames = _currentProfile.Entities
                        .Where(x => x.Enabled)
                        .OrderBy(x => x.DisplayName ?? x.LogicalName, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.LogicalName)
                        .ToList();

                    args.Result = new SchemaComparisonBuilder().Build(sourceTables, targetTables, orderedLogicalNames);
                },
                PostWorkCallBack = args =>
                {
                    _btnCompareStructure.Enabled = true;
                    _btnCancel.Enabled = false;

                    if (args.Error is OperationCanceledException)
                    {
                        AppendLog("Compare Structure cancelled.");
                        return;
                    }

                    if (args.Error != null)
                    {
                        MessageBox.Show("Compare Structure failed: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    var result = (SchemaComparisonResult)args.Result;
                    var mismatched = result.Tables.Count(t => t.HasDifferences);
                    AppendLog($"Structure comparison: {result.Tables.Count} table(s) compared, {mismatched} with differences.");

                    using (var dialog = new SchemaComparisonExportForm(result, _sourceLabel.Text, _targetLabel.Text))
                    {
                        dialog.ShowDialog(this);
                    }
                }
            });
        }

        private void OnExecute(object sender, EventArgs e)
        {
            if (_lastPreflight == null || !_lastPreflight.ReadyToExecute)
                return;

            var confirmed = MessageBox.Show(
                $"SOURCE → TARGET\n\nThis will migrate profile '{_currentProfile.Name}' now.\nContinue?",
                "Confirm execution", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmed != DialogResult.Yes) return;

            _currentOperationCts = new CancellationTokenSource();
            var token = _currentOperationCts.Token;
            _btnExecute.Enabled = false;
            _btnCancel.Enabled = true;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Executing migration...",
                Work = (worker, args) =>
                {
                    LoadEnabledTableMetadata(out var sourceTables, out var targetTables, token);

                    // Real dependency-ordered plan, built from actual attribute metadata (not the
                    // empty placeholder TableSummary used before this was wired up) — otherwise
                    // DependencyGraphBuilder sees zero lookup edges and the topological order
                    // collapses to plain preferredOrder.
                    var plan = MigrationPlanner.CreatePlan(_currentProfile, sourceTables);

                    var request = new MigrationExecutionRequest
                    {
                        Profile = _currentProfile,
                        Plan = plan,
                        SourceTables = sourceTables,
                        TargetTables = targetTables,
                        SourceRecords = _sourceRecords,
                        TargetRecords = _targetRecords,
                        SourceMetadata = _sourceMetadata,
                        SourceLabel = _sourceLabel.Text,
                        TargetLabel = _targetLabel.Text,
                        Logger = new PluginExecutionLogger(AppendLog),
                        ManifestStore = _manifestStore,
                        ExcludedRecordIds = _recordSelectionOverrides
                    };

                    args.Result = new MigrationExecutor().ExecuteAsync(request, token).GetAwaiter().GetResult();
                },
                PostWorkCallBack = args =>
                {
                    _btnExecute.Enabled = true;
                    _btnCancel.Enabled = false;

                    if (args.Error != null)
                    {
                        AppendLog("ERROR: Execution failed: " + args.Error.Message);
                        MessageBox.Show("Execution failed: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    _lastManifest = (ExecutionManifest)args.Result;
                    AppendLog($"=== EXECUTION {_lastManifest.Status.ToString().ToUpperInvariant()} (id {_lastManifest.ExecutionId:D}) ===");
                    foreach (var t in _lastManifest.Tables)
                    {
                        AppendLog($"{t.LogicalName}: {t.Created} created, {t.Updated} updated, {t.Failed} failed of {t.SourceRecordCount} " +
                                   $"({t.Duration.TotalSeconds:0.0}s, {t.WriteStrategyUsed}).");
                        LogFailureDetails(t);
                    }

                    UpdateExecuteButtonState();
                    ShowTaskCompletedMessage("Execution Completed", _lastManifest);
                }
            });
        }

        /// <summary>
        /// Surfaces WHY records failed, not just how many — real feedback: a run with 56 failures
        /// out of 2739 records showed nothing but the count, with no way to diagnose it short of
        /// opening the ExecutionManifest JSON on disk by hand. Groups by distinct error message
        /// since bulk failures usually share one root cause, rather than dumping every failure
        /// as its own near-identical line.
        /// </summary>
        private void LogFailureDetails(TableExecutionResult t)
        {
            if (t.Failed == 0) return;

            var grouped = t.Errors
                .Where(e => e.Outcome == RecordOutcome.Failed)
                .GroupBy(e => e.ErrorMessage ?? "(no error message)")
                .OrderByDescending(g => g.Count());

            foreach (var g in grouped)
                AppendLog($"    {g.Count()}x: {g.Key}  (e.g. record {g.First().RecordId:D}, pass {g.First().Pass})");
        }

        /// <summary>
        /// A popup, not just a log line — real feedback: for a run that takes a couple of
        /// minutes, easy to walk away and miss the moment it actually finished if the only signal
        /// is text scrolling by in the log box.
        /// </summary>
        private void ShowTaskCompletedMessage(string title, ExecutionManifest manifest)
        {
            var totalCreated = manifest.Tables.Sum(t => t.Created);
            var totalUpdated = manifest.Tables.Sum(t => t.Updated);
            var totalFailed = manifest.Tables.Sum(t => t.Failed);

            var message = $"Status: {manifest.Status}\n\n" +
                          $"{totalCreated} created, {totalUpdated} updated, {totalFailed} failed (across {manifest.Tables.Count} table(s)).";
            message += totalFailed > 0 ? "\n\nCheck the log for details on what failed." : "\n\nAll records processed successfully.";

            var icon = totalFailed > 0 || manifest.Status == ExecutionStatus.Failed
                ? MessageBoxIcon.Warning
                : MessageBoxIcon.Information;

            MessageBox.Show(message, title, MessageBoxButtons.OK, icon);
        }

        private void OnCancel(object sender, EventArgs e)
        {
            _currentOperationCts?.Cancel();
            AppendLog("Cancellation requested.");
        }

        private void OnRetryFailed(object sender, EventArgs e)
        {
            if (_lastManifest == null || !_lastManifest.AllFailures().Any())
            {
                MessageBox.Show("There is no failed execution to retry. Run Execute first.", "Nothing to retry",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirmed = MessageBox.Show(
                $"Retry {_lastManifest.AllFailures().Count()} failed record(s) from execution {_lastManifest.ExecutionId:D}?\nContinue?",
                "Confirm Retry Failed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirmed != DialogResult.Yes) return;

            _currentOperationCts = new CancellationTokenSource();
            var token = _currentOperationCts.Token;
            _btnExecute.Enabled = false;
            _btnRetryFailed.Enabled = false;
            _btnCancel.Enabled = true;

            WorkAsync(new WorkAsyncInfo
            {
                Message = "Retrying failed records...",
                Work = (worker, args) =>
                {
                    // Only lightweight per-table metadata is reloaded here — never the tables'
                    // actual data, and MigrationPlanner.CreatePlan is pure in-memory arithmetic
                    // over that metadata, so this stays within "sin re-planificar ni re-leer
                    // tablas ya completadas" (ARCHITECTURE.md sección 7): RetryFailedAsync itself
                    // only re-fetches the specific record ids that were previously marked Failed.
                    LoadEnabledTableMetadata(out var sourceTables, out var targetTables, token);
                    var plan = MigrationPlanner.CreatePlan(_currentProfile, sourceTables);

                    var request = new MigrationExecutionRequest
                    {
                        Profile = _currentProfile,
                        Plan = plan,
                        SourceTables = sourceTables,
                        TargetTables = targetTables,
                        SourceRecords = _sourceRecords,
                        TargetRecords = _targetRecords,
                        SourceMetadata = _sourceMetadata,
                        SourceLabel = _sourceLabel.Text,
                        TargetLabel = _targetLabel.Text,
                        Logger = new PluginExecutionLogger(AppendLog),
                        ManifestStore = _manifestStore
                    };

                    args.Result = new MigrationExecutor().RetryFailedAsync(_lastManifest, request, token).GetAwaiter().GetResult();
                },
                PostWorkCallBack = args =>
                {
                    _btnExecute.Enabled = true;
                    _btnCancel.Enabled = false;

                    if (args.Error != null)
                    {
                        AppendLog("ERROR: Retry Failed failed: " + args.Error.Message);
                        MessageBox.Show("Retry Failed failed: " + args.Error.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        UpdateExecuteButtonState();
                        return;
                    }

                    _lastManifest = (ExecutionManifest)args.Result;
                    AppendLog($"=== RETRY FAILED {_lastManifest.Status.ToString().ToUpperInvariant()} (id {_lastManifest.ExecutionId:D}) ===");
                    foreach (var t in _lastManifest.Tables)
                    {
                        AppendLog($"{t.LogicalName}: {t.Created} created, {t.Updated} updated, {t.Failed} failed of {t.SourceRecordCount}.");
                        LogFailureDetails(t);
                    }

                    UpdateExecuteButtonState();
                    ShowTaskCompletedMessage("Retry Failed Completed", _lastManifest);
                }
            });
        }

        private void OnViewLog(object sender, EventArgs e)
        {
            SwitchToMigrationTab();
            _logBox.Focus();
            _logBox.SelectionStart = _logBox.Text.Length;
            _logBox.ScrollToCaret();
        }

        private void OnClearLog(object sender, EventArgs e)
        {
            _logBox.Clear();
        }

        /// <summary>
        /// Jumps to the Migration tab, where Preflight/Execute live. Called after any action that
        /// leaves the user with a saved profile ready to run — otherwise there's no visual cue
        /// pointing them from Tables/Profiles toward what to do next (real feedback: a first-time
        /// user had no idea Migration was a separate tab with its own Preflight/Execute buttons).
        /// </summary>
        private void SwitchToMigrationTab()
        {
            var migrationTab = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Text == "Migration");
            if (migrationTab != null) _tabs.SelectedTab = migrationTab;
        }

        private void SwitchToTablesTab()
        {
            var tablesTab = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Text == "Tables");
            if (tablesTab != null) _tabs.SelectedTab = tablesTab;
        }

        private void AppendLog(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (_logBox.InvokeRequired)
                _logBox.Invoke(new Action(() => _logBox.AppendText(line + Environment.NewLine)));
            else
                _logBox.AppendText(line + Environment.NewLine);
        }

        private sealed class ProfileListItem
        {
            public string Label;
            public MigrationProfile Profile;
            public override string ToString() => Label;
        }

        private sealed class TableListItem
        {
            public TableSummary Table;
            public override string ToString() => $"{Table.DisplayName}  ({Table.LogicalName})";
        }
    }
}
