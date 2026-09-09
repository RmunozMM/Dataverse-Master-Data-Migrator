using System;
using System.IO;
using System.Linq;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Profiles;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class ProfileRepositoryTests : IDisposable
    {
        private readonly string _tempFolder;

        public ProfileRepositoryTests()
        {
            _tempFolder = Path.Combine(Path.GetTempPath(), "dmdm_tests_" + Guid.NewGuid().ToString("N"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, recursive: true);
        }

        private static MigrationProfile NewProfile(string name) => new MigrationProfile
        {
            Name = name,
            Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
        };

        [Fact]
        public void Save_Then_LoadAll_RoundTrips()
        {
            var repo = new MigrationProfileRepository(_tempFolder);
            var profile = NewProfile("Maestros Comercial");

            repo.Save(profile);
            var loaded = repo.LoadAll();

            Assert.Single(loaded);
            Assert.True(loaded[0].IsValid);
            Assert.Equal(profile.Id, loaded[0].Profile.Id);
        }

        [Fact]
        public void Save_Twice_CreatesBackupOfPreviousVersion()
        {
            var repo = new MigrationProfileRepository(_tempFolder);
            var profile = NewProfile("Maestros Admisión");
            repo.Save(profile);

            profile.Description = "actualizado";
            repo.Save(profile);

            var backupsFolder = Path.Combine(_tempFolder, "Backups");
            var backups = Directory.GetFiles(backupsFolder, $"{profile.Id:D}.*.json");
            Assert.Single(backups);
        }

        [Fact]
        public void Save_MoreThanFiveTimes_RotatesOldBackups()
        {
            var repo = new MigrationProfileRepository(_tempFolder);
            var profile = NewProfile("Maestros Marketing");
            repo.Save(profile);

            for (int i = 0; i < 8; i++)
            {
                profile.Description = "rev " + i;
                repo.Save(profile);
                System.Threading.Thread.Sleep(1010); // el timestamp del backup tiene resolución de segundo
            }

            var backupsFolder = Path.Combine(_tempFolder, "Backups");
            var backups = Directory.GetFiles(backupsFolder, $"{profile.Id:D}.*.json");
            Assert.True(backups.Length <= 5, $"Se esperaban máximo 5 backups, hubo {backups.Length}");
        }

        [Fact]
        public void LoadAll_WithOneCorruptFile_StillReturnsTheOthers()
        {
            var repo = new MigrationProfileRepository(_tempFolder);
            repo.Save(NewProfile("Maestros Completos"));

            repo.EnsureFoldersExist();
            File.WriteAllText(Path.Combine(_tempFolder, "corrupto.json"), "{ esto no es json valido");

            var loaded = repo.LoadAll();

            Assert.Equal(2, loaded.Count);
            Assert.Contains(loaded, r => r.IsValid);
            Assert.Contains(loaded, r => !r.IsValid);
        }

        [Fact]
        public void Delete_RemovesProfileFile()
        {
            var repo = new MigrationProfileRepository(_tempFolder);
            var profile = NewProfile("Maestros Temporales");
            repo.Save(profile);

            repo.Delete(profile.Id);

            Assert.Empty(repo.LoadAll());
        }
    }
}
