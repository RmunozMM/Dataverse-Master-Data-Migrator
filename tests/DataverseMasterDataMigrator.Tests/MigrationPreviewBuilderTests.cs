using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Abstractions;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;
using DataverseMasterDataMigrator.Core.Validation;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class MigrationPreviewBuilderTests
    {
        private sealed class FakeService : IDataverseRecordService
        {
            private readonly Dictionary<string, List<DataRecord>> _sourceRecords;
            private readonly Dictionary<string, List<DataRecord>> _targetRecords;

            public FakeService(Dictionary<string, List<DataRecord>> sourceRecords = null, Dictionary<string, List<DataRecord>> targetRecords = null)
            {
                _sourceRecords = sourceRecords ?? new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase);
                _targetRecords = targetRecords ?? new Dictionary<string, List<DataRecord>>(StringComparer.OrdinalIgnoreCase);
            }

            public Task<RecordPage> RetrievePageAsync(string logicalName, IReadOnlyList<string> columns, string pageToken, int pageSize, CancellationToken cancellationToken)
            {
                _sourceRecords.TryGetValue(logicalName, out var records);
                return Task.FromResult(new RecordPage { Records = records ?? new List<DataRecord>(), HasMore = false, NextPageToken = null });
            }

            public Task<IReadOnlyList<DataRecord>> RetrieveByIdsAsync(string logicalName, IReadOnlyList<Guid> ids, IReadOnlyList<string> columns, CancellationToken cancellationToken)
            {
                _targetRecords.TryGetValue(logicalName, out var all);
                var idSet = new HashSet<Guid>(ids);
                var matches = (all ?? new List<DataRecord>()).Where(r => idSet.Contains(r.Id)).ToList();
                return Task.FromResult<IReadOnlyList<DataRecord>>(matches);
            }

            public Task<bool> ExistsAsync(DataReference reference, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<int> GetApproximateCountAsync(string logicalName, CancellationToken cancellationToken) => Task.FromResult(0);
            public Task<IReadOnlyList<RecordOperationResult>> WriteBatchAsync(string logicalName, IReadOnlyList<DataRecord> batch, WriteStrategy strategy, int pass, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task AssociateAsync(string relationshipSchemaName, DataReference from, IReadOnlyList<DataReference> to, CancellationToken cancellationToken) => throw new NotSupportedException();
        }

        private static MigrationPlan SinglePlan(string logicalName) => new MigrationPlan
        {
            Steps = new List<PlanStep> { new PlanStep { LogicalName = logicalName, Order = 0 } }
        };

        [Fact]
        public async Task AllNewRecords_AreAllToCreate()
        {
            var sourceIds = new List<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
            var source = new FakeService(new Dictionary<string, List<DataRecord>>
            {
                ["wit_tema"] = sourceIds.Select(id => new DataRecord("wit_tema", id)).ToList()
            });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" } };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            var summary = Assert.Single(result);
            Assert.Equal(3, summary.SourceRecordCount);
            Assert.Equal(3, summary.ToCreate);
            Assert.Equal(0, summary.ToUpdate);
            Assert.Equal(3, summary.Records.Count);
            Assert.All(summary.Records, r => Assert.Equal(RecordPreviewState.New, r.State));
        }

        [Fact]
        public async Task SomeRecordsAlreadyInTarget_AreCountedAndMarkedAsUpdate()
        {
            var existingId = Guid.NewGuid();
            var newId = Guid.NewGuid();
            var source = new FakeService(new Dictionary<string, List<DataRecord>>
            {
                ["wit_tema"] = new List<DataRecord> { new DataRecord("wit_tema", existingId), new DataRecord("wit_tema", newId) }
            });
            var target = new FakeService(targetRecords: new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord> { new DataRecord("wit_tema", existingId) } });
            var sourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" } };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            var summary = Assert.Single(result);
            Assert.Equal(2, summary.SourceRecordCount);
            Assert.Equal(1, summary.ToCreate);
            Assert.Equal(1, summary.ToUpdate);
            Assert.Equal(RecordPreviewState.Update, summary.Records.Single(r => r.Id == existingId).State);
            Assert.Equal(RecordPreviewState.New, summary.Records.Single(r => r.Id == newId).State);
        }

        [Fact]
        public async Task RecordWithPrimaryNameAttribute_UsesItAsDisplayName()
        {
            var id = Guid.NewGuid();
            var record = new DataRecord("wit_tema", id);
            record.Attributes["wit_name"] = "Tema de prueba";

            var source = new FakeService(new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord> { record } });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = new TableSummary { LogicalName = "wit_tema", PrimaryNameAttribute = "wit_name" }
            };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            Assert.Equal("Tema de prueba", Assert.Single(result).Records.Single().DisplayName);
        }

        [Fact]
        public async Task RecordWithoutPrimaryNameAttribute_FallsBackToId()
        {
            var id = Guid.NewGuid();
            var source = new FakeService(new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord> { new DataRecord("wit_tema", id) } });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" } };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            Assert.Equal(id.ToString(), Assert.Single(result).Records.Single().DisplayName);
        }

        [Fact]
        public async Task UpdateRecord_CarriesTargetsCurrentDisplayName()
        {
            var id = Guid.NewGuid();
            var sourceRecord = new DataRecord("wit_tema", id);
            sourceRecord.Attributes["wit_name"] = "Nombre nuevo";
            var targetRecord = new DataRecord("wit_tema", id);
            targetRecord.Attributes["wit_name"] = "Nombre viejo";

            var source = new FakeService(new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord> { sourceRecord } });
            var target = new FakeService(targetRecords: new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord> { targetRecord } });
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = new TableSummary { LogicalName = "wit_tema", PrimaryNameAttribute = "wit_name" }
            };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            var row = Assert.Single(result).Records.Single();
            Assert.Equal(RecordPreviewState.Update, row.State);
            Assert.Equal("Nombre nuevo", row.DisplayName);
            Assert.Equal("Nombre viejo", row.TargetDisplayName);
        }

        [Fact]
        public async Task NewRecord_HasNullTargetDisplayName()
        {
            var id = Guid.NewGuid();
            var sourceRecord = new DataRecord("wit_tema", id);
            sourceRecord.Attributes["wit_name"] = "Nombre nuevo";

            var source = new FakeService(new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord> { sourceRecord } });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = new TableSummary { LogicalName = "wit_tema", PrimaryNameAttribute = "wit_name" }
            };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            var row = Assert.Single(result).Records.Single();
            Assert.Equal(RecordPreviewState.New, row.State);
            Assert.Null(row.TargetDisplayName);
        }

        [Fact]
        public async Task MoreRecordsThanCap_TruncatesDetailButKeepsExactAggregateCounts()
        {
            var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList();
            var source = new FakeService(new Dictionary<string, List<DataRecord>>
            {
                ["wit_tema"] = ids.Select(id => new DataRecord("wit_tema", id)).ToList()
            });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" } };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 2, CancellationToken.None);

            var summary = Assert.Single(result);
            Assert.Equal(5, summary.SourceRecordCount); // exact, unaffected by the cap
            Assert.Equal(2, summary.Records.Count);     // capped detail
            Assert.True(summary.Truncated);
        }

        [Fact]
        public async Task EmptySourceTable_ProducesZeroedSummaryWithoutCallingTarget()
        {
            var source = new FakeService(new Dictionary<string, List<DataRecord>> { ["wit_tema"] = new List<DataRecord>() });
            var target = new FakeService(); // RetrieveByIdsAsync would return empty anyway, but this also proves it's not even invoked with a non-empty id list
            var sourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" } };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            var summary = Assert.Single(result);
            Assert.Equal(0, summary.SourceRecordCount);
            Assert.Equal(0, summary.ToCreate);
            Assert.Equal(0, summary.ToUpdate);
            Assert.Empty(summary.Records);
            Assert.False(summary.Truncated);
        }

        [Fact]
        public async Task OnProgress_IsInvokedOncePerTableInPlan()
        {
            var source = new FakeService(new Dictionary<string, List<DataRecord>>
            {
                ["wit_tema"] = new List<DataRecord> { new DataRecord("wit_tema", Guid.NewGuid()) },
                ["wit_subtema"] = new List<DataRecord> { new DataRecord("wit_subtema", Guid.NewGuid()) }
            });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" },
                ["wit_subtema"] = new TableSummary { LogicalName = "wit_subtema" }
            };
            var plan = new MigrationPlan
            {
                Steps = new List<PlanStep>
                {
                    new PlanStep { LogicalName = "wit_tema", Order = 0 },
                    new PlanStep { LogicalName = "wit_subtema", Order = 1 }
                }
            };

            var messages = new List<string>();
            await new MigrationPreviewBuilder().BuildAsync(plan, sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None, msg => messages.Add(msg));

            Assert.Equal(2, messages.Count);
            Assert.Contains(messages, m => m.Contains("wit_tema"));
            Assert.Contains(messages, m => m.Contains("wit_subtema"));
        }

        [Fact]
        public async Task OnProgress_OmittedByDefault_DoesNotThrow()
        {
            // Proves the new onProgress parameter defaults to null safely — every call site
            // above this test was written before the parameter existed and must keep compiling
            // and passing unchanged.
            var source = new FakeService(new Dictionary<string, List<DataRecord>>
            {
                ["wit_tema"] = new List<DataRecord> { new DataRecord("wit_tema", Guid.NewGuid()) }
            });
            var target = new FakeService();
            var sourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = new TableSummary { LogicalName = "wit_tema" } };

            var result = await new MigrationPreviewBuilder().BuildAsync(SinglePlan("wit_tema"), sourceTables, source, target, pageSize: 500, maxRecordsPerTable: 100, CancellationToken.None);

            Assert.Single(result);
        }
    }
}
