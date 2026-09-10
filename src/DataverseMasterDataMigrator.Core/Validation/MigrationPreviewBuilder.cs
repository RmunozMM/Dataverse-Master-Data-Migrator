using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;

namespace DataverseMasterDataMigrator.Core.Validation
{
    public enum RecordPreviewState
    {
        /// <summary>Id doesn't exist in Target yet — Execute would create it.</summary>
        New,

        /// <summary>Id already exists in Target — Execute would update it.</summary>
        Update
    }

    public sealed class RecordPreviewRow
    {
        public Guid Id { get; set; }

        /// <summary>Best-effort human label — the table's primary name attribute value when the
        /// table has one and it was populated; falls back to the raw id otherwise.</summary>
        public string DisplayName { get; set; }

        /// <summary>Target's CURRENT value for the same name attribute — null when <see cref="State"/>
        /// is <see cref="RecordPreviewState.New"/> (nothing to show yet). Lets the UI show a
        /// side-by-side Source/Target comparison instead of just a New/Update flag.</summary>
        public string TargetDisplayName { get; set; }

        public RecordPreviewState State { get; set; }
    }

    public sealed class TableDataPreview
    {
        public string LogicalName { get; set; }

        /// <summary>The table's friendly label (e.g. "Tipo de Contacto") — null if unavailable.
        /// Shown alongside <see cref="LogicalName"/> so the table picker matches the same
        /// schema-name + display-name convention used elsewhere in the plugin (Tables tab).</summary>
        public string DisplayName { get; set; }

        public int SourceRecordCount { get; set; }
        public int ToCreate { get; set; }
        public int ToUpdate { get; set; }

        /// <summary>Per-record detail, capped at the request's <c>maxRecordsPerTable</c> — see
        /// <see cref="Truncated"/> when there were more records than that.</summary>
        public IReadOnlyList<RecordPreviewRow> Records { get; set; } = new List<RecordPreviewRow>();

        /// <summary>True when <see cref="SourceRecordCount"/> exceeds how many rows were actually
        /// kept in <see cref="Records"/> (the aggregate counts above are still exact — this only
        /// caps the level of per-record detail kept in memory for display).</summary>
        public bool Truncated { get; set; }
    }

    /// <summary>
    /// Read-only: for each table in the plan, classifies every Source record as New or Update
    /// given the profile's current options (V1 is always Upsert with preserveSourceGuid — a
    /// record is an update if its id already exists in Target, a create otherwise). Answers "what
    /// will actually happen, to which records" before Execute, without writing anything.
    /// </summary>
    public sealed class MigrationPreviewBuilder
    {
        public async Task<IReadOnlyList<TableDataPreview>> BuildAsync(
            MigrationPlan plan,
            IReadOnlyDictionary<string, TableSummary> sourceTables,
            IDataverseRecordService sourceRecords,
            IDataverseRecordService targetRecords,
            int pageSize,
            int maxRecordsPerTable,
            CancellationToken cancellationToken,
            Action<string> onProgress = null,
            IReadOnlyDictionary<string, RecordFilter> entityFilters = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (sourceTables == null) throw new ArgumentNullException(nameof(sourceTables));
            if (sourceRecords == null) throw new ArgumentNullException(nameof(sourceRecords));
            if (targetRecords == null) throw new ArgumentNullException(nameof(targetRecords));

            var results = new List<TableDataPreview>();
            var orderedSteps = plan.Steps.OrderBy(s => s.Order).ToList();

            for (int stepIndex = 0; stepIndex < orderedSteps.Count; stepIndex++)
            {
                var step = orderedSteps[stepIndex];
                cancellationToken.ThrowIfCancellationRequested();
                onProgress?.Invoke($"Reading {step.LogicalName} from Source and Target... ({stepIndex + 1}/{orderedSteps.Count})");

                sourceTables.TryGetValue(step.LogicalName, out var table);
                var nameAttribute = table?.PrimaryNameAttribute;
                var columns = nameAttribute != null ? new[] { nameAttribute } : Array.Empty<string>();

                RecordFilter filter = null;
                entityFilters?.TryGetValue(step.LogicalName, out filter);

                var allRecords = new List<DataRecord>();
                string pageToken = null;
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var page = await sourceRecords
                        .RetrieveFilteredPageAsync(step.LogicalName, columns, filter, pageToken, pageSize, cancellationToken)
                        .ConfigureAwait(false);
                    pageToken = page.HasMore ? page.NextPageToken : null;
                    allRecords.AddRange(page.Records);
                }
                while (pageToken != null);

                var allIds = allRecords.Select(r => r.Id).ToList();
                var existingById = new Dictionary<Guid, DataRecord>();
                if (allIds.Count > 0)
                {
                    // Fetches the same name column from Target too (not just existence) so the UI
                    // can show a real side-by-side Source/Target comparison, not just a flag.
                    var existingInTarget = await targetRecords
                        .RetrieveByIdsAsync(step.LogicalName, allIds, columns, cancellationToken)
                        .ConfigureAwait(false);
                    existingById = existingInTarget.ToDictionary(r => r.Id);
                }

                var kept = maxRecordsPerTable > 0 ? allRecords.Take(maxRecordsPerTable) : allRecords;
                var rows = kept.Select(r =>
                {
                    var isUpdate = existingById.TryGetValue(r.Id, out var targetRecord);
                    string targetName = (isUpdate && nameAttribute != null && targetRecord.Attributes.TryGetValue(nameAttribute, out var tv) && tv != null)
                        ? tv.ToString()
                        : null;

                    return new RecordPreviewRow
                    {
                        Id = r.Id,
                        DisplayName = (nameAttribute != null && r.Attributes.TryGetValue(nameAttribute, out var v) && v != null)
                            ? v.ToString()
                            : r.Id.ToString(),
                        TargetDisplayName = targetName,
                        State = isUpdate ? RecordPreviewState.Update : RecordPreviewState.New
                    };
                }).ToList();

                results.Add(new TableDataPreview
                {
                    LogicalName = step.LogicalName,
                    DisplayName = table?.DisplayName,
                    SourceRecordCount = allRecords.Count,
                    ToUpdate = existingById.Count,
                    ToCreate = allRecords.Count - existingById.Count,
                    Records = rows,
                    Truncated = maxRecordsPerTable > 0 && allRecords.Count > maxRecordsPerTable
                });
            }

            return results;
        }
    }
}
