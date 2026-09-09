using System;
using DataverseMasterDataMigrator.Core.Models;
using Newtonsoft.Json;

namespace DataverseMasterDataMigrator.Core.Profiles
{
    public static class MigrationProfileSerializer
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc
        };

        public static string Serialize(MigrationProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            return JsonConvert.SerializeObject(profile, Settings);
        }

        /// <summary>
        /// Nunca lanza. Un perfil corrupto no puede tumbar el plugin (sección 6 del
        /// requerimiento). Devuelve el motivo del fallo en <paramref name="error"/>.
        /// </summary>
        public static bool TryDeserialize(string json, out MigrationProfile profile, out string error)
        {
            profile = null;
            error = null;

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "El archivo está vacío.";
                return false;
            }

            try
            {
                var candidate = JsonConvert.DeserializeObject<MigrationProfile>(json, Settings);
                if (candidate == null)
                {
                    error = "El archivo no contiene un objeto de perfil válido.";
                    return false;
                }

                if (candidate.SchemaVersion > MigrationProfile.CurrentSchemaVersion)
                {
                    error = $"Este perfil requiere una versión más reciente del plugin " +
                            $"(schemaVersion {candidate.SchemaVersion}, soportado hasta " +
                            $"{MigrationProfile.CurrentSchemaVersion}).";
                    return false;
                }

                var structuralErrors = candidate.ValidateStructure();
                if (structuralErrors.Count > 0)
                {
                    error = string.Join(" | ", structuralErrors);
                    return false;
                }

                profile = candidate;
                return true;
            }
            catch (JsonException ex)
            {
                error = $"JSON inválido: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                // Defensivo: cualquier otra falla de deserialización tampoco debe propagarse.
                error = $"No se pudo leer el perfil: {ex.Message}";
                return false;
            }
        }
    }
}
