using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Validation;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class ExternalLookupSamplerTests
    {
        private static AttributeSummary Lookup(string logicalName, bool required, params string[] targets) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.Lookup,
            LookupTargets = targets,
            RequiredLevel = required ? "ApplicationRequired" : "None",
            IsValidForCreate = true,
            IsValidForUpdate = true
        };

        /// <summary>A system-managed lookup like createdby/modifiedby/organizationid: never
        /// writable, so MigrationExecutor never attempts it and Preflight shouldn't warn about it either.</summary>
        private static AttributeSummary SystemManagedLookup(string logicalName, params string[] targets) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.Lookup,
            LookupTargets = targets,
            RequiredLevel = "None",
            IsValidForCreate = false,
            IsValidForUpdate = false
        };

        private sealed class FakeSourceService : IDataverseRecordService
        {
            private readonly Dictionary<string, List<DataRecord>> _data;
            public FakeSourceService(Dictionary<string, List<DataRecord>> data) => _data = data;

            public Task<RecordPage> RetrievePageAsync(string logicalName, IReadOnlyList<string> columns, string pageToken, int pageSize, CancellationToken cancellationToken)
                => RetrieveFilteredPageAsync(logicalName, columns, null, pageToken, pageSize, cancellationToken);

            public Task<RecordPage> RetrieveFilteredPageAsync(string logicalName, IReadOnlyList<string> columns, RecordFilter filter, string pageToken, int pageSize, CancellationToken cancellationToken)
            {
                _data.TryGetValue(logicalName, out var list);
                var filtered = (list ?? new List<DataRecord>()).Where(r => MatchesFilter(r, filter)).ToList();
                return Task.FromResult(new RecordPage { Records = filtered, HasMore = false, NextPageToken = null });
            }

            public Task<bool> ExistsAsync(DataReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<int> GetApproximateCountAsync(string logicalName, CancellationToken cancellationToken) => Task.FromResult(0);
            public Task<IReadOnlyList<RecordOperationResult>> WriteBatchAsync(string logicalName, IReadOnlyList<DataRecord> batch, WriteStrategy strategy, int pass, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task AssociateAsync(string relationshipSchemaName, DataReference from, IReadOnlyList<DataReference> to, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DataRecord>> RetrieveByIdsAsync(string logicalName, IReadOnlyList<Guid> ids, IReadOnlyList<string> columns, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<RecordOperationResult>> DeleteBatchAsync(string logicalName, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();

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

        private sealed class FakeTargetExistence : IDataverseRecordService
        {
            private readonly HashSet<Guid> _existingIds;
            public int ExistsCallCount { get; private set; }

            public FakeTargetExistence(IEnumerable<Guid> existingIds) => _existingIds = new HashSet<Guid>(existingIds);

            public Task<bool> ExistsAsync(DataReference reference, CancellationToken cancellationToken)
            {
                ExistsCallCount++;
                return Task.FromResult(_existingIds.Contains(reference.Id));
            }

            public Task<RecordPage> RetrievePageAsync(string logicalName, IReadOnlyList<string> columns, string pageToken, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<RecordPage> RetrieveFilteredPageAsync(string logicalName, IReadOnlyList<string> columns, RecordFilter filter, string pageToken, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<int> GetApproximateCountAsync(string logicalName, CancellationToken cancellationToken) => Task.FromResult(0);
            public Task<IReadOnlyList<RecordOperationResult>> WriteBatchAsync(string logicalName, IReadOnlyList<DataRecord> batch, WriteStrategy strategy, int pass, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task AssociateAsync(string relationshipSchemaName, DataReference from, IReadOnlyList<DataReference> to, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<DataRecord>> RetrieveByIdsAsync(string logicalName, IReadOnlyList<Guid> ids, IReadOnlyList<string> columns, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<IReadOnlyList<RecordOperationResult>> DeleteBatchAsync(string logicalName, IReadOnlyList<Guid> ids, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        [Fact]
        public async Task MissingExternalReference_IsReported()
        {
            var missingId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["primarycontactid"] = new DataReference("contact", missingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: true, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            var finding = Assert.Single(findings);
            Assert.Equal("wit_detalle", finding.TableLogicalName);
            Assert.Equal("primarycontactid", finding.AttributeLogicalName);
            Assert.True(finding.IsRequired);
            Assert.Equal(1, finding.AffectedRecordCount);
        }

        [Fact]
        public async Task ExistingExternalReference_IsNotReported()
        {
            var existingId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["primarycontactid"] = new DataReference("contact", existingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: false, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            var target = new FakeTargetExistence(new[] { existingId });

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Empty(findings);
        }

        [Fact]
        public async Task SystemManagedLookup_IsNeverSampled()
        {
            var missingId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["createdby"] = new DataReference("systemuser", missingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { SystemManagedLookup("createdby", "systemuser") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            // ExistsAsync would throw NotSupportedException if called — proving a non-writable
            // lookup never reaches the Target existence check, since MigrationExecutor would
            // never attempt to write it either.
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Empty(findings);
        }

        [Fact]
        public async Task OwnerLookup_IsNeverSampled()
        {
            // Regression test for a real bug: ownerid IS writable (unlike createdby/modifiedby),
            // so it wasn't caught by the IsValidForCreate/IsValidForUpdate filter — it showed up
            // as a blocking REQUIRED_LOOKUP_UNRESOLVED error on every table, since a Source
            // user/team GUID essentially never exists in Target.
            var missingId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["ownerid"] = new DataReference("systemuser", missingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("ownerid", required: true, "systemuser", "team") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            // ExistsAsync would throw NotSupportedException if called — proving ownerid never
            // reaches the Target existence check.
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Empty(findings);
        }

        [Fact]
        public async Task LookupTargetInsideProfile_IsNeverSampled()
        {
            var record = new DataRecord("wit_subtema", Guid.NewGuid());
            record.Attributes["wit_temaid"] = new DataReference("wit_tema", Guid.NewGuid());

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" },
                ["wit_subtema"] = new TableSummary
                {
                    LogicalName = "wit_subtema",
                    Attributes = new List<AttributeSummary> { Lookup("wit_temaid", required: true, "wit_tema") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_tema" }, new ProfileEntity { LogicalName = "wit_subtema" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_subtema"] = new List<DataRecord> { record }
            });
            // ExistsAsync would throw NotSupportedException if called — proving the in-profile
            // target never reaches the Target existence check.
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Empty(findings);
        }

        [Fact]
        public async Task SameMissingReferencedTwice_IsCountedOnceAgainstTarget()
        {
            var missingId = Guid.NewGuid();
            var record1 = new DataRecord("wit_detalle", Guid.NewGuid());
            record1.Attributes["primarycontactid"] = new DataReference("contact", missingId);
            var record2 = new DataRecord("wit_detalle", Guid.NewGuid());
            record2.Attributes["primarycontactid"] = new DataReference("contact", missingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: false, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record1, record2 }
            });
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Equal(2, Assert.Single(findings).AffectedRecordCount);
            Assert.Equal(1, target.ExistsCallCount); // cached after the first distinct GUID.
        }

        [Fact]
        public async Task ReferenceWithGenericEntityLogicalName_IsSkippedNotSampled()
        {
            // Regression test for a real production crash: some generic/polymorphic system
            // lookups (e.g. msdyn_* Copilot/AI Builder "regarding"-style fields with no specific
            // declared target type) carry the literal placeholder LogicalName "entity" instead of
            // a real table name. Dataverse's Retrieve rejects that name outright ("The 'Retrieve'
            // method does not support entities of type 'entity'"), which used to crash the whole
            // Preflight WorkAsync operation. It must be skipped instead: not reported as missing,
            // and never even passed to ExistsAsync (there's no real table to check against).
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["primarycontactid"] = new DataReference("entity", Guid.NewGuid());

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: true, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Empty(findings);
            Assert.Equal(0, target.ExistsCallCount);
        }

        [Fact]
        public async Task OnProgress_IsInvoked_WithTableName_WhenSamplingExternalLookups()
        {
            var missingId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["primarycontactid"] = new DataReference("contact", missingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: true, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var messages = new List<string>();
            await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None, msg => messages.Add(msg));

            Assert.NotEmpty(messages);
            Assert.Contains(messages, m => m.Contains("wit_detalle"));
        }

        [Fact]
        public async Task OnProgress_OmittedByDefault_DoesNotThrow()
        {
            // Proves the new onProgress parameter defaults to null safely — every call site
            // above this test was written before the parameter existed and must keep compiling
            // and passing unchanged.
            var missingId = Guid.NewGuid();
            var record = new DataRecord("wit_detalle", Guid.NewGuid());
            record.Attributes["primarycontactid"] = new DataReference("contact", missingId);

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: true, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities = { new ProfileEntity { LogicalName = "wit_detalle" } }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { record }
            });
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Single(findings);
        }

        [Fact]
        public async Task EntityFilter_ExcludesRecordFromSampling()
        {
            var missingId = Guid.NewGuid();
            var recordWithIssue = new DataRecord("wit_detalle", Guid.NewGuid());
            recordWithIssue.Attributes["primarycontactid"] = new DataReference("contact", missingId);
            recordWithIssue.Attributes["tipo"] = "A";

            var recordWithoutIssue = new DataRecord("wit_detalle", Guid.NewGuid());
            recordWithoutIssue.Attributes["tipo"] = "B";

            var sourceTables = new Dictionary<string, TableSummary>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new TableSummary
                {
                    LogicalName = "wit_detalle",
                    Attributes = new List<AttributeSummary> { Lookup("primarycontactid", required: true, "contact") }
                }
            };

            var profile = new MigrationProfile
            {
                Name = "Test",
                Entities =
                {
                    new ProfileEntity
                    {
                        LogicalName = "wit_detalle",
                        Filter = new RecordFilter
                        {
                            Conditions = { new FilterCondition { AttributeName = "tipo", Operator = FilterOperator.Equal, Value = "B" } }
                        }
                    }
                }
            };

            var source = new FakeSourceService(new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wit_detalle"] = new List<DataRecord> { recordWithIssue, recordWithoutIssue }
            });
            var target = new FakeTargetExistence(Array.Empty<Guid>());

            var findings = await new ExternalLookupSampler().SampleAsync(profile, sourceTables, source, target, pageSize: 500, CancellationToken.None);

            Assert.Empty(findings);
        }
    }
}
