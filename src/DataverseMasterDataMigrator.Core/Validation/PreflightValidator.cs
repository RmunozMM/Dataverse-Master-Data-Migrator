using System;
using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;

namespace DataverseMasterDataMigrator.Core.Validation
{
    /// <summary>
    /// Un lookup externo al perfil (apunta a una tabla no incluida) cuya existencia en Target ya
    /// fue verificada por la capa XrmToolBox consultando datos reales. El Core no consulta
    /// datos por sí mismo aquí: recibe el hallazgo ya hecho, lo que mantiene esta clase testeable
    /// sin conexión (ver ARCHITECTURE.md sección 5).
    /// </summary>
    public sealed class UnresolvedLookupFinding
    {
        public string TableLogicalName { get; set; }
        public string AttributeLogicalName { get; set; }
        public bool IsRequired { get; set; }
        public int AffectedRecordCount { get; set; }
    }

    public sealed class PreflightContext
    {
        public MigrationProfile Profile { get; set; }
        public string SourceOrganizationId { get; set; }
        public string TargetOrganizationId { get; set; }
        public IReadOnlyDictionary<string, TableSummary> SourceTables { get; set; } = new Dictionary<string, TableSummary>();
        public IReadOnlyDictionary<string, TableSummary> TargetTables { get; set; } = new Dictionary<string, TableSummary>();
        public IReadOnlyList<UnresolvedLookupFinding> UnresolvedExternalLookups { get; set; } = new List<UnresolvedLookupFinding>();
    }

    public static class PreflightValidator
    {
        public static PreflightResult Validate(PreflightContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            var result = new PreflightResult();

            if (context.Profile == null)
            {
                result.AddError("PROFILE_MISSING", "No hay un perfil cargado.");
                return result;
            }

            var structuralErrors = context.Profile.ValidateStructure();
            foreach (var e in structuralErrors)
                result.AddError("PROFILE_INVALID", e);

            if (structuralErrors.Count > 0)
                return result; // Sin un perfil estructuralmente válido no tiene sentido seguir.

            if (!string.IsNullOrEmpty(context.SourceOrganizationId) &&
                !string.IsNullOrEmpty(context.TargetOrganizationId) &&
                string.Equals(context.SourceOrganizationId, context.TargetOrganizationId, StringComparison.OrdinalIgnoreCase))
            {
                result.AddError("SOURCE_EQUALS_TARGET",
                    "Source y Target apuntan al mismo ambiente (mismo OrganizationId). No se puede ejecutar.");
            }

            var enabledEntities = context.Profile.Entities.Where(e => e.Enabled).ToList();
            if (enabledEntities.Count == 0)
            {
                result.AddError("NO_TABLES_SELECTED", "El perfil no tiene tablas habilitadas.");
                return result;
            }

            var usableTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in enabledEntities)
            {
                if (!context.SourceTables.TryGetValue(entity.LogicalName, out var sourceTable))
                {
                    result.AddError("TABLE_NOT_IN_SOURCE",
                        $"La tabla '{entity.LogicalName}' del perfil ya no existe en Source.",
                        entity.LogicalName);
                    continue;
                }

                if (!context.TargetTables.TryGetValue(entity.LogicalName, out var targetTable))
                {
                    result.AddError("TABLE_NOT_IN_TARGET",
                        $"La tabla '{entity.LogicalName}' no existe en Target.",
                        entity.LogicalName);
                    continue;
                }

                usableTables[entity.LogicalName] = sourceTable;
                CheckAttributeSchemaDrift(result, context.Profile, entity, sourceTable, targetTable);
            }

            if (usableTables.Count > 0)
            {
                var plan = MigrationPlanner.CreatePlan(context.Profile, usableTables);
                foreach (var cycle in plan.Cycles)
                {
                    result.AddWarning("DEPENDENCY_CYCLE",
                        $"Ciclo de dependencias detectado entre: {string.Join(" -> ", cycle)}. " +
                        "Se resuelve automáticamente con estrategia multipass.",
                        cycle.FirstOrDefault());
                }
            }

            foreach (var finding in context.UnresolvedExternalLookups ?? Array.Empty<UnresolvedLookupFinding>())
            {
                if (finding.IsRequired)
                {
                    var severity = context.Profile.Options.RequiredLookupPolicy;
                    var message = $"'{finding.TableLogicalName}.{finding.AttributeLogicalName}' es un lookup " +
                                   $"obligatorio hacia una tabla fuera del perfil, y {finding.AffectedRecordCount} " +
                                   "registro(s) referencian un valor que no existe en Target.";

                    if (severity == LookupPolicy.FailPreflight)
                        result.AddError("REQUIRED_LOOKUP_UNRESOLVED", message, finding.TableLogicalName);
                    else
                        result.AddWarning("REQUIRED_LOOKUP_UNRESOLVED", message, finding.TableLogicalName);
                }
                else
                {
                    var policy = context.Profile.Options.OptionalLookupPolicy;
                    var message = $"'{finding.TableLogicalName}.{finding.AttributeLogicalName}' es un lookup " +
                                   $"opcional hacia una tabla fuera del perfil, y {finding.AffectedRecordCount} " +
                                   "registro(s) referencian un valor que no existe en Target.";

                    if (policy == LookupPolicy.SkipSilently)
                        result.AddInfo("OPTIONAL_LOOKUP_UNRESOLVED", message, finding.TableLogicalName);
                    else
                        result.AddWarning("OPTIONAL_LOOKUP_UNRESOLVED", message, finding.TableLogicalName);
                }
            }

            return result;
        }

        /// <summary>
        /// Warns when a Source attribute that would actually be written (per
        /// <see cref="AttributeWritabilityRules"/> — the same rule Execute itself uses) doesn't
        /// exist at all in Target. This is a pure metadata comparison, cheap and independent of
        /// which records currently have that field populated — it means Target's solution/
        /// customizations are behind Source, and any record with that field set will fail to
        /// write. Real feedback: a run had 37 of 56 failures be exactly this ("entity doesn't
        /// contain attribute..."), discovered only after Execute rather than before it.
        /// </summary>
        private static void CheckAttributeSchemaDrift(
            PreflightResult result, MigrationProfile profile, ProfileEntity entity, TableSummary sourceTable, TableSummary targetTable)
        {
            bool restoreState = profile.Options.RestoreStateStatus && sourceTable.HasStateStatus;
            var writableAttrs = AttributeWritabilityRules.GetWritableAttributes(sourceTable, entity, restoreState);
            var targetAttributeNames = new HashSet<string>(
                targetTable.Attributes.Select(a => a.LogicalName), StringComparer.OrdinalIgnoreCase);

            foreach (var attr in writableAttrs)
            {
                if (targetAttributeNames.Contains(attr.LogicalName)) continue;

                result.AddWarning("ATTRIBUTE_NOT_IN_TARGET",
                    $"'{entity.LogicalName}.{attr.LogicalName}' existe en Source pero no en Target — probablemente " +
                    "el solution/customizaciones de Target está desactualizado respecto a Source. Cualquier " +
                    "registro que tenga este campo con valor va a fallar al escribirse.",
                    entity.LogicalName);
            }
        }
    }
}
