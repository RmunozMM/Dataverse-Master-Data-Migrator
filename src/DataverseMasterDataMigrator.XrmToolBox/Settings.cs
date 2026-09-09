namespace DataverseMasterDataMigrator.XrmToolBox
{
    /// <summary>
    /// Small preferences persisted via XrmToolBox's SettingsManager, same pattern as Metadata
    /// Dataverse Document's Settings.cs. Migration profiles themselves are NOT here — they are
    /// independent files managed by Core.Profiles.MigrationProfileRepository (section 7 of the
    /// requirement: profiles must survive plugin DLL updates and outlive small preferences).
    /// </summary>
    public class Settings
    {
        public string LastProfileId { get; set; }
        public string LastTablesSearchText { get; set; }
        public int LeftPanelWidth { get; set; } = 380;
        public bool IncludeSystemTables { get; set; } = false;
    }
}
