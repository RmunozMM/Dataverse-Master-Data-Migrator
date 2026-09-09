using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Profiles
{
    public sealed class ProfileLoadResult
    {
        public MigrationProfile Profile { get; set; }
        public string FileName { get; set; }
        public bool IsValid { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Persistencia de perfiles en archivos individuales. Recibe la carpeta ya resuelta por el
    /// llamador (ver ARCHITECTURE.md sección 3: el Core no decide rutas de XrmToolBox/AppData).
    /// </summary>
    public sealed class MigrationProfileRepository
    {
        private const int MaxBackupsPerProfile = 5;
        private readonly string _profilesFolder;
        private readonly string _backupsFolder;

        public MigrationProfileRepository(string profilesFolder)
        {
            if (string.IsNullOrWhiteSpace(profilesFolder))
                throw new ArgumentException("profilesFolder es obligatorio.", nameof(profilesFolder));

            _profilesFolder = profilesFolder;
            _backupsFolder = Path.Combine(profilesFolder, "Backups");
        }

        public string ProfilesFolder => _profilesFolder;

        public void EnsureFoldersExist()
        {
            Directory.CreateDirectory(_profilesFolder);
            Directory.CreateDirectory(_backupsFolder);
        }

        private string PathFor(Guid id) => Path.Combine(_profilesFolder, id.ToString("D") + ".json");

        /// <summary>
        /// Lee todos los perfiles de la carpeta. Un archivo corrupto se reporta con
        /// <c>IsValid = false</c> pero no interrumpe la lectura del resto (sección 6:
        /// "no permitir que un perfil defectuoso tumbe el plugin").
        /// </summary>
        public IReadOnlyList<ProfileLoadResult> LoadAll()
        {
            EnsureFoldersExist();
            var results = new List<ProfileLoadResult>();

            foreach (var file in Directory.EnumerateFiles(_profilesFolder, "*.json", SearchOption.TopDirectoryOnly))
            {
                string json;
                try
                {
                    json = File.ReadAllText(file, System.Text.Encoding.UTF8);
                }
                catch (IOException ex)
                {
                    results.Add(new ProfileLoadResult
                    {
                        FileName = Path.GetFileName(file),
                        IsValid = false,
                        Error = $"No se pudo leer el archivo: {ex.Message}"
                    });
                    continue;
                }

                bool ok = MigrationProfileSerializer.TryDeserialize(json, out var profile, out var error);
                results.Add(new ProfileLoadResult
                {
                    FileName = Path.GetFileName(file),
                    Profile = profile,
                    IsValid = ok,
                    Error = error
                });
            }

            return results;
        }

        /// <summary>
        /// Guarda el perfil. Si ya existía un archivo con el mismo id, lo respalda primero
        /// (sección 6: "generar backups al modificar un perfil").
        /// </summary>
        public void Save(MigrationProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));

            var structuralErrors = profile.ValidateStructure();
            if (structuralErrors.Count > 0)
                throw new InvalidOperationException(
                    "No se puede guardar un perfil inválido: " + string.Join(" | ", structuralErrors));

            EnsureFoldersExist();

            var targetPath = PathFor(profile.Id);
            if (File.Exists(targetPath))
            {
                BackupExisting(profile.Id, targetPath);
            }

            profile.UpdatedUtc = DateTime.UtcNow;
            var json = MigrationProfileSerializer.Serialize(profile);

            // Escritura atómica simple: a un archivo temporal y luego reemplazo, para no dejar
            // un .json truncado si el proceso se interrumpe a mitad de escritura.
            var tempPath = targetPath + ".tmp";
            File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);
        }

        public void Delete(Guid id)
        {
            var path = PathFor(id);
            if (File.Exists(path))
                File.Delete(path);
        }

        private void BackupExisting(Guid id, string currentPath)
        {
            try
            {
                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var backupPath = Path.Combine(_backupsFolder, $"{id:D}.{stamp}.json");
                File.Copy(currentPath, backupPath, overwrite: true);
                RotateBackups(id);
            }
            catch (IOException)
            {
                // Un backup fallido no debe impedir guardar el cambio real del usuario.
            }
        }

        private void RotateBackups(Guid id)
        {
            var pattern = $"{id:D}.*.json";
            var backups = Directory.EnumerateFiles(_backupsFolder, pattern)
                .OrderByDescending(f => f)
                .ToList();

            foreach (var stale in backups.Skip(MaxBackupsPerProfile))
            {
                try { File.Delete(stale); } catch (IOException) { /* best effort */ }
            }
        }
    }
}
