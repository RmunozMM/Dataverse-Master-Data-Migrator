using System;
using System.Collections.Generic;
using System.Linq;

namespace DataverseMasterDataMigrator.Core.Models
{
    public enum AttributeKind
    {
        Primitive,
        Lookup,
        PrimaryId,
        Virtual,
        StateStatus
    }

    public sealed class AttributeSummary
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public AttributeKind Kind { get; set; }
        public bool IsValidForCreate { get; set; }
        public bool IsValidForUpdate { get; set; }

        /// <summary>
        /// Tablas de destino posibles cuando <see cref="Kind"/> es <see cref="AttributeKind.Lookup"/>.
        /// Un lookup polimórfico (customer, owner) puede apuntar a más de una tabla.
        /// </summary>
        public IReadOnlyList<string> LookupTargets { get; set; } = new List<string>();

        /// <summary>
        /// Nivel de obligatoriedad tal como lo reporta la metadata (ApplicationRequired,
        /// SystemRequired, Recommended, None). Se guarda como texto para no acoplar el Core a
        /// un enum específico del SDK; "None" es el único valor que se interpreta como opcional.
        /// </summary>
        public string RequiredLevel { get; set; } = "None";

        public bool IsRequired => !string.Equals(RequiredLevel, "None", System.StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True for Dataverse's built-in polymorphic "Owner" type (<c>ownerid</c> and
        /// equivalents) — by platform convention this always targets exactly
        /// <c>systemuser</c>/<c>team</c>, never anything else. These identities are
        /// environment-specific and virtually never carry meaning across a cross-environment
        /// migration (a Source user/team GUID essentially never exists in Target) — but unlike
        /// createdby/modifiedby, ownerid genuinely IS writable, so it needs its own check rather
        /// than falling out of the IsValidForCreate/IsValidForUpdate filter. Treated the same way
        /// as those system-managed fields: never written, never sampled for Preflight.
        /// </summary>
        public bool IsOwnerLookup =>
            Kind == AttributeKind.Lookup &&
            LookupTargets.Count > 0 &&
            LookupTargets.All(t =>
                string.Equals(t, "systemuser", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "team", StringComparison.OrdinalIgnoreCase));
    }

    public enum RelationshipKind
    {
        OneToMany,
        ManyToOne,
        ManyToMany
    }

    public sealed class RelationshipSummary
    {
        public string SchemaName { get; set; }
        public RelationshipKind Kind { get; set; }

        /// <summary>Tabla que contiene el atributo lookup (para 1:N / N:1).</summary>
        public string ReferencingEntity { get; set; }
        public string ReferencingAttribute { get; set; }

        /// <summary>Tabla apuntada por el lookup (para 1:N / N:1).</summary>
        public string ReferencedEntity { get; set; }

        /// <summary>Solo para N:N: las dos tablas relacionadas y la tabla de intersección.</summary>
        public string Entity1LogicalName { get; set; }
        public string Entity2LogicalName { get; set; }
        public string IntersectEntityName { get; set; }
    }

    public sealed class TableSummary
    {
        public string LogicalName { get; set; }
        public string DisplayName { get; set; }
        public string SchemaName { get; set; }
        public bool IsCustomEntity { get; set; }
        public string PrimaryIdAttribute { get; set; }

        /// <summary>The table's "name" column (e.g. Dataverse's <c>PrimaryNameAttribute</c>) —
        /// used to show a human-readable label for a record instead of just its GUID, e.g. in a
        /// data preview. May be null for tables without one.</summary>
        public string PrimaryNameAttribute { get; set; }

        public bool HasStateStatus { get; set; }

        public bool SupportsCreateMultiple { get; set; }
        public bool SupportsUpdateMultiple { get; set; }

        public IReadOnlyList<AttributeSummary> Attributes { get; set; } = new List<AttributeSummary>();
        public IReadOnlyList<RelationshipSummary> Relationships { get; set; } = new List<RelationshipSummary>();
    }
}
