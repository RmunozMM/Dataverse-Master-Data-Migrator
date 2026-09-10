using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;

namespace DataverseMasterDataMigrator.Core.Migration
{
    /// <summary>
    /// Everything the engine needs for one run. Built by the XrmToolBox layer (it owns the real
    /// <see cref="IOrganizationService"/>-backed adapters); this class only sees the Core
    /// abstractions (ARCHITECTURE.md sección 1).
    /// </summary>
    public sealed class MigrationExecutionRequest
    {
        public MigrationProfile Profile { get; set; }
        public MigrationPlan Plan { get; set; }

        /// <summary>Metadata completa (con Attributes) de las tablas habilitadas, ya resuelta por
        /// la capa XrmToolBox antes de llamar al motor.</summary>
        public IReadOnlyDictionary<string, TableSummary> SourceTables { get; set; }
        public IReadOnlyDictionary<string, TableSummary> TargetTables { get; set; }

        public IDataverseRecordService SourceRecords { get; set; }
        public IDataverseRecordService TargetRecords { get; set; }

        /// <summary>Solo se usa para resolver, bajo demanda, el esquema de la tabla de
        /// intersección de una relación N:N (Pass 3b). Puede ser null si <see cref="MigrationProfile.Options"/>
        /// tiene <c>CreateManyToMany = false</c>.</summary>
        public IDataverseMetadataProvider SourceMetadata { get; set; }

        public IRecordTransformer Transformer { get; set; } = new IdentityRecordTransformer();

        public string SourceLabel { get; set; }
        public string TargetLabel { get; set; }

        public int PageSize { get; set; } = 500;
        public int WriteChunkSize { get; set; } = 200;

        public RetryPolicy RetryPolicy { get; set; } = new RetryPolicy();
        public IExecutionLogger Logger { get; set; }
        public ExecutionManifestStore ManifestStore { get; set; }

        /// <summary>
        /// Per-table set of Source record ids to skip entirely (never written) — populated from a
        /// manual record-by-record selection made in Data Preview. Null, or a table missing from
        /// this dictionary, means "migrate every record of that table" (the default, unchanged
        /// behavior). A record skipped here never shows up in <see cref="TableExecutionResult.SourceRecordCount"/>
        /// either, since it was never actually attempted.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyCollection<Guid>> ExcludedRecordIds { get; set; }
    }

    /// <summary>
    /// Signals a controlled abort of the current run (e.g. <see cref="ProfileOptions.StopOnError"/>
    /// tripped). Caught at the top of <see cref="MigrationExecutor.ExecuteAsync"/> — never bubbles
    /// out as an unhandled crash (sección 6 requirement: un perfil/ejecución nunca debe tumbar el plugin).
    /// </summary>
    internal sealed class MigrationAbortedException : Exception
    {
        public MigrationAbortedException(string message) : base(message) { }
    }

    /// <summary>
    /// The real multipass write engine (ARCHITECTURE.md sección 6-7):
    ///
    /// Pass 1 — create/update every enabled table, in dependency order, writing every writable
    ///          attribute except lookups whose target table hasn't been processed yet (those are
    ///          deferred) and statecode/statuscode (deferred to Pass 3).
    /// Pass 2 — targeted update of the lookups deferred in Pass 1 (self-references and cycle
    ///          members — the only cases where the target order isn't strictly earlier).
    /// Pass 3 — statecode/statuscode restoration, then N:N associations.
    ///
    /// Checkpoints are written after every write chunk (not just per table), via
    /// <see cref="ExecutionManifestStore"/>, so a crash mid-table never loses already-committed
    /// progress.
    /// </summary>
    public sealed class MigrationExecutor
    {
        public async Task<ExecutionManifest> ExecuteAsync(MigrationExecutionRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Profile == null) throw new ArgumentNullException(nameof(request.Profile));
            if (request.Plan == null) throw new ArgumentNullException(nameof(request.Plan));

            var manifest = new ExecutionManifest
            {
                ProfileId = request.Profile.Id,
                ProfileName = request.Profile.Name,
                SourceLabel = request.SourceLabel,
                TargetLabel = request.TargetLabel,
                Status = ExecutionStatus.Running,
                Tables = new List<TableExecutionResult>()
            };

            var log = request.Logger;
            var orderedSteps = request.Plan.Steps.OrderBy(s => s.Order).ToList();
            var orderByTable = orderedSteps.ToDictionary(s => s.LogicalName, s => s.Order, StringComparer.OrdinalIgnoreCase);
            var tableResults = new Dictionary<string, TableExecutionResult>(StringComparer.OrdinalIgnoreCase);
            var pendingByTable = new Dictionary<string, List<PendingRecordState>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var step in orderedSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await RunPass1ForTableAsync(request, step, orderByTable, manifest, tableResults, pendingByTable, log, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var step in orderedSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!pendingByTable.TryGetValue(step.LogicalName, out var pending) || pending.Count == 0) continue;
                    if (!request.TargetTables.TryGetValue(step.LogicalName, out var targetTable)) continue;

                    await RunPass2ForTableAsync(request, step.LogicalName, targetTable, pending, tableResults[step.LogicalName], manifest, log, cancellationToken)
                        .ConfigureAwait(false);
                }

                foreach (var step in orderedSteps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!pendingByTable.TryGetValue(step.LogicalName, out var pending) || pending.Count == 0) continue;
                    if (!request.TargetTables.TryGetValue(step.LogicalName, out var targetTable)) continue;

                    await RunStateStatusRestoreAsync(request, step.LogicalName, targetTable, pending, tableResults[step.LogicalName], manifest, log, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (request.Profile.Options.CreateManyToMany)
                {
                    await RunManyToManyAsync(request, orderedSteps, log, cancellationToken).ConfigureAwait(false);
                }

                var anyFailure = tableResults.Values.Any(t => t.Failed > 0);
                manifest.Status = anyFailure ? ExecutionStatus.CompletedWithWarnings : ExecutionStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                manifest.Status = ExecutionStatus.Cancelled;
                log?.LogWarning("Execution cancelled by user.");
            }
            catch (MigrationAbortedException ex)
            {
                manifest.Status = ExecutionStatus.Failed;
                log?.LogError("Execution stopped: " + ex.Message);
            }
            catch (Exception ex)
            {
                manifest.Status = ExecutionStatus.Failed;
                log?.LogError("Execution failed with an unexpected error: " + ex.Message);
            }
            finally
            {
                manifest.FinishedUtc = DateTime.UtcNow;
                request.ManifestStore?.Save(manifest);
            }

            return manifest;
        }

        /// <summary>
        /// Retries only the records marked <see cref="RecordOutcome.Failed"/> in <paramref name="manifest"/>
        /// (ARCHITECTURE.md sección 7) — never re-plans and never re-pages a table that already
        /// completed. <paramref name="request"/> must still carry <see cref="MigrationExecutionRequest.Plan"/>
        /// and the enabled tables' metadata (both cheap, in-memory/metadata-only to recompute —
        /// what this deliberately skips is re-reading each table's actual DATA).
        ///
        /// Each failed record is re-fetched from Source by id via <see cref="IDataverseRecordService.RetrieveByIdsAsync"/>
        /// and rebuilt with exactly the same per-pass rules Pass 1/2/3 used originally (manifest
        /// checkpoints never store the record payload itself — sección 20 — so there is nothing to
        /// resume from except the source data and the pass number it failed at).
        /// </summary>
        public async Task<ExecutionManifest> RetryFailedAsync(
            ExecutionManifest manifest,
            MigrationExecutionRequest request,
            CancellationToken cancellationToken)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var log = request.Logger;
            var orderByTable = request.Plan.Steps.ToDictionary(s => s.LogicalName, s => s.Order, StringComparer.OrdinalIgnoreCase);

            var failureGroups = manifest.Tables
                .SelectMany(t => t.Errors.Where(e => e.Outcome == RecordOutcome.Failed).Select(e => new { TableResult = t, Error = e }))
                .GroupBy(x => new { x.TableResult.LogicalName, x.Error.Pass })
                .ToList();

            if (failureGroups.Count == 0)
            {
                log?.LogInfo("Retry Failed: no failed records to retry.");
                return manifest;
            }

            manifest.Status = ExecutionStatus.Running;

            try
            {
                foreach (var group in failureGroups)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var logicalName = group.Key.LogicalName;
                    var pass = group.Key.Pass;
                    var tableResult = group.First().TableResult;
                    var ids = group.Select(x => x.Error.RecordId).Distinct().ToList();

                    if (!request.SourceTables.TryGetValue(logicalName, out var sourceTable) ||
                        !request.TargetTables.TryGetValue(logicalName, out var targetTable))
                    {
                        log?.LogWarning($"Retry Failed: '{logicalName}' metadata is no longer available; skipped {ids.Count} record(s).");
                        continue;
                    }

                    var entityConfig = request.Profile.Entities.FirstOrDefault(e =>
                        string.Equals(e.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase));
                    if (entityConfig == null) continue;

                    bool restoreState = request.Profile.Options.RestoreStateStatus && sourceTable.HasStateStatus;
                    var writableAttrs = GetWritableAttributes(sourceTable, entityConfig, restoreState);
                    var strategy = WriteStrategySelector.SelectFor(targetTable);
                    int stepOrder = orderByTable.TryGetValue(logicalName, out var o) ? o : int.MaxValue;

                    log?.LogInfo($"[Retry] {logicalName} (pass {pass}): re-fetching {ids.Count} failed record(s) from Source.");

                    var idsInGroup = new HashSet<Guid>(ids);
                    var removed = tableResult.Errors.RemoveAll(e => e.Pass == pass && idsInGroup.Contains(e.RecordId));
                    tableResult.Failed -= removed;

                    if (pass == 1)
                    {
                        var columns = writableAttrs.Select(a => a.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var sourceRecords = await request.SourceRecords.RetrieveByIdsAsync(logicalName, ids, columns, cancellationToken).ConfigureAwait(false);

                        var existenceCache = new Dictionary<DataReference, bool>();
                        sourceRecords = await RemoveSkipSilentlyLookupsAsync(request, writableAttrs, sourceRecords, existenceCache, cancellationToken).ConfigureAwait(false);

                        var splits = sourceRecords.Select(source =>
                            SplitForPass1(logicalName, stepOrder, orderByTable, writableAttrs, source, request.Transformer, sourceTable, request.Profile.Options.OwnerIdOverride)).ToList();

                        var batch = splits.Select(s => s.Write).ToList();
                        var results = await WriteWithRetryAsync(request, logicalName, batch, strategy, pass: 1, cancellationToken).ConfigureAwait(false);
                        ApplyResults(tableResult, results);
                        request.ManifestStore?.Save(manifest);

                        // A record whose Pass 1 retry just succeeded may still need its deferred
                        // lookups (Pass 2) or statecode/statuscode (Pass 3) applied — those were
                        // never attempted the first time around since Pass 1 itself had failed.
                        var succeededIds = new HashSet<Guid>(results.Where(r => r.Outcome == RecordOutcome.Succeeded).Select(r => r.RecordId));

                        var pass2Batch = splits
                            .Where(s => succeededIds.Contains(s.Write.Id) && s.Deferred.Count > 0)
                            .Select(s =>
                            {
                                var record = new DataRecord(logicalName, s.Write.Id);
                                foreach (var kvp in s.Deferred) record.Attributes[kvp.Key] = kvp.Value;
                                return record;
                            }).ToList();

                        if (pass2Batch.Count > 0)
                        {
                            var pass2Results = await WriteWithRetryAsync(request, logicalName, pass2Batch, strategy, pass: 2, cancellationToken).ConfigureAwait(false);
                            ApplyFollowUpResults(tableResult, pass2Results);
                        }

                        var pass3Batch = splits
                            .Where(s => succeededIds.Contains(s.Write.Id) && (s.StateCode != null || s.StatusCode != null))
                            .Select(s =>
                            {
                                var record = new DataRecord(logicalName, s.Write.Id);
                                if (s.StateCode != null) record.Attributes["statecode"] = s.StateCode;
                                if (s.StatusCode != null) record.Attributes["statuscode"] = s.StatusCode;
                                return record;
                            }).ToList();

                        if (pass3Batch.Count > 0)
                        {
                            var pass3Results = await WriteWithRetryAsync(request, logicalName, pass3Batch, strategy, pass: 3, cancellationToken).ConfigureAwait(false);
                            ApplyFollowUpResults(tableResult, pass3Results);
                        }
                    }
                    else if (pass == 2)
                    {
                        var lookupAttrs = writableAttrs.Where(a => a.Kind == AttributeKind.Lookup).ToList();
                        var columns = lookupAttrs.Select(a => a.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var sourceRecords = await request.SourceRecords.RetrieveByIdsAsync(logicalName, ids, columns, cancellationToken).ConfigureAwait(false);

                        var batch = new List<DataRecord>();
                        foreach (var source in sourceRecords)
                        {
                            var write = new DataRecord(logicalName, source.Id);
                            foreach (var attr in lookupAttrs)
                            {
                                if (!source.Attributes.TryGetValue(attr.LogicalName, out var value) || value == null) continue;
                                if (value is DataReference reference)
                                {
                                    bool resolvedNow = !orderByTable.TryGetValue(reference.LogicalName, out var targetOrder) || targetOrder < stepOrder;
                                    if (!resolvedNow) write.Attributes[attr.LogicalName] = value; // only the ones Pass 1 would have deferred belong here.
                                }
                            }
                            if (write.Attributes.Count > 0) batch.Add(write);
                        }

                        var results = await WriteWithRetryAsync(request, logicalName, batch, strategy, pass: 2, cancellationToken).ConfigureAwait(false);
                        ApplyFollowUpResults(tableResult, results);
                        request.ManifestStore?.Save(manifest);
                    }
                    else // pass 3 — statecode/statuscode restore.
                    {
                        var stateAttrs = writableAttrs.Where(a => a.Kind == AttributeKind.StateStatus).ToList();
                        var columns = stateAttrs.Select(a => a.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var sourceRecords = await request.SourceRecords.RetrieveByIdsAsync(logicalName, ids, columns, cancellationToken).ConfigureAwait(false);

                        var batch = new List<DataRecord>();
                        foreach (var source in sourceRecords)
                        {
                            var write = new DataRecord(logicalName, source.Id);
                            foreach (var attr in stateAttrs)
                            {
                                if (source.Attributes.TryGetValue(attr.LogicalName, out var value) && value != null)
                                    write.Attributes[attr.LogicalName] = value;
                            }
                            if (write.Attributes.Count > 0) batch.Add(write);
                        }

                        var results = await WriteWithRetryAsync(request, logicalName, batch, strategy, pass: 3, cancellationToken).ConfigureAwait(false);
                        ApplyFollowUpResults(tableResult, results);
                    }

                    request.ManifestStore?.Save(manifest);
                }

                var anyFailure = manifest.Tables.Any(t => t.Failed > 0);
                manifest.Status = anyFailure ? ExecutionStatus.CompletedWithWarnings : ExecutionStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                manifest.Status = ExecutionStatus.Cancelled;
                log?.LogWarning("Retry Failed cancelled by user.");
            }
            catch (Exception ex)
            {
                manifest.Status = ExecutionStatus.Failed;
                log?.LogError("Retry Failed stopped with an unexpected error: " + ex.Message);
            }
            finally
            {
                manifest.FinishedUtc = DateTime.UtcNow;
                request.ManifestStore?.Save(manifest);
            }

            return manifest;
        }

        // ---------------------------------------------------------------------------------
        // Pass 1
        // ---------------------------------------------------------------------------------

        private async Task RunPass1ForTableAsync(
            MigrationExecutionRequest request,
            PlanStep step,
            IReadOnlyDictionary<string, int> orderByTable,
            ExecutionManifest manifest,
            Dictionary<string, TableExecutionResult> tableResults,
            Dictionary<string, List<PendingRecordState>> pendingByTable,
            IExecutionLogger log,
            CancellationToken cancellationToken)
        {
            if (!request.SourceTables.TryGetValue(step.LogicalName, out var sourceTable))
                return;
            if (!request.TargetTables.TryGetValue(step.LogicalName, out var targetTable))
                return; // Preflight should already have blocked this; stay defensive rather than crash.

            var entityConfig = request.Profile.Entities.FirstOrDefault(e =>
                string.Equals(e.LogicalName, step.LogicalName, StringComparison.OrdinalIgnoreCase));
            if (entityConfig == null) return;

            var tableResult = new TableExecutionResult { LogicalName = step.LogicalName };
            tableResults[step.LogicalName] = tableResult;
            manifest.Tables.Add(tableResult);

            var pending = new List<PendingRecordState>();
            pendingByTable[step.LogicalName] = pending;

            bool restoreState = request.Profile.Options.RestoreStateStatus && sourceTable.HasStateStatus;
            var writableAttrs = GetWritableAttributes(sourceTable, entityConfig, restoreState);
            var columns = writableAttrs.Select(a => a.LogicalName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var strategy = WriteStrategySelector.SelectFor(targetTable);
            var stopwatch = Stopwatch.StartNew();

            log?.LogInfo($"[Pass 1] {step.LogicalName}: starting (strategy={strategy}).");

            IReadOnlyCollection<Guid> excludedIds = null;
            request.ExcludedRecordIds?.TryGetValue(step.LogicalName, out excludedIds);

            var existenceCache = new Dictionary<DataReference, bool>();

            string pageToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await request.SourceRecords
                    .RetrieveFilteredPageAsync(step.LogicalName, columns, entityConfig.Filter, pageToken, request.PageSize, cancellationToken)
                    .ConfigureAwait(false);
                pageToken = page.HasMore ? page.NextPageToken : null;

                // Records the user explicitly excluded in Data Preview never get counted or
                // attempted at all — they were never part of "what we tried to migrate".
                var pageRecords = (excludedIds == null || excludedIds.Count == 0)
                    ? page.Records
                    : page.Records.Where(r => !excludedIds.Contains(r.Id)).ToList();

                tableResult.SourceRecordCount += pageRecords.Count;

                pageRecords = await RemoveSkipSilentlyLookupsAsync(request, writableAttrs, pageRecords, existenceCache, cancellationToken).ConfigureAwait(false);

                foreach (var chunk in Chunk(pageRecords, request.WriteChunkSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var writeBatch = new List<DataRecord>(chunk.Count);
                    var chunkPending = new List<PendingRecordState>(chunk.Count);

                    foreach (var source in chunk)
                    {
                        var split = SplitForPass1(step.LogicalName, step.Order, orderByTable, writableAttrs, source, request.Transformer, sourceTable, request.Profile.Options.OwnerIdOverride);
                        writeBatch.Add(split.Write);
                        chunkPending.Add(new PendingRecordState
                        {
                            Id = source.Id,
                            DeferredLookups = split.Deferred,
                            StateCode = split.StateCode,
                            StatusCode = split.StatusCode
                        });
                    }

                    var results = await WriteWithRetryAsync(request, step.LogicalName, writeBatch, strategy, pass: 1, cancellationToken)
                        .ConfigureAwait(false);
                    ApplyResults(tableResult, results);

                    var succeeded = new HashSet<Guid>(results.Where(r => r.Outcome == RecordOutcome.Succeeded).Select(r => r.RecordId));
                    foreach (var p in chunkPending)
                    {
                        p.Pass1Succeeded = succeeded.Contains(p.Id);
                        pending.Add(p);
                    }

                    request.ManifestStore?.Save(manifest);

                    if (request.Profile.Options.StopOnError && results.Any(r => r.Outcome == RecordOutcome.Failed))
                    {
                        throw new MigrationAbortedException(
                            $"Table '{step.LogicalName}' produced a non-recoverable error and stopOnError is enabled.");
                    }
                }
            }
            while (pageToken != null);

            tableResult.Duration = stopwatch.Elapsed;
            tableResult.WriteStrategyUsed = strategy.ToString();
            log?.LogInfo($"[Pass 1] {step.LogicalName}: done — {tableResult.Created} created, {tableResult.Updated} updated, {tableResult.Failed} failed.");
        }

        // ---------------------------------------------------------------------------------
        // Pass 2 — deferred lookups
        // ---------------------------------------------------------------------------------

        private async Task RunPass2ForTableAsync(
            MigrationExecutionRequest request,
            string logicalName,
            TableSummary targetTable,
            List<PendingRecordState> pending,
            TableExecutionResult tableResult,
            ExecutionManifest manifest,
            IExecutionLogger log,
            CancellationToken cancellationToken)
        {
            var toUpdate = pending.Where(p => p.Pass1Succeeded && p.DeferredLookups.Count > 0).ToList();
            if (toUpdate.Count == 0) return;

            log?.LogInfo($"[Pass 2] {logicalName}: resolving {toUpdate.Count} deferred lookup(s).");
            var strategy = WriteStrategySelector.SelectFor(targetTable);

            foreach (var chunk in Chunk(toUpdate, request.WriteChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = chunk.Select(p =>
                {
                    var record = new DataRecord(logicalName, p.Id);
                    foreach (var kvp in p.DeferredLookups)
                        record.Attributes[kvp.Key] = kvp.Value;
                    return record;
                }).ToList();

                var results = await WriteWithRetryAsync(request, logicalName, batch, strategy, pass: 2, cancellationToken).ConfigureAwait(false);
                ApplyFollowUpResults(tableResult, results);
                request.ManifestStore?.Save(manifest);
            }
        }

        // ---------------------------------------------------------------------------------
        // Pass 3a — statecode/statuscode restore
        // ---------------------------------------------------------------------------------

        private async Task RunStateStatusRestoreAsync(
            MigrationExecutionRequest request,
            string logicalName,
            TableSummary targetTable,
            List<PendingRecordState> pending,
            TableExecutionResult tableResult,
            ExecutionManifest manifest,
            IExecutionLogger log,
            CancellationToken cancellationToken)
        {
            var toRestore = pending.Where(p => p.Pass1Succeeded && (p.StateCode != null || p.StatusCode != null)).ToList();
            if (toRestore.Count == 0) return;

            log?.LogInfo($"[Pass 3] {logicalName}: restoring statecode/statuscode for {toRestore.Count} record(s).");
            var strategy = WriteStrategySelector.SelectFor(targetTable);

            foreach (var chunk in Chunk(toRestore, request.WriteChunkSize))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var batch = chunk.Select(p =>
                {
                    var record = new DataRecord(logicalName, p.Id);
                    if (p.StateCode != null) record.Attributes["statecode"] = p.StateCode;
                    if (p.StatusCode != null) record.Attributes["statuscode"] = p.StatusCode;
                    return record;
                }).ToList();

                var results = await WriteWithRetryAsync(request, logicalName, batch, strategy, pass: 3, cancellationToken).ConfigureAwait(false);
                ApplyFollowUpResults(tableResult, results);
                request.ManifestStore?.Save(manifest);
            }
        }

        // ---------------------------------------------------------------------------------
        // Pass 3b — N:N associations
        // ---------------------------------------------------------------------------------

        private async Task RunManyToManyAsync(
            MigrationExecutionRequest request,
            IReadOnlyList<PlanStep> orderedSteps,
            IExecutionLogger log,
            CancellationToken cancellationToken)
        {
            if (request.SourceMetadata == null)
            {
                log?.LogWarning("[Pass 3] N:N associations skipped: no metadata provider configured.");
                return;
            }

            var processedRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var profileTables = new HashSet<string>(orderedSteps.Select(s => s.LogicalName), StringComparer.OrdinalIgnoreCase);

            foreach (var step in orderedSteps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!request.SourceTables.TryGetValue(step.LogicalName, out var sourceTable)) continue;

                foreach (var relationship in sourceTable.Relationships.Where(r => r.Kind == RelationshipKind.ManyToMany))
                {
                    if (!processedRelationships.Add(relationship.SchemaName)) continue;
                    if (!profileTables.Contains(relationship.Entity1LogicalName) || !profileTables.Contains(relationship.Entity2LogicalName))
                        continue; // el otro extremo no está en el perfil: sección 4, no se planifica ni se asocia.

                    try
                    {
                        await AssociateRelationshipAsync(request, relationship, log, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        log?.LogWarning($"[Pass 3] N:N '{relationship.SchemaName}' skipped after an error: {ex.Message}");
                    }
                }
            }
        }

        private async Task AssociateRelationshipAsync(
            MigrationExecutionRequest request,
            RelationshipSummary relationship,
            IExecutionLogger log,
            CancellationToken cancellationToken)
        {
            var intersectDetail = await request.SourceMetadata
                .GetTableDetailAsync(relationship.IntersectEntityName, cancellationToken)
                .ConfigureAwait(false);

            var col1 = intersectDetail.Attributes.FirstOrDefault(a =>
                a.Kind == AttributeKind.Lookup && a.LookupTargets.Contains(relationship.Entity1LogicalName, StringComparer.OrdinalIgnoreCase));
            var col2 = intersectDetail.Attributes.FirstOrDefault(a =>
                a.Kind == AttributeKind.Lookup && a.LookupTargets.Contains(relationship.Entity2LogicalName, StringComparer.OrdinalIgnoreCase));

            if (col1 == null || col2 == null)
            {
                log?.LogWarning($"[Pass 3] N:N '{relationship.SchemaName}': could not identify the two FK columns on " +
                                 $"intersect entity '{relationship.IntersectEntityName}'; skipped.");
                return;
            }

            var columns = new[] { col1.LogicalName, col2.LogicalName };
            var groups = new Dictionary<Guid, List<DataReference>>();

            string pageToken = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await request.SourceRecords
                    .RetrievePageAsync(relationship.IntersectEntityName, columns, pageToken, request.PageSize, cancellationToken)
                    .ConfigureAwait(false);
                pageToken = page.HasMore ? page.NextPageToken : null;

                foreach (var row in page.Records)
                {
                    if (!row.TryGetValue<DataReference>(col1.LogicalName, out var from) ||
                        !row.TryGetValue<DataReference>(col2.LogicalName, out var to))
                        continue;

                    if (!groups.TryGetValue(from.Id, out var list))
                        groups[from.Id] = list = new List<DataReference>();
                    list.Add(to);
                }
            }
            while (pageToken != null);

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await request.TargetRecords
                        .AssociateAsync(relationship.SchemaName, new DataReference(relationship.Entity1LogicalName, group.Key), group.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log?.LogWarning($"[Pass 3] N:N '{relationship.SchemaName}': association for {group.Key} failed: {ex.Message}");
                }
            }
        }

        // ---------------------------------------------------------------------------------
        // Shared helpers — also reused by RetryFailedAsync below, so a retry rebuilds each
        // record's write payload with exactly the same rules Pass 1 used the first time.
        // ---------------------------------------------------------------------------------

        private static List<AttributeSummary> GetWritableAttributes(TableSummary sourceTable, ProfileEntity entityConfig, bool restoreState) =>
            AttributeWritabilityRules.GetWritableAttributes(sourceTable, entityConfig, restoreState);

        /// <summary>Removes external-lookup attribute values from records when they're confirmed
        /// missing in Target AND the relevant policy (Required/OptionalLookupPolicy, per whether the
        /// attribute itself is required) is SkipSilently — so Pass 1's existing "attribute absent from
        /// the record = nothing to write for it" handling (see SplitForPass1) quietly omits just that
        /// one field instead of the whole record failing at Dataverse's own validation. A pure no-op
        /// (returns the input list itself, unchanged) unless the profile actually opted into
        /// SkipSilently for at least one of the two policies - existing behavior for every profile that
        /// hasn't is completely unaffected.</summary>
        private static async Task<IReadOnlyList<DataRecord>> RemoveSkipSilentlyLookupsAsync(
            MigrationExecutionRequest request,
            IReadOnlyList<AttributeSummary> writableAttrs,
            IReadOnlyList<DataRecord> records,
            Dictionary<DataReference, bool> existenceCache,
            CancellationToken cancellationToken)
        {
            var options = request.Profile.Options;
            if (options.OptionalLookupPolicy != LookupPolicy.SkipSilently && options.RequiredLookupPolicy != LookupPolicy.SkipSilently)
                return records; // fast path: no profile has opted into this, zero behavior/perf change

            var profileTables = new HashSet<string>(request.SourceTables.Keys, StringComparer.OrdinalIgnoreCase);
            var externalLookupAttrs = writableAttrs
                .Where(a => a.Kind == AttributeKind.Lookup)
                .Where(a => a.LookupTargets.Any(t => !profileTables.Contains(t)))
                .ToList();
            if (externalLookupAttrs.Count == 0) return records;

            var result = new List<DataRecord>(records.Count);
            foreach (var record in records)
            {
                DataRecord filtered = null; // clone lazily, only if something actually gets removed
                foreach (var attr in externalLookupAttrs)
                {
                    if (!record.Attributes.TryGetValue(attr.LogicalName, out var value) || !(value is DataReference reference))
                        continue;

                    // A genuinely polymorphic/unresolvable reference — seen in practice on
                    // some Microsoft-managed system tables (msdyn_* Copilot/AI Builder
                    // "regarding"-style fields with no specific declared target type) —
                    // carries the literal placeholder "entity" as its LogicalName instead
                    // of a real table name. Dataverse's Retrieve rejects that name outright
                    // ("The 'Retrieve' method does not support entities of type 'entity'"),
                    // which would otherwise crash the entire migration run over a single
                    // unresolvable value (see ExternalLookupSampler for the same guard during
                    // Preflight). There's no real table to check existence against here, so
                    // skip it — same treatment as any other reference we simply can't evaluate.
                    if (string.IsNullOrEmpty(reference.LogicalName) ||
                        string.Equals(reference.LogicalName, "entity", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (profileTables.Contains(reference.LogicalName)) continue; // this value's actual target is in-profile after all

                    var policy = attr.IsRequired ? options.RequiredLookupPolicy : options.OptionalLookupPolicy;
                    if (policy != LookupPolicy.SkipSilently) continue;

                    cancellationToken.ThrowIfCancellationRequested();
                    if (!existenceCache.TryGetValue(reference, out var exists))
                    {
                        exists = await request.TargetRecords.ExistsAsync(reference, cancellationToken).ConfigureAwait(false);
                        existenceCache[reference] = exists;
                    }
                    if (exists) continue;

                    if (filtered == null)
                    {
                        filtered = new DataRecord(record.LogicalName, record.Id);
                        foreach (var kvp in record.Attributes) filtered.Attributes[kvp.Key] = kvp.Value;
                    }
                    filtered.Attributes.Remove(attr.LogicalName);
                }
                result.Add(filtered ?? record);
            }
            return result;
        }

        private sealed class SplitResult
        {
            public DataRecord Write;
            public Dictionary<string, object> Deferred;
            public object StateCode;
            public object StatusCode;
        }

        private static SplitResult SplitForPass1(
            string logicalName,
            int stepOrder,
            IReadOnlyDictionary<string, int> orderByTable,
            IReadOnlyList<AttributeSummary> writableAttrs,
            DataRecord source,
            IRecordTransformer transformer,
            TableSummary sourceTable,
            Guid? ownerIdOverride)
        {
            var write = new DataRecord(logicalName, source.Id);
            var deferred = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            object stateVal = null, statusVal = null;

            foreach (var attr in writableAttrs)
            {
                if (attr.Kind == AttributeKind.StateStatus)
                {
                    if (!source.Attributes.TryGetValue(attr.LogicalName, out var sv) || sv == null) continue;
                    if (string.Equals(attr.LogicalName, "statecode", StringComparison.OrdinalIgnoreCase)) stateVal = sv;
                    else statusVal = sv;
                    continue; // never written in Pass 1 — see RunStateStatusRestoreAsync.
                }

                if (!source.Attributes.TryGetValue(attr.LogicalName, out var value) || value == null)
                    continue;

                if (attr.Kind == AttributeKind.Lookup && value is DataReference reference)
                {
                    // Resolvable now if the referenced table either isn't part of this plan
                    // (external lookup — sección 12, checked at runtime by Dataverse itself: an
                    // invalid GUID just fails the write) or was already fully processed by an
                    // earlier plan step. Kahn's algorithm guarantees any genuine (non-cycle)
                    // dependency edge already has a strictly lower order, so this single
                    // comparison also correctly defers self-references and cycle members to
                    // Pass 2 without needing to consult CycleDetector's output directly here.
                    bool resolvedNow = !orderByTable.TryGetValue(reference.LogicalName, out var targetOrder)
                        || targetOrder < stepOrder;

                    if (!resolvedNow)
                    {
                        deferred[attr.LogicalName] = value;
                        continue;
                    }
                }

                write.Attributes[attr.LogicalName] = value;
            }

            if (ownerIdOverride.HasValue)
            {
                var ownerAttr = sourceTable.Attributes.FirstOrDefault(a => a.IsOwnerLookup);
                if (ownerAttr != null)
                    write.Attributes[ownerAttr.LogicalName] = new DataReference("systemuser", ownerIdOverride.Value);
            }

            return new SplitResult
            {
                Write = transformer.Transform(write, sourceTable),
                Deferred = deferred,
                StateCode = stateVal,
                StatusCode = statusVal
            };
        }

        private static async Task<List<RecordOperationResult>> WriteWithRetryAsync(
            MigrationExecutionRequest request,
            string logicalName,
            IReadOnlyList<DataRecord> batch,
            WriteStrategy strategy,
            int pass,
            CancellationToken cancellationToken)
        {
            var finalResults = new Dictionary<Guid, RecordOperationResult>();
            IReadOnlyList<DataRecord> remaining = batch;
            int attempt = 1;

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var attemptResults = await request.TargetRecords
                    .WriteBatchAsync(logicalName, remaining, strategy, pass, cancellationToken)
                    .ConfigureAwait(false);

                var toRetry = new List<DataRecord>();
                TimeSpan? waitDelay = null;

                foreach (var result in attemptResults)
                {
                    result.RetryCount = attempt - 1;

                    if (result.Outcome != RecordOutcome.Failed)
                    {
                        finalResults[result.RecordId] = result;
                        continue;
                    }

                    var decision = request.RetryPolicy.Evaluate(new RetryContext
                    {
                        AttemptNumber = attempt,
                        IsTransient = result.IsTransient,
                        RetryAfter = result.RetryAfterHint
                    });

                    if (decision.ShouldRetry)
                    {
                        var record = remaining.First(r => r.Id == result.RecordId);
                        toRetry.Add(record);
                        if (waitDelay == null || decision.Delay > waitDelay.Value)
                            waitDelay = decision.Delay;
                    }
                    else
                    {
                        finalResults[result.RecordId] = result;
                    }
                }

                if (toRetry.Count == 0) break;

                if (waitDelay.HasValue && waitDelay.Value > TimeSpan.Zero)
                    await Task.Delay(waitDelay.Value, cancellationToken).ConfigureAwait(false);

                remaining = toRetry;
                attempt++;
            }

            return batch
                .Select(r => finalResults.TryGetValue(r.Id, out var result)
                    ? result
                    : new RecordOperationResult { RecordId = r.Id, TableLogicalName = logicalName, Outcome = RecordOutcome.Skipped, Pass = pass })
                .ToList();
        }

        private static void ApplyResults(TableExecutionResult tableResult, IReadOnlyList<RecordOperationResult> results)
        {
            foreach (var r in results)
            {
                switch (r.Outcome)
                {
                    case RecordOutcome.Succeeded:
                        if (r.Operation == RecordOperation.Update) tableResult.Updated++;
                        else tableResult.Created++;
                        break;
                    case RecordOutcome.Failed:
                        tableResult.Failed++;
                        tableResult.Errors.Add(r);
                        break;
                }
            }
        }

        /// <summary>
        /// For Pass 2 (deferred lookups) and Pass 3 (statecode/statuscode restore) results only —
        /// every record they touch was already counted as Created or Updated during Pass 1, so a
        /// success here must NOT be counted again. (Real bug this fixes: the adapter's
        /// ExecuteMultipleUpsert strategy can't distinguish create-vs-update in its response and
        /// always reports <see cref="RecordOperation.Create"/> for every success — reusing
        /// <see cref="ApplyResults"/> for Pass 2/3 was silently doubling the Created count, e.g. 32
        /// source records showing up as "64 created" once Pass 3's statecode/statuscode restore
        /// ran against those same 32 records.) A failure here is still real and actionable —
        /// Retry Failed needs it — so failures are tracked exactly as before.
        /// </summary>
        private static void ApplyFollowUpResults(TableExecutionResult tableResult, IReadOnlyList<RecordOperationResult> results)
        {
            foreach (var r in results)
            {
                if (r.Outcome == RecordOutcome.Failed)
                {
                    tableResult.Failed++;
                    tableResult.Errors.Add(r);
                }
            }
        }

        private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
        {
            for (int i = 0; i < source.Count; i += size)
                yield return source.Skip(i).Take(size).ToList();
        }

        private sealed class PendingRecordState
        {
            public Guid Id;
            public IDictionary<string, object> DeferredLookups;
            public object StateCode;
            public object StatusCode;
            public bool Pass1Succeeded;
        }
    }
}
