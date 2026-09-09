using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages;

namespace DataverseMasterDataMigrator.XrmToolBox.Services
{
    /// <summary>
    /// Implements <see cref="IDataverseRecordService"/> against a real
    /// <see cref="IOrganizationService"/>.
    ///
    /// A note on <see cref="WriteStrategy.BulkCreateThenUpdate"/>: CreateMultipleRequest and
    /// UpdateMultipleRequest live in a version of the SDK newer than the one referenced by
    /// Metadata Dataverse Document's lib/ folder (they were added for the "bulk operations"
    /// wave). Verify they resolve against your installed Microsoft.Xrm.Sdk.dll /
    /// Microsoft.Crm.Sdk.Proxy.dll before relying on this path — if they don't exist in your
    /// SDK version, WriteStrategySelector will still work correctly by falling back to
    /// ExecuteMultipleUpsert for every table (see WriteStrategySelector.SelectFor), so nothing
    /// breaks; you simply lose the bulk-message performance advantage until the SDK reference is
    /// updated.
    /// </summary>
    public sealed class DataverseRecordServiceAdapter : IDataverseRecordService
    {
        private const int DefaultPageSize = 500;
        private readonly IOrganizationService _service;

        public DataverseRecordServiceAdapter(IOrganizationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public Task<RecordPage> RetrievePageAsync(
            string logicalName,
            IReadOnlyList<string> columns,
            string pageToken,
            int pageSize,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var effectivePageSize = pageSize > 0 ? pageSize : DefaultPageSize;
            var query = new QueryExpression(logicalName)
            {
                ColumnSet = new ColumnSet(columns.ToArray()),
                PageInfo = new PagingInfo
                {
                    Count = effectivePageSize,
                    PageNumber = 1,
                    PagingCookie = null
                }
            };

            // PagingInfo needs both PageNumber and the raw cookie once we're past page 1; the
            // "pageToken" this method receives/returns is that cookie serialized as-is.
            if (!string.IsNullOrEmpty(pageToken))
            {
                var parts = pageToken.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length == 2 && int.TryParse(parts[0], out var pageNumber))
                {
                    query.PageInfo.PageNumber = pageNumber;
                    query.PageInfo.PagingCookie = parts[1];
                }
            }

            var result = _service.RetrieveMultiple(query);

            var records = result.Entities.Select(ToDataRecord).ToList();
            string nextToken = null;
            if (result.MoreRecords)
            {
                var nextPageNumber = query.PageInfo.PageNumber + 1;
                nextToken = nextPageNumber + "||" + result.PagingCookie;
            }

            return Task.FromResult(new RecordPage
            {
                Records = records,
                HasMore = result.MoreRecords,
                NextPageToken = nextToken
            });
        }

        public Task<IReadOnlyList<DataRecord>> RetrieveByIdsAsync(
            string logicalName,
            IReadOnlyList<Guid> ids,
            IReadOnlyList<string> columns,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (ids == null || ids.Count == 0)
                return Task.FromResult<IReadOnlyList<DataRecord>>(Array.Empty<DataRecord>());

            // Every Dataverse entity's primary key attribute is, by platform convention, always
            // "<logicalname>id" — never configurable, even for custom entities — so this doesn't
            // need a metadata round trip to look up.
            var primaryIdAttribute = logicalName + "id";
            var results = new List<DataRecord>(ids.Count);

            // ConditionOperator.In has practical limits on very large lists; 500 keeps each
            // request well within them while still batching far fewer round trips than one
            // Retrieve per id (this method exists specifically for a small "failed records" set,
            // never a full-table read — see RetrievePageAsync for that).
            const int MaxIdsPerQuery = 500;
            for (int offset = 0; offset < ids.Count; offset += MaxIdsPerQuery)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var slice = ids.Skip(offset).Take(MaxIdsPerQuery).Cast<object>().ToArray();

                var query = new QueryExpression(logicalName)
                {
                    ColumnSet = new ColumnSet(columns.ToArray())
                };
                query.Criteria.AddCondition(primaryIdAttribute, ConditionOperator.In, slice);

                var result = _service.RetrieveMultiple(query);
                results.AddRange(result.Entities.Select(ToDataRecord));
            }

            return Task.FromResult<IReadOnlyList<DataRecord>>(results);
        }

        public Task<bool> ExistsAsync(DataReference reference, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                _service.Retrieve(reference.LogicalName, reference.Id, new ColumnSet(false));
                return Task.FromResult(true);
            }
            catch (System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> ex)
                when (IsNotFoundFault(ex))
            {
                return Task.FromResult(false);
            }
        }

        public Task<int> GetApproximateCountAsync(string logicalName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = new RetrieveTotalRecordCountRequest
            {
                EntityNames = new[] { logicalName }
            };
            var response = (RetrieveTotalRecordCountResponse)_service.Execute(request);
            long count;
            response.EntityRecordCountCollection.TryGetValue(logicalName, out count);
            return Task.FromResult((int)count);
        }

        public Task<IReadOnlyList<RecordOperationResult>> WriteBatchAsync(
            string logicalName,
            IReadOnlyList<DataRecord> batch,
            WriteStrategy strategy,
            int pass,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (strategy)
            {
                case WriteStrategy.BulkCreateThenUpdate:
                    return Task.FromResult<IReadOnlyList<RecordOperationResult>>(WriteBulkCreateThenUpdate(logicalName, batch, pass));
                case WriteStrategy.ExecuteMultipleUpsert:
                    return Task.FromResult<IReadOnlyList<RecordOperationResult>>(WriteExecuteMultipleUpsert(batch, pass));
                default:
                    return Task.FromResult<IReadOnlyList<RecordOperationResult>>(WriteIndividual(batch, pass));
            }
        }

        public Task AssociateAsync(
            string relationshipSchemaName,
            DataReference from,
            IReadOnlyList<DataReference> to,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var related = new EntityReferenceCollection(
                to.Select(r => new EntityReference(r.LogicalName, r.Id)).ToList());

            try
            {
                _service.Associate(
                    from.LogicalName,
                    from.Id,
                    new Relationship(relationshipSchemaName),
                    related);
            }
            catch (System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> ex)
                when (IsDuplicateAssociationFault(ex))
            {
                // Section 15: a second run must stay idempotent. An already-existing N:N
                // association surfaces as a duplicate-key fault; treat it as a no-op success
                // rather than an error.
            }

            return Task.CompletedTask;
        }

        // ---- helpers -------------------------------------------------------------------

        private List<RecordOperationResult> WriteBulkCreateThenUpdate(string logicalName, IReadOnlyList<DataRecord> batch, int pass)
        {
            var toCreate = new List<DataRecord>();
            var toUpdate = new List<DataRecord>();

            foreach (var record in batch)
            {
                bool exists;
                try
                {
                    _service.Retrieve(logicalName, record.Id, new ColumnSet(false));
                    exists = true;
                }
                catch (System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> ex)
                    when (IsNotFoundFault(ex))
                {
                    exists = false;
                }

                (exists ? toUpdate : toCreate).Add(record);
            }

            var results = new List<RecordOperationResult>();

            if (toCreate.Count > 0)
            {
                results.AddRange(ExecuteBulk(logicalName, toCreate, RecordOperation.Create, pass,
                    entities => new CreateMultipleRequest { Targets = new EntityCollection(entities) { EntityName = logicalName } }));
            }

            if (toUpdate.Count > 0)
            {
                results.AddRange(ExecuteBulk(logicalName, toUpdate, RecordOperation.Update, pass,
                    entities => new UpdateMultipleRequest { Targets = new EntityCollection(entities) { EntityName = logicalName } }));
            }

            return results;
        }

        private List<RecordOperationResult> ExecuteBulk(
            string logicalName,
            List<DataRecord> records,
            RecordOperation operation,
            int pass,
            Func<Entity[], OrganizationRequest> buildRequest)
        {
            var entities = records.Select(ToSdkEntity).ToArray();

            try
            {
                _service.Execute(buildRequest(entities));

                return records.Select(r => new RecordOperationResult
                {
                    RecordId = r.Id,
                    TableLogicalName = logicalName,
                    Operation = operation,
                    Outcome = RecordOutcome.Succeeded,
                    Pass = pass
                }).ToList();
            }
            catch (Exception ex)
            {
                // A same-operation bulk request that fails wholesale is reported as a failure
                // for every record in it; the retry engine (Core.Migration.RetryPolicy) decides
                // whether to retry the whole sub-batch or split it further.
                ClassifyException(ex, out var isTransient, out var retryAfter);
                return records.Select(r => new RecordOperationResult
                {
                    RecordId = r.Id,
                    TableLogicalName = logicalName,
                    Operation = operation,
                    Outcome = RecordOutcome.Failed,
                    Pass = pass,
                    ErrorMessage = ex.Message,
                    IsTransient = isTransient,
                    RetryAfterHint = retryAfter
                }).ToList();
            }
        }

        private List<RecordOperationResult> WriteExecuteMultipleUpsert(IReadOnlyList<DataRecord> batch, int pass)
        {
            var requestCollection = new OrganizationRequestCollection();
            foreach (var record in batch)
            {
                requestCollection.Add(new UpsertRequest { Target = ToSdkEntity(record) });
            }

            var executeMultiple = new ExecuteMultipleRequest
            {
                Settings = new ExecuteMultipleSettings { ContinueOnError = true, ReturnResponses = true },
                Requests = requestCollection
            };

            var response = (ExecuteMultipleResponse)_service.Execute(executeMultiple);
            var results = new List<RecordOperationResult>();

            for (int i = 0; i < batch.Count; i++)
            {
                var record = batch[i];
                var itemResponse = response.Responses.FirstOrDefault(r => r.RequestIndex == i);

                if (itemResponse?.Fault != null)
                {
                    ClassifyFault(itemResponse.Fault, out var isTransient, out var retryAfter);
                    results.Add(new RecordOperationResult
                    {
                        RecordId = record.Id,
                        TableLogicalName = record.LogicalName,
                        Operation = RecordOperation.Create, // Upsert: exact create-vs-update outcome isn't reported back per item.
                        Outcome = RecordOutcome.Failed,
                        Pass = pass,
                        ErrorMessage = itemResponse.Fault.Message,
                        IsTransient = isTransient,
                        RetryAfterHint = retryAfter
                    });
                }
                else
                {
                    results.Add(new RecordOperationResult
                    {
                        RecordId = record.Id,
                        TableLogicalName = record.LogicalName,
                        Operation = RecordOperation.Create,
                        Outcome = RecordOutcome.Succeeded,
                        Pass = pass
                    });
                }
            }

            return results;
        }

        private List<RecordOperationResult> WriteIndividual(IReadOnlyList<DataRecord> batch, int pass)
        {
            var results = new List<RecordOperationResult>();
            foreach (var record in batch)
            {
                try
                {
                    _service.Execute(new UpsertRequest { Target = ToSdkEntity(record) });
                    results.Add(new RecordOperationResult
                    {
                        RecordId = record.Id,
                        TableLogicalName = record.LogicalName,
                        Operation = RecordOperation.Create,
                        Outcome = RecordOutcome.Succeeded,
                        Pass = pass
                    });
                }
                catch (Exception ex)
                {
                    ClassifyException(ex, out var isTransient, out var retryAfter);
                    results.Add(new RecordOperationResult
                    {
                        RecordId = record.Id,
                        TableLogicalName = record.LogicalName,
                        Operation = RecordOperation.Create,
                        Outcome = RecordOutcome.Failed,
                        Pass = pass,
                        ErrorMessage = ex.Message,
                        IsTransient = isTransient,
                        RetryAfterHint = retryAfter
                    });
                }
            }
            return results;
        }

        private static Entity ToSdkEntity(DataRecord record)
        {
            var entity = new Entity(record.LogicalName, record.Id);
            foreach (var kvp in record.Attributes)
            {
                if (kvp.Value is DataReference dr)
                {
                    // Same "entity" placeholder problem already fixed in ExternalLookupSampler
                    // (Preflight path): a genuinely polymorphic/unresolvable reference — seen on
                    // Microsoft-managed msdyn_* Copilot/AI Builder "regarding"-style fields — that
                    // Dataverse's Create/Update would reject outright ("The 'Create'/'Update'
                    // method does not support entities of type 'entity'"). Omit the value from
                    // the write payload entirely rather than fail the whole record/batch over one
                    // unresolvable field.
                    if (string.IsNullOrEmpty(dr.LogicalName) || string.Equals(dr.LogicalName, "entity", StringComparison.OrdinalIgnoreCase))
                        continue;

                    entity[kvp.Key] = new EntityReference(dr.LogicalName, dr.Id);
                }
                else
                {
                    entity[kvp.Key] = kvp.Value;
                }
            }
            return entity;
        }

        private static DataRecord ToDataRecord(Entity entity)
        {
            var record = new DataRecord(entity.LogicalName, entity.Id);
            foreach (var kvp in entity.Attributes)
            {
                record.Attributes[kvp.Key] = kvp.Value is EntityReference er
                    ? new DataReference(er.LogicalName, er.Id)
                    : kvp.Value;
            }
            return record;
        }

        /// <summary>
        /// Classifies a failed write as transient or not (ARCHITECTURE.md sección 8: the Core
        /// RetryPolicy only applies the backoff math, this adapter is the one that actually
        /// understands Dataverse/WCF exceptions).
        ///
        /// Deliberately conservative: relies on well-documented signals only —
        /// <c>OrganizationServiceFault.ErrorDetails["Retry-After"]</c> (the mechanism Dataverse
        /// uses to report Service Protection API throttling) and standard .NET
        /// network-transport exception types (timeout / connection failures). It does NOT guess
        /// at undocumented numeric Dataverse error codes for "throttled" — if a specific fault
        /// code turns out to need retrying in your tenant, add it here once observed rather than
        /// assumed.
        /// </summary>
        private static void ClassifyException(Exception ex, out bool isTransient, out TimeSpan? retryAfter)
        {
            if (ex is System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> faultEx && faultEx.Detail != null)
            {
                ClassifyFault(faultEx.Detail, out isTransient, out retryAfter);
                return;
            }

            retryAfter = null;
            isTransient = ex is System.ServiceModel.CommunicationException
                || ex is System.TimeoutException
                || ex is System.Net.Sockets.SocketException
                || ex is System.Net.WebException;
        }

        private static void ClassifyFault(Microsoft.Xrm.Sdk.OrganizationServiceFault fault, out bool isTransient, out TimeSpan? retryAfter)
        {
            retryAfter = null;
            isTransient = false;

            if (fault?.ErrorDetails != null && fault.ErrorDetails.TryGetValue("Retry-After", out var raw) && raw is TimeSpan ts)
            {
                isTransient = true;
                retryAfter = ts;
            }
        }

        private static bool IsNotFoundFault(System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> ex)
        {
            // Error code 0x80040217 (-2147220969) is Dataverse's "record does not exist".
            // Matching by ErrorCode rather than message text since messages are localized.
            return ex.Detail?.ErrorCode == unchecked((int)0x80040217);
        }

        private static bool IsDuplicateAssociationFault(System.ServiceModel.FaultException<Microsoft.Xrm.Sdk.OrganizationServiceFault> ex)
        {
            // 0x80040237 is Dataverse's "duplicate key" / already-associated fault.
            return ex.Detail?.ErrorCode == unchecked((int)0x80040237);
        }
    }
}
