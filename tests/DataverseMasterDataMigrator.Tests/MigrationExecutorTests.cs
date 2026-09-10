using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class MigrationExecutorTests
    {
        private static AttributeSummary Primitive(string logicalName) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.Primitive,
            IsValidForCreate = true,
            IsValidForUpdate = true
        };

        private static AttributeSummary Lookup(string logicalName, params string[] targets) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.Lookup,
            LookupTargets = targets,
            IsValidForCreate = true,
            IsValidForUpdate = true
        };

        private static AttributeSummary RequiredLookup(string logicalName, params string[] targets) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.Lookup,
            LookupTargets = targets,
            RequiredLevel = "ApplicationRequired",
            IsValidForCreate = true,
            IsValidForUpdate = true
        };

        private static AttributeSummary StateStatus(string logicalName) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.StateStatus,
            IsValidForCreate = true,
            IsValidForUpdate = true
        };

        private static TableSummary Table(string logicalName, params AttributeSummary[] attributes) => new TableSummary
        {
            LogicalName = logicalName,
            DisplayName = logicalName,
            PrimaryIdAttribute = logicalName + "id",
            HasStateStatus = attributes.Any(a => a.Kind == AttributeKind.StateStatus),
            Attributes = attributes
        };

        /// <summary>
        /// In-memory fake of both Source and Target sides of <see cref="IDataverseRecordService"/>.
        /// No mocking framework is used anywhere in this test project (see the rest of this
        /// folder) — a small hand-written fake keeps these tests exercising the real engine logic
        /// without a live Dataverse connection.
        /// </summary>
        private sealed class FakeRecordService : IDataverseRecordService
        {
            private readonly Dictionary<string, List<DataRecord>> _sourceData;

            public Dictionary<(string Table, Guid Id), DataRecord> Written { get; } = new Dictionary<(string, Guid), DataRecord>();
            public List<Guid> AssociatedFrom { get; } = new List<Guid>();
            public Dictionary<Guid, int> AttemptsSeen { get; } = new Dictionary<Guid, int>();

            /// <summary>Referenced ids that ExistsAsync should report as NOT existing in Target.
            /// Empty by default, matching the previous unconditional "return true" — every
            /// pre-existing test that never touches this is completely unaffected.</summary>
            public HashSet<Guid> MissingTargetIds { get; } = new HashSet<Guid>();
            public Dictionary<Guid, int> ExistsCallCounts { get; } = new Dictionary<Guid, int>();

            /// <summary>Return non-null to force a specific result for (recordId, attemptNumber); return null to let it succeed normally.</summary>
            public Func<Guid, int, RecordOperationResult> FailureInjector { get; set; }

            public FakeRecordService(Dictionary<string, List<DataRecord>> sourceData = null)
            {
                _sourceData = sourceData ?? new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase);
            }

            public Task<RecordPage> RetrievePageAsync(string logicalName, IReadOnlyList<string> columns, string pageToken, int pageSize, CancellationToken cancellationToken)
                => RetrieveFilteredPageAsync(logicalName, columns, null, pageToken, pageSize, cancellationToken);

            public Task<RecordPage> RetrieveFilteredPageAsync(string logicalName, IReadOnlyList<string> columns, RecordFilter filter, string pageToken, int pageSize, CancellationToken cancellationToken)
            {
                _sourceData.TryGetValue(logicalName, out var list);
                var filtered = (list ?? new List<DataRecord>()).Where(r => MatchesFilter(r, filter)).ToList();
                return Task.FromResult(new RecordPage { Records = filtered, HasMore = false, NextPageToken = null });
            }

            public Task<IReadOnlyList<DataRecord>> RetrieveByIdsAsync(string logicalName, IReadOnlyList<Guid> ids, IReadOnlyList<string> columns, CancellationToken cancellationToken)
            {
                _sourceData.TryGetValue(logicalName, out var list);
                var idSet = new HashSet<Guid>(ids);
                var matches = (list ?? new List<DataRecord>()).Where(r => idSet.Contains(r.Id)).ToList();
                return Task.FromResult<IReadOnlyList<DataRecord>>(matches);
            }

            public Task<bool> ExistsAsync(DataReference reference, CancellationToken cancellationToken)
            {
                ExistsCallCounts.TryGetValue(reference.Id, out var count);
                ExistsCallCounts[reference.Id] = count + 1;
                return Task.FromResult(!MissingTargetIds.Contains(reference.Id));
            }

            public Task<int> GetApproximateCountAsync(string logicalName, CancellationToken cancellationToken) => Task.FromResult(0);

            public Task<IReadOnlyList<RecordOperationResult>> WriteBatchAsync(
                string logicalName, IReadOnlyList<DataRecord> batch, WriteStrategy strategy, int pass, CancellationToken cancellationToken)
            {
                var results = new List<RecordOperationResult>();

                foreach (var record in batch)
                {
                    AttemptsSeen.TryGetValue(record.Id, out var seen);
                    seen++;
                    AttemptsSeen[record.Id] = seen;

                    var injected = FailureInjector?.Invoke(record.Id, seen);
                    if (injected != null)
                    {
                        injected.RecordId = record.Id;
                        injected.TableLogicalName = logicalName;
                        injected.Pass = pass;
                        results.Add(injected);
                        continue;
                    }

                    var key = (logicalName, record.Id);
                    if (!Written.TryGetValue(key, out var existing))
                    {
                        existing = new DataRecord(logicalName, record.Id);
                        Written[key] = existing;
                    }
                    foreach (var kvp in record.Attributes)
                        existing.Attributes[kvp.Key] = kvp.Value;

                    results.Add(new RecordOperationResult
                    {
                        RecordId = record.Id,
                        TableLogicalName = logicalName,
                        Operation = RecordOperation.Create,
                        Outcome = RecordOutcome.Succeeded,
                        Pass = pass
                    });
                }

                return Task.FromResult<IReadOnlyList<RecordOperationResult>>(results);
            }

            public Task AssociateAsync(string relationshipSchemaName, DataReference from, IReadOnlyList<DataReference> to, CancellationToken cancellationToken)
            {
                AssociatedFrom.Add(from.Id);
                return Task.CompletedTask;
            }

            public Task<IReadOnlyList<RecordOperationResult>> DeleteBatchAsync(string logicalName, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
                => throw new NotSupportedException("FakeRecordService no soporta DeleteBatchAsync (ningún test de MigrationExecutor lo necesita).");

            private static bool MatchesFilter(DataRecord record, RecordFilter filter)
            {
                if (filter == null || ((filter.Conditions?.Count ?? 0) == 0 && (filter.SubFilters?.Count ?? 0) == 0))
                    return true;

                bool EvaluateCondition(FilterCondition c)
                {
                    record.Attributes.TryGetValue(c.AttributeName, out var actual);
                    if (c.Operator == FilterOperator.In)
                    {
                        var values = (c.Value is string || !(c.Value is System.Collections.IEnumerable enumerable))
                            ? new[] { c.Value }
                            : enumerable.Cast<object>();
                        return values.Any(v => Equals(actual, v));
                    }
                    var matches = Equals(actual, c.Value);
                    return c.Operator == FilterOperator.NotEqual ? !matches : matches;
                }

                var results = (filter.Conditions ?? new List<FilterCondition>()).Select(EvaluateCondition)
                    .Concat((filter.SubFilters ?? new List<RecordFilter>()).Select(sf => MatchesFilter(record, sf)))
                    .ToList();

                if (results.Count == 0) return true;
                return filter.LogicalOperator == FilterLogicalOperator.Or ? results.Any(x => x) : results.All(x => x);
            }
        }

        private static MigrationExecutionRequest BuildRequest(
            MigrationProfile profile,
            Dictionary<string, TableSummary> tables,
            FakeRecordService source,
            FakeRecordService target)
        {
            var plan = MigrationPlanner.CreatePlan(profile, tables);
            return new MigrationExecutionRequest
            {
                Profile = profile,
                Plan = plan,
                SourceTables = tables,
                TargetTables = tables,
                SourceRecords = source,
                TargetRecords = target,
                RetryPolicy = new RetryPolicy(maxAttempts: 3, baseDelay: TimeSpan.FromMilliseconds(1), maxDelay: TimeSpan.FromMilliseconds(5))
            };
        }

        [Fact]
        public async Task ExcludedRecordIds_AreNeverWrittenOrCounted()
        {
            var keptId = Guid.NewGuid();
            var excludedId = Guid.NewGuid();
            var kept = new DataRecord("wit_tema", keptId);
            kept.Attributes["name"] = "Kept";
            var excluded = new DataRecord("wit_tema", excludedId);
            excluded.Attributes["name"] = "Excluded";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { kept, excluded }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);
            request.ExcludedRecordIds = new Dictionary<string, IReadOnlyCollection<Guid>>
            {
                ["wit_tema"] = new List<Guid> { excludedId }
            };

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            var tableResult = manifest.Tables.Single();
            Assert.Equal(1, tableResult.SourceRecordCount); // the excluded record was never even counted
            Assert.Equal(1, tableResult.Created);
            Assert.True(target.Written.ContainsKey(("wit_tema", keptId)));
            Assert.False(target.Written.ContainsKey(("wit_tema", excludedId)));
            Assert.False(target.AttemptsSeen.ContainsKey(excludedId)); // never even attempted
        }

        [Fact]
        public async Task LinearDependency_WritesParentBeforeChild_WithLookupResolvedInPass1()
        {
            var temaId = Guid.NewGuid();
            var subtemaId = Guid.NewGuid();

            var tema = new DataRecord("wit_tema", temaId);
            tema.Attributes["name"] = "Tema A";

            var subtema = new DataRecord("wit_subtema", subtemaId);
            subtema.Attributes["name"] = "Subtema A";
            subtema.Attributes["wit_temaid"] = new DataReference("wit_tema", temaId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { tema },
                ["wit_subtema"] = new List<DataRecord> { subtema }
            };

            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name")),
                ["wit_subtema"] = Table("wit_subtema", Primitive("name"), Lookup("wit_temaid", "wit_tema"))
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_tema" }, new ProfileEntity { LogicalName = "wit_subtema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(2, manifest.Tables.Count);
            Assert.True(target.Written.ContainsKey(("wit_subtema", subtemaId)));
            var writtenSubtema = target.Written[("wit_subtema", subtemaId)];
            var lookupValue = Assert.IsType<DataReference>(writtenSubtema.Attributes["wit_temaid"]);
            Assert.Equal(temaId, lookupValue.Id);
        }

        [Fact]
        public async Task SelfReferencingLookup_IsDeferredToPass2()
        {
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();

            var parent = new DataRecord("wit_categoria", parentId);
            parent.Attributes["name"] = "Raíz";

            var child = new DataRecord("wit_categoria", childId);
            child.Attributes["name"] = "Hija";
            child.Attributes["wit_parentid"] = new DataReference("wit_categoria", parentId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_categoria"] = new List<DataRecord> { parent, child }
            };

            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_categoria"] = Table("wit_categoria", Primitive("name"), Lookup("wit_parentid", "wit_categoria"))
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_categoria" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);

            var writtenChild = target.Written[("wit_categoria", childId)];
            var lookupValue = Assert.IsType<DataReference>(writtenChild.Attributes["wit_parentid"]);
            Assert.Equal(parentId, lookupValue.Id);

            // The write for the deferred attribute must have happened as a Pass 2 call, i.e. the
            // record required at least two WriteBatchAsync round trips.
            Assert.True(target.AttemptsSeen[childId] >= 2);
        }

        [Fact]
        public async Task TransientFailure_RetriesAndEventuallySucceeds()
        {
            var recordId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };

            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"))
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService
            {
                FailureInjector = (id, attempt) => attempt == 1
                    ? new RecordOperationResult { Outcome = RecordOutcome.Failed, IsTransient = true, ErrorMessage = "simulated throttling" }
                    : null
            };
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Created);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            Assert.Equal(2, target.AttemptsSeen[recordId]);

            var succeededResult = manifest.Tables.Single().Errors; // no errors expected
            Assert.Empty(succeededResult);
        }

        [Fact]
        public async Task NonTransientFailure_WithStopOnError_AbortsExecution()
        {
            var recordId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };

            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"))
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { StopOnError = true },
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService
            {
                FailureInjector = (id, attempt) => new RecordOperationResult { Outcome = RecordOutcome.Failed, IsTransient = false, ErrorMessage = "permanent" }
            };
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Failed, manifest.Status);
        }

        [Fact]
        public async Task RetryFailedAsync_PermanentPass1Failure_RetriesAndSucceedsOnceFixed()
        {
            var recordId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"))
            };
            var profile = new MigrationProfile { Name = "Test", Entities = { new ProfileEntity { LogicalName = "wit_tema" } } };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService
            {
                FailureInjector = (id, attempt) => new RecordOperationResult { Outcome = RecordOutcome.Failed, IsTransient = false, ErrorMessage = "permanent" }
            };
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);
            Assert.Equal(ExecutionStatus.CompletedWithWarnings, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Failed);

            target.FailureInjector = null; // simulate whatever caused the permanent error getting fixed
            var retried = await new MigrationExecutor().RetryFailedAsync(manifest, request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, retried.Status);
            Assert.Equal(0, retried.Tables.Single().Failed);
            Assert.Equal(1, retried.Tables.Single().Created);
            Assert.True(target.Written.ContainsKey(("wit_tema", recordId)));
        }

        [Fact]
        public async Task RetryFailedAsync_Pass2Failure_RetriesOnlyTheDeferredLookup()
        {
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();

            var parent = new DataRecord("wit_categoria", parentId);
            parent.Attributes["name"] = "Raíz";
            var child = new DataRecord("wit_categoria", childId);
            child.Attributes["name"] = "Hija";
            child.Attributes["wit_parentid"] = new DataReference("wit_categoria", parentId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_categoria"] = new List<DataRecord> { parent, child }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_categoria"] = Table("wit_categoria", Primitive("name"), Lookup("wit_parentid", "wit_categoria"))
            };
            var profile = new MigrationProfile { Name = "Test", Entities = { new ProfileEntity { LogicalName = "wit_categoria" } } };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService
            {
                // Fails only the child's 2nd write (its Pass 2 deferred-lookup update) — Pass 1
                // (attempt 1, for both parent and child) succeeds normally.
                FailureInjector = (id, attempt) => (id == childId && attempt == 2)
                    ? new RecordOperationResult { Outcome = RecordOutcome.Failed, IsTransient = false, ErrorMessage = "permanent" }
                    : null
            };
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);
            Assert.Equal(ExecutionStatus.CompletedWithWarnings, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Failed);
            Assert.False(target.Written[("wit_categoria", childId)].Attributes.ContainsKey("wit_parentid"));

            target.FailureInjector = null;
            var retried = await new MigrationExecutor().RetryFailedAsync(manifest, request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, retried.Status);
            Assert.Equal(0, retried.Tables.Single().Failed);
            var lookupValue = Assert.IsType<DataReference>(target.Written[("wit_categoria", childId)].Attributes["wit_parentid"]);
            Assert.Equal(parentId, lookupValue.Id);
        }

        [Fact]
        public async Task OwnerLookup_IsNeverWrittenAndDoesNotFailTheRecord()
        {
            // Regression test for a real bug: ownerid points to a Source-environment user/team
            // GUID that essentially never exists in Target. Since ownerid IS writable (unlike
            // createdby/modifiedby), it wasn't excluded by GetWritableAttributes and would have
            // been included in the write payload, causing a real Create failure (invalid FK)
            // rather than just a Preflight warning.
            var recordId = Guid.NewGuid();
            var ownerId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";
            record.Attributes["ownerid"] = new DataReference("systemuser", ownerId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"), Lookup("ownerid", "systemuser", "team"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Created);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            Assert.False(target.Written[("wit_tema", recordId)].Attributes.ContainsKey("ownerid"));
        }

        [Fact]
        public async Task OwnerIdOverride_Set_WritesOwnerLookupToOverrideSystemUser()
        {
            // Umayor.TestDataSeeder-only mitigation (ProfileOptions.OwnerIdOverride): when set,
            // Pass 1 must explicitly write the table's real owner-lookup attribute (ownerid, here
            // with LookupTargets systemuser/team so IsOwnerLookup is true) pointing at the given
            // SystemUser, instead of leaving it out of the payload as the default (null) does.
            var recordId = Guid.NewGuid();
            var overrideOwnerId = Guid.NewGuid();
            var sourceOwnerId = Guid.NewGuid(); // the Source-environment owner — must NOT survive the override.
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";
            record.Attributes["ownerid"] = new DataReference("systemuser", sourceOwnerId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"), Lookup("ownerid", "systemuser", "team"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { OwnerIdOverride = overrideOwnerId },
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Created);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            var writtenOwner = Assert.IsType<DataReference>(target.Written[("wit_tema", recordId)].Attributes["ownerid"]);
            Assert.Equal("systemuser", writtenOwner.LogicalName);
            Assert.Equal(overrideOwnerId, writtenOwner.Id);
        }

        [Fact]
        public async Task OwnerIdOverride_NullByDefault_OwnerLookupStillNeverWritten()
        {
            // Confirms the generic migrator's unchanged default behavior: with OwnerIdOverride left
            // null (its default — the generic DataverseMasterDataMigrator.XrmToolBox profile editor
            // never sets it), the owner-lookup attribute must still never appear in the write
            // payload at all, exactly like before this feature existed.
            var recordId = Guid.NewGuid();
            var sourceOwnerId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";
            record.Attributes["ownerid"] = new DataReference("systemuser", sourceOwnerId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"), Lookup("ownerid", "systemuser", "team"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                // Options left as default: OwnerIdOverride == null.
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            Assert.Null(profile.Options.OwnerIdOverride);

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.False(target.Written[("wit_tema", recordId)].Attributes.ContainsKey("ownerid"));
        }

        [Fact]
        public async Task StateStatusRestoreInPass3_DoesNotDoubleCountCreated()
        {
            // Regression test for a real bug: Pass 3's statecode/statuscode restore reused
            // ApplyResults, which increments Created for every successful write whose Operation
            // isn't explicitly Update — the adapter's ExecuteMultipleUpsert strategy always
            // reports Operation=Create (it can't tell create vs. update apart from the SDK
            // response), so a single record ended up counted twice: once in Pass 1, once again
            // when Pass 3 restored its state. A user hit this with 32 Source records reported as
            // "64 created".
            var recordId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";
            record.Attributes["statecode"] = 1;
            record.Attributes["statuscode"] = 2;

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"), StateStatus("statecode"), StateStatus("statuscode"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { RestoreStateStatus = true },
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            var tableResult = manifest.Tables.Single();
            Assert.Equal(1, tableResult.Created);
            Assert.Equal(0, tableResult.Updated);
            Assert.Equal(0, tableResult.Failed);
            // 2 WriteBatchAsync calls for this one record: Pass 1 create, Pass 3 state restore.
            Assert.Equal(2, target.AttemptsSeen[recordId]);
        }

        [Fact]
        public async Task RetryFailedAsync_NoFailures_ReturnsManifestUnchanged()
        {
            var recordId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"))
            };
            var profile = new MigrationProfile { Name = "Test", Entities = { new ProfileEntity { LogicalName = "wit_tema" } } };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);
            Assert.Equal(ExecutionStatus.Completed, manifest.Status);

            var retried = await new MigrationExecutor().RetryFailedAsync(manifest, request, CancellationToken.None);

            Assert.Same(manifest, retried);
            Assert.Equal(ExecutionStatus.Completed, retried.Status);
        }

        [Fact]
        public async Task Cancellation_MarksManifestCancelled()
        {
            var recordId = Guid.NewGuid();
            var record = new DataRecord("wit_tema", recordId);
            record.Attributes["name"] = "Tema A";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { record }
            };

            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"))
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_tema" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var manifest = await new MigrationExecutor().ExecuteAsync(request, cts.Token);
                Assert.Equal(ExecutionStatus.Cancelled, manifest.Status);
            }
        }

        // ---------------------------------------------------------------------------------
        // SkipSilently external-lookup regression tests (real bug: 66/96 failures in one run
        // cascaded from an external lookup whose GUID didn't exist in Target being written
        // anyway and rejected by Dataverse itself, instead of being omitted per the profile's
        // OptionalLookupPolicy/RequiredLookupPolicy = SkipSilently setting).
        // ---------------------------------------------------------------------------------

        [Fact]
        public async Task ExternalLookup_Missing_OptionalSkipSilently_OmitsAttributeButWritesRecord()
        {
            var recordId = Guid.NewGuid();
            var missingContactId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { OptionalLookupPolicy = LookupPolicy.SkipSilently },
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            target.MissingTargetIds.Add(missingContactId);
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Created);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            Assert.True(target.Written.ContainsKey(("wit_detalle", recordId)));
            Assert.False(target.Written[("wit_detalle", recordId)].Attributes.ContainsKey("primarycontactid"));
        }

        [Fact]
        public async Task ExternalLookup_Missing_DefaultWarnAndContinue_StillWritesAttribute_Regression()
        {
            // Regression-protection test: the DEFAULT OptionalLookupPolicy (WarnAndContinue) must
            // behave exactly as before this fix — the value is still attempted as-is.
            var recordId = Guid.NewGuid();
            var missingContactId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                // Options left as default: OptionalLookupPolicy = WarnAndContinue.
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            target.MissingTargetIds.Add(missingContactId);
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.True(target.Written[("wit_detalle", recordId)].Attributes.ContainsKey("primarycontactid"));
            var lookupValue = Assert.IsType<DataReference>(target.Written[("wit_detalle", recordId)].Attributes["primarycontactid"]);
            Assert.Equal(missingContactId, lookupValue.Id);
        }

        [Fact]
        public async Task ExternalLookup_Existing_OptionalSkipSilently_ValueIsNotRemoved()
        {
            // SkipSilently only removes values confirmed MISSING in Target — a value that
            // resolves fine must never be blanket-stripped just because it targets an
            // out-of-profile table.
            var recordId = Guid.NewGuid();
            var existingContactId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("contact", existingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { OptionalLookupPolicy = LookupPolicy.SkipSilently },
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService(); // existingContactId is NOT in MissingTargetIds, i.e. it exists.
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            var lookupValue = Assert.IsType<DataReference>(target.Written[("wit_detalle", recordId)].Attributes["primarycontactid"]);
            Assert.Equal(existingContactId, lookupValue.Id);
        }

        [Fact]
        public async Task ExternalLookup_Missing_RequiredSkipSilently_OmitsAttribute()
        {
            // Confirms RequiredLookupPolicy is respected independently of OptionalLookupPolicy,
            // keyed by the attribute's own IsRequired.
            var recordId = Guid.NewGuid();
            var missingContactId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), RequiredLookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { RequiredLookupPolicy = LookupPolicy.SkipSilently },
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            target.MissingTargetIds.Add(missingContactId);
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            Assert.False(target.Written[("wit_detalle", recordId)].Attributes.ContainsKey("primarycontactid"));
        }

        [Fact]
        public async Task ExternalLookup_SameMissingGuidAcrossRecords_ExistsAsyncCalledOnce()
        {
            // Matches ExternalLookupSampler's established caching idiom: the same distinct
            // external-lookup GUID, referenced by many records, must only cost one ExistsAsync
            // round trip against Target.
            var missingContactId = Guid.NewGuid();
            var record1 = new DataRecord("wit_detalle", Guid.NewGuid());
            record1.Attributes["name"] = "A";
            record1.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);
            var record2 = new DataRecord("wit_detalle", Guid.NewGuid());
            record2.Attributes["name"] = "B";
            record2.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record1, record2 }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { OptionalLookupPolicy = LookupPolicy.SkipSilently },
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            target.MissingTargetIds.Add(missingContactId);
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(2, manifest.Tables.Single().Created);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            Assert.Equal(1, target.ExistsCallCounts[missingContactId]); // cached after the first distinct GUID.
        }

        [Fact]
        public async Task RetryFailedAsync_Pass1_RespectsSkipSilently_ForRetriedRecord()
        {
            // The specific gap the fix closes: a naive retry of a Pass 1 failure caused by an
            // unresolved external lookup would otherwise hit the exact same wall again.
            var recordId = Guid.NewGuid();
            var missingContactId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { OptionalLookupPolicy = LookupPolicy.SkipSilently },
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService
            {
                // Forces the record's very first write attempt to fail permanently, regardless of
                // its content — standing in for the real "Entity ... Does Not Exist" rejection a
                // Dataverse write would have produced before this fix, so this test can prove the
                // RETRY path (not the original ExecuteAsync path) is the one whose write finally
                // lands in Written.
                FailureInjector = (id, attempt) => attempt == 1
                    ? new RecordOperationResult { Outcome = RecordOutcome.Failed, IsTransient = false, ErrorMessage = "permanent" }
                    : null
            };
            target.MissingTargetIds.Add(missingContactId);
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);
            Assert.Equal(ExecutionStatus.CompletedWithWarnings, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Failed);
            Assert.False(target.Written.ContainsKey(("wit_detalle", recordId))); // failed attempt never lands in Written.

            target.FailureInjector = null; // simulate whatever caused the permanent error getting fixed
            var retried = await new MigrationExecutor().RetryFailedAsync(manifest, request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, retried.Status);
            Assert.Equal(0, retried.Tables.Single().Failed);
            Assert.True(target.Written.ContainsKey(("wit_detalle", recordId)));
            Assert.False(target.Written[("wit_detalle", recordId)].Attributes.ContainsKey("primarycontactid"));
        }

        [Fact]
        public async Task DefaultPolicies_NeverCallsExistsAsync_FastPathUnaffected()
        {
            // Regression-protection + fast-path proof: when neither OptionalLookupPolicy nor
            // RequiredLookupPolicy is SkipSilently, RemoveSkipSilentlyLookupsAsync's short-circuit
            // means the engine never even calls ExistsAsync for an external lookup — matching the
            // doc comment's claim that this is a pure no-op for every profile that hasn't opted in,
            // with zero behavior/perf change (same idiom ExternalLookupSamplerTests uses: proving
            // a check never fires by asserting it was never called).
            var recordId = Guid.NewGuid();
            var missingContactId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("contact", missingContactId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                // default Options: OptionalLookupPolicy = WarnAndContinue, RequiredLookupPolicy = FailPreflight.
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            target.MissingTargetIds.Add(missingContactId);
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Created);
            Assert.True(target.Written[("wit_detalle", recordId)].Attributes.ContainsKey("primarycontactid"));
            Assert.Empty(target.ExistsCallCounts);
        }

        [Fact]
        public async Task ExternalLookup_EntityPlaceholder_SkipSilently_DoesNotThrowAndSkipsExistsCheck()
        {
            // Real gap found in review: an unresolvable polymorphic reference (LogicalName
            // literally "entity" — seen on msdyn_* Copilot/AI Builder fields) has no real table
            // to check existence against. Dataverse's SDK rejects Retrieve/exists-style calls
            // against "entity" outright with a different fault than the IsNotFoundFault
            // DataverseRecordServiceAdapter.ExistsAsync already tolerates, so calling ExistsAsync
            // on it would propagate an unhandled fault all the way up through ExecuteAsync's
            // outer catch-all and abort the entire run — exactly what SkipSilently must never do.
            // Mirrors ExternalLookupSampler's existing guard: skip the value entirely, same as
            // any other reference we simply can't evaluate.
            var recordId = Guid.NewGuid();
            var entityPlaceholderId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", recordId);
            record.Attributes["name"] = "Detalle A";
            record.Attributes["primarycontactid"] = new DataReference("entity", entityPlaceholderId);

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = Table("wit_detalle", Primitive("name"), Lookup("primarycontactid", "contact"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Options = new ProfileOptions { OptionalLookupPolicy = LookupPolicy.SkipSilently },
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            // Deliberately mark it "missing" too — proves the guard short-circuits BEFORE the
            // existence check even runs, not merely that this particular id happens to exist.
            target.MissingTargetIds.Add(entityPlaceholderId);
            var request = BuildRequest(profile, tables, source, target);

            // The core assertion: this must complete, not throw/abort the whole run.
            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            Assert.Equal(1, manifest.Tables.Single().Created);
            Assert.Equal(0, manifest.Tables.Single().Failed);
            Assert.True(target.Written.ContainsKey(("wit_detalle", recordId)));
            // ExistsAsync must never be called for the unresolvable "entity" placeholder specifically.
            Assert.False(target.ExistsCallCounts.ContainsKey(entityPlaceholderId));
        }

        [Fact]
        public async Task ProfileEntityFilter_OnlyMigratesMatchingRecords()
        {
            var matchingId = Guid.NewGuid();
            var nonMatchingId = Guid.NewGuid();
            var matching = new DataRecord("wit_tema", matchingId);
            matching.Attributes["name"] = "Tema A";
            matching.Attributes["tipo"] = "A";
            var nonMatching = new DataRecord("wit_tema", nonMatchingId);
            nonMatching.Attributes["name"] = "Tema B";
            nonMatching.Attributes["tipo"] = "B";

            var sourceData = new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new List<DataRecord> { matching, nonMatching }
            };
            var tables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = Table("wit_tema", Primitive("name"), Primitive("tipo"))
            };
            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities =
                {
                    new ProfileEntity
                    {
                        LogicalName = "wit_tema",
                        Filter = new RecordFilter
                        {
                            Conditions = { new FilterCondition { AttributeName = "tipo", Operator = FilterOperator.Equal, Value = "A" } }
                        }
                    }
                }
            };

            var source = new FakeRecordService(sourceData);
            var target = new FakeRecordService();
            var request = BuildRequest(profile, tables, source, target);

            var manifest = await new MigrationExecutor().ExecuteAsync(request, CancellationToken.None);

            Assert.Equal(ExecutionStatus.Completed, manifest.Status);
            var tableResult = manifest.Tables.Single();
            Assert.Equal(1, tableResult.SourceRecordCount);
            Assert.True(target.Written.ContainsKey(("wit_tema", matchingId)));
            Assert.False(target.Written.ContainsKey(("wit_tema", nonMatchingId)));
        }
    }
}
