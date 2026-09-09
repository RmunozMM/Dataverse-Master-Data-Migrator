using System;
using System.IO;
using DataverseMasterDataMigrator.Core.Models;
using Newtonsoft.Json;

namespace DataverseMasterDataMigrator.Core.Migration
{
    /// <summary>
    /// Guarda el manifest de ejecución incrementalmente (sección 20: checkpoint "al menos por
    /// tabla"). Cada llamada a <see cref="Save"/> sobrescribe el mismo archivo de forma atómica
    /// (escribir a .tmp + reemplazar), así una caída a mitad de la tabla 73 dejó registrado el
    /// resultado de las 72 anteriores.
    /// </summary>
    public sealed class ExecutionManifestStore
    {
        private readonly string _folder;
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };

        public ExecutionManifestStore(string executionsFolder)
        {
            if (string.IsNullOrWhiteSpace(executionsFolder))
                throw new ArgumentException("executionsFolder es obligatorio.", nameof(executionsFolder));
            _folder = executionsFolder;
        }

        private string PathFor(Guid executionId) => Path.Combine(_folder, executionId.ToString("D") + ".json");

        public void Save(ExecutionManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            Directory.CreateDirectory(_folder);

            var json = JsonConvert.SerializeObject(manifest, Settings);
            var path = PathFor(manifest.ExecutionId);
            var tempPath = path + ".tmp";

            File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(tempPath, path);
        }

        public ExecutionManifest Load(Guid executionId)
        {
            var path = PathFor(executionId);
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonConvert.DeserializeObject<ExecutionManifest>(json, Settings);
        }

        public string[] ListExecutionFiles()
        {
            Directory.CreateDirectory(_folder);
            return Directory.GetFiles(_folder, "*.json", SearchOption.TopDirectoryOnly);
        }
    }
}
