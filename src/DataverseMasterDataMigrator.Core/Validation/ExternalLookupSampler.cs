using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Validation
{
    /// <summary>
    /// Fills in <see cref="PreflightContext.UnresolvedExternalLookups"/> for real (ARCHITECTURE.md
    /// sección 12): for every lookup attribute whose target table isn't part of the profile, pages
    /// through Source, and for every distinct referenced GUID checks whether it exists in Target —
    /// once per distinct GUID, never once per record, since the same external record is typically
    /// referenced by many rows.
    /// </summary>
    public sealed class ExternalLookupSampler
    {
        public async Task<IReadOnlyList<UnresolvedLookupFinding>> SampleAsync(
            MigrationProfile profile,
            IReadOnlyDictionary<string, TableSummary> sourceTables,
            IDataverseRecordService sourceRecords,
            IDataverseRecordService targetRecords,
            int pageSize,
            CancellationToken cancellationToken,
            Action<string> onProgress = null)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (sourceTables == null) throw new ArgumentNullException(nameof(sourceTables));
            if (sourceRecords == null) throw new ArgumentNullException(nameof(sourceRecords));
            if (targetRecords == null) throw new ArgumentNullException(nameof(targetRecords));

            var profileTables = new HashSet<string>(sourceTables.Keys, StringComparer.OrdinalIgnoreCase);
            var findings = new List<UnresolvedLookupFinding>();

            foreach (var entity in profile.Entities.Where(e => e.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sourceTables.TryGetValue(entity.LogicalName, out var table)) continue;

                var excluded = new HashSet<string>(entity.ExcludedAttributes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

                // A polymorphic lookup (e.g. customerid) can have SOME possible targets inside the
                // profile and others outside it. Whether one particular value counts as "external"
                // is decided per-record below, from the actual DataReference.LogicalName it carries
                // — LookupTargets here is only used to decide whether the attribute is worth
                // sampling at all (skip it entirely if every possible target is already in-profile).
                var externalLookups = table.Attributes
                    .Where(a => a.Kind == AttributeKind.Lookup)
                    // Matches MigrationExecutor.GetWritableAttributes exactly: a system-managed
                    // lookup (createdby, modifiedby, organizationid, ...) is never valid for
                    // create/update, so the executor never attempts to write it — warning about
                    // it here would just be noise about a value that will never actually be
                    // touched (real feedback: this confused a user testing against a profile
                    // where every such warning turned out to be one of these fields).
                    .Where(a => a.IsValidForCreate || a.IsValidForUpdate)
                    // Same reasoning as AttributeWritabilityRules.GetWritableAttributes: an
                    // attribute that Dataverse won't even let us READ can't be sampled at all —
                    // requesting it in a ColumnSet is rejected outright.
                    .Where(a => a.IsValidForRead)
                    // ownerid (and any equivalent Owner-type lookup) IS writable, unlike
                    // createdby/modifiedby, but the executor still excludes it (IsOwnerLookup) —
                    // a Source user/team GUID essentially never exists in Target. Without this,
                    // ownerid showed up as a blocking REQUIRED_LOOKUP_UNRESOLVED error on every
                    // table (real feedback), even though the actual write never touches it.
                    .Where(a => !a.IsOwnerLookup)
                    .Where(a => !excluded.Contains(a.LogicalName))
                    .Where(a => a.LookupTargets.Any(t => !profileTables.Contains(t)))
                    .ToList();

                if (externalLookups.Count == 0) continue;

                onProgress?.Invoke($"Sampling external lookups: {entity.DisplayName ?? entity.LogicalName}...");

                var columns = externalLookups.Select(a => a.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var existenceCache = new Dictionary<DataReference, bool>();
                var missingCountByAttribute = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                string pageToken = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await sourceRecords
                        .RetrieveFilteredPageAsync(entity.LogicalName, columns, entity.Filter, pageToken, pageSize, cancellationToken)
                        .ConfigureAwait(false);
                    pageToken = page.HasMore ? page.NextPageToken : null;

                    foreach (var record in page.Records)
                    {
                        foreach (var attr in externalLookups)
                        {
                            if (!record.TryGetValue<DataReference>(attr.LogicalName, out var reference)) continue;

                            // A genuinely polymorphic/unresolvable reference — seen in practice on
                            // some Microsoft-managed system tables (msdyn_* Copilot/AI Builder
                            // "regarding"-style fields with no specific declared target type) —
                            // carries the literal placeholder "entity" as its LogicalName instead
                            // of a real table name. Dataverse's Retrieve rejects that name outright
                            // ("The 'Retrieve' method does not support entities of type 'entity'"),
                            // which used to crash the entire Preflight run over a single
                            // unresolvable value. There's no real table to check existence
                            // against here, so skip it — same treatment as any other reference we
                            // simply can't evaluate.
                            if (string.IsNullOrEmpty(reference.LogicalName) ||
                                string.Equals(reference.LogicalName, "entity", StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (profileTables.Contains(reference.LogicalName)) continue; // this value's actual target is in-profile after all.

                            if (!existenceCache.TryGetValue(reference, out var exists))
                            {
                                exists = await targetRecords.ExistsAsync(reference, cancellationToken).ConfigureAwait(false);
                                existenceCache[reference] = exists;
                            }

                            if (!exists)
                            {
                                missingCountByAttribute.TryGetValue(attr.LogicalName, out var count);
                                missingCountByAttribute[attr.LogicalName] = count + 1;
                            }
                        }
                    }
                }
                while (pageToken != null);

                foreach (var attr in externalLookups)
                {
                    if (missingCountByAttribute.TryGetValue(attr.LogicalName, out var count) && count > 0)
                    {
                        findings.Add(new UnresolvedLookupFinding
                        {
                            TableLogicalName = entity.LogicalName,
                            AttributeLogicalName = attr.LogicalName,
                            IsRequired = attr.IsRequired,
                            AffectedRecordCount = count
                        });
                    }
                }
            }

            return findings;
        }
    }
}
