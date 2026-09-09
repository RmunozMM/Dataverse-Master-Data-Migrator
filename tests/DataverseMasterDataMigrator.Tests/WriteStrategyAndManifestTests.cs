using System;
using System.IO;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class WriteStrategySelectorTests
    {
        [Fact]
        public void CreateAndUpdateMultipleSupported_UsesBulkCreateThenUpdate()
        {
            var table = new TableSummary { SupportsCreateMultiple = true, SupportsUpdateMultiple = true };

            var strategy = WriteStrategySelector.SelectFor(table);

            Assert.Equal(WriteStrategy.BulkCreateThenUpdate, strategy);
        }

        [Fact]
        public void NoMultipleSupport_FallsBackToExecuteMultipleUpsert()
        {
            var table = new TableSummary { SupportsCreateMultiple = false, SupportsUpdateMultiple = false };

            var strategy = WriteStrategySelector.SelectFor(table);

            Assert.Equal(WriteStrategy.ExecuteMultipleUpsert, strategy);
        }

        [Fact]
        public void OnlyCreateMultipleSupported_StillFallsBackToExecuteMultipleUpsert()
        {
            var table = new TableSummary { SupportsCreateMultiple = true, SupportsUpdateMultiple = false };

            var strategy = WriteStrategySelector.SelectFor(table);

            Assert.Equal(WriteStrategy.ExecuteMultipleUpsert, strategy);
        }
    }

    public class ExecutionManifestStoreTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "dmdm_manifest_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }

        [Fact]
        public void Save_Then_Load_RoundTrips()
        {
            var store = new ExecutionManifestStore(_folder);
            var manifest = new ExecutionManifest
            {
                ProfileName = "Maestros Comercial",
                Status = ExecutionStatus.Running
            };
            manifest.Tables.Add(new TableExecutionResult { LogicalName = "wit_tema", Created = 5 });

            store.Save(manifest);
            var loaded = store.Load(manifest.ExecutionId);

            Assert.NotNull(loaded);
            Assert.Equal(manifest.ExecutionId, loaded.ExecutionId);
            Assert.Single(loaded.Tables);
            Assert.Equal(5, loaded.Tables[0].Created);
        }

        [Fact]
        public void Load_UnknownExecutionId_ReturnsNull()
        {
            var store = new ExecutionManifestStore(_folder);

            var loaded = store.Load(Guid.NewGuid());

            Assert.Null(loaded);
        }

        [Fact]
        public void Save_Overwrites_PreviousStateOfSameExecution()
        {
            var store = new ExecutionManifestStore(_folder);
            var manifest = new ExecutionManifest { Status = ExecutionStatus.Running };
            store.Save(manifest);

            manifest.Status = ExecutionStatus.CompletedWithWarnings;
            manifest.FinishedUtc = DateTime.UtcNow;
            store.Save(manifest);

            var loaded = store.Load(manifest.ExecutionId);
            Assert.Equal(ExecutionStatus.CompletedWithWarnings, loaded.Status);
        }
    }
}
