using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace DataverseMasterDataMigrator.Core.Models
{
    public enum MigrationMode
    {
        Upsert,
        CreateOnly,
        UpdateOnly
    }

    public enum LookupPolicy
    {
        FailPreflight,
        WarnOnly,
        WarnAndContinue,
        SkipSilently
    }

    public enum AttributeSelectionMode
    {
        AllWritable
    }

    public sealed class ProfileOptions
    {
        public bool PreserveSourceGuid { get; set; } = true;
        public MigrationMode Mode { get; set; } = MigrationMode.Upsert;
        public bool StopOnError { get; set; } = false;
        public bool RestoreStateStatus { get; set; } = true;
        public bool CreateManyToMany { get; set; } = true;
        public LookupPolicy RequiredLookupPolicy { get; set; } = LookupPolicy.FailPreflight;
        public LookupPolicy OptionalLookupPolicy { get; set; } = LookupPolicy.WarnAndContinue;
    }

    public sealed class ProfileEntity
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public bool Enabled { get; set; } = true;
        public int PreferredOrder { get; set; }
        public string Filter { get; set; }
        public AttributeSelectionMode AttributeMode { get; set; } = AttributeSelectionMode.AllWritable;
        public List<string> ExcludedAttributes { get; set; } = new List<string>();
    }

    public sealed class MigrationProfile
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public ProfileOptions Options { get; set; } = new ProfileOptions();
        public List<ProfileEntity> Entities { get; set; } = new List<ProfileEntity>();

        /// <summary>
        /// Validación estructural mínima (no de metadata real, eso lo hace el Preflight).
        /// Comprueba que el objeto en memoria sea coherente para poder operarlo.
        /// </summary>
        public IReadOnlyList<string> ValidateStructure()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("El perfil no tiene nombre.");

            if (Options == null)
                errors.Add("El perfil no tiene sección 'options'.");

            if (Entities == null)
            {
                errors.Add("El perfil no tiene sección 'entities'.");
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in Entities)
                {
                    if (string.IsNullOrWhiteSpace(e.LogicalName))
                    {
                        errors.Add("Hay una entidad del perfil sin 'logicalName'.");
                        continue;
                    }

                    if (!seen.Add(e.LogicalName))
                        errors.Add($"La tabla '{e.LogicalName}' está repetida en el perfil.");
                }
            }

            return errors;
        }
    }
}
