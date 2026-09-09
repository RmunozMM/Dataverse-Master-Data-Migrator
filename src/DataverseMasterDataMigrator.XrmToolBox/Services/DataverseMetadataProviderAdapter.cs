using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Crm.Sdk.Messages;

namespace DataverseMasterDataMigrator.XrmToolBox.Services
{
    /// <summary>
    /// Implements <see cref="IDataverseMetadataProvider"/> against a real
    /// <see cref="IOrganizationService"/>. One instance per connection (Source or Target) —
    /// whoever constructs it decides which <see cref="IOrganizationService"/> to hand it.
    /// </summary>
    public sealed class DataverseMetadataProviderAdapter : IDataverseMetadataProvider
    {
        private readonly IOrganizationService _service;

        public DataverseMetadataProviderAdapter(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public Task<IReadOnlyList<TableSummary>> ListTablesAsync(CancellationToken cancellationToken)
        {
            // Deliberately light: EntityFilters.Entity only, no Attributes/Relationships
            // (section 8: "do not load heavy metadata unnecessarily at startup"). This is the
            // same lesson documented in PLAN_CORRECCION_CRASH_EXPORT.md — don't request
            // EntityFilters.All when a narrower filter covers what's actually used.
            cancellationToken.ThrowIfCancellationRequested();

            var request = new RetrieveAllEntitiesRequest
            {
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveAllEntitiesResponse)_service.Execute(request);

            var result = response.EntityMetadata
                .Where(e => e.IsValidForAdvancedFind.GetValueOrDefault(false)
                            || e.IsCustomEntity.GetValueOrDefault(false))
                .Select(MapLight)
                .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Task.FromResult<IReadOnlyList<TableSummary>>(result);
        }

        public Task<TableSummary> GetTableDetailAsync(string logicalName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new RetrieveEntityRequest
            {
                LogicalName = logicalName,
                // Attributes + Relationships only — no Privileges, which the plan never uses
                // (same fix already applied to Metadata Dataverse Document's exporter).
                EntityFilters = EntityFilters.Entity | EntityFilters.Attributes | EntityFilters.Relationships,
                RetrieveAsIfPublished = true
            };

            var response = (RetrieveEntityResponse)_service.Execute(request);
            var full = MapFull(response.EntityMetadata);
            return Task.FromResult(full);
        }

        public Task<string> GetOrganizationIdAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = (WhoAmIResponse)_service.Execute(new WhoAmIRequest());
            return Task.FromResult(response.OrganizationId.ToString("D"));
        }

        private static TableSummary MapLight(EntityMetadata e) => new TableSummary
        {
            LogicalName = e.LogicalName,
            DisplayName = e.DisplayName?.UserLocalizedLabel?.Label ?? e.LogicalName,
            SchemaName = e.SchemaName,
            IsCustomEntity = e.IsCustomEntity.GetValueOrDefault(false),
            PrimaryIdAttribute = e.PrimaryIdAttribute,
            PrimaryNameAttribute = e.PrimaryNameAttribute,
            HasStateStatus = e.Attributes?.Any(a =>
                string.Equals(a.LogicalName, "statecode", StringComparison.OrdinalIgnoreCase)) ?? false
        };

        private static TableSummary MapFull(EntityMetadata e)
        {
            var summary = MapLight(e);

            // EntityMetadata.IsCreateMultipleSupported / IsUpdateMultipleSupported were added by
            // Microsoft to expose bulk-message support per table in a CoreAssemblies release
            // newer than the one referenced from lib/ (verified by disassembly — not present on
            // this Microsoft.Xrm.Sdk.dll). Left as false below; see the SupportsCreateMultiple /
            // SupportsUpdateMultiple assignment further down for what that means in practice.
            var attributes = (e.Attributes ?? Array.Empty<AttributeMetadata>())
                .Select(MapAttribute)
                .ToList();

            var relationships = new List<RelationshipSummary>();
            relationships.AddRange((e.OneToManyRelationships ?? Array.Empty<OneToManyRelationshipMetadata>())
                .Select(r => new RelationshipSummary
                {
                    SchemaName = r.SchemaName,
                    Kind = RelationshipKind.OneToMany,
                    ReferencingEntity = r.ReferencingEntity,
                    ReferencingAttribute = r.ReferencingAttribute,
                    ReferencedEntity = r.ReferencedEntity
                }));
            relationships.AddRange((e.ManyToOneRelationships ?? Array.Empty<OneToManyRelationshipMetadata>())
                .Select(r => new RelationshipSummary
                {
                    SchemaName = r.SchemaName,
                    Kind = RelationshipKind.ManyToOne,
                    ReferencingEntity = r.ReferencingEntity,
                    ReferencingAttribute = r.ReferencingAttribute,
                    ReferencedEntity = r.ReferencedEntity
                }));
            relationships.AddRange((e.ManyToManyRelationships ?? Array.Empty<ManyToManyRelationshipMetadata>())
                .Select(r => new RelationshipSummary
                {
                    SchemaName = r.SchemaName,
                    Kind = RelationshipKind.ManyToMany,
                    Entity1LogicalName = r.Entity1LogicalName,
                    Entity2LogicalName = r.Entity2LogicalName,
                    IntersectEntityName = r.IntersectEntityName
                }));

            return new TableSummary
            {
                LogicalName = summary.LogicalName,
                DisplayName = summary.DisplayName,
                SchemaName = summary.SchemaName,
                IsCustomEntity = summary.IsCustomEntity,
                PrimaryIdAttribute = summary.PrimaryIdAttribute,
                PrimaryNameAttribute = summary.PrimaryNameAttribute,
                HasStateStatus = summary.HasStateStatus,
                SupportsCreateMultiple = false, // see note in MapFull: not exposed by this SDK version
                SupportsUpdateMultiple = false, // WriteStrategySelector safely falls back to ExecuteMultipleUpsert
                Attributes = attributes,
                Relationships = relationships
            };
        }

        private static AttributeSummary MapAttribute(AttributeMetadata a)
        {
            var kind = AttributeKind.Primitive;
            var lookupTargets = Array.Empty<string>();

            if (a is LookupAttributeMetadata lookup)
            {
                kind = AttributeKind.Lookup;
                lookupTargets = lookup.Targets ?? Array.Empty<string>();
            }
            else if (string.Equals(a.LogicalName, "statecode", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(a.LogicalName, "statuscode", StringComparison.OrdinalIgnoreCase))
            {
                kind = AttributeKind.StateStatus;
            }
            else if (a.AttributeOf != null)
            {
                // A "virtual" companion attribute (e.g. the text label of an OptionSet) —
                // not something we should attempt to write back (section 10: exclude virtual
                // fields from migration).
                kind = AttributeKind.Virtual;
            }

            return new AttributeSummary
            {
                LogicalName = a.LogicalName,
                DisplayName = a.DisplayName?.UserLocalizedLabel?.Label ?? a.LogicalName,
                Kind = kind,
                IsValidForCreate = a.IsValidForCreate.GetValueOrDefault(false),
                IsValidForUpdate = a.IsValidForUpdate.GetValueOrDefault(false),
                LookupTargets = lookupTargets,
                RequiredLevel = a.RequiredLevel?.Value.ToString() ?? "None"
            };
        }
    }
}
