using System.Collections.Generic;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Validation;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class PreflightValidatorTests
    {
        private static TableSummary Table(string logicalName) => new TableSummary
        {
            LogicalName = logicalName,
            DisplayName = logicalName
        };

        private static MigrationProfile ProfileWith(params string[] logicalNames)
        {
            var profile = new MigrationProfile { Name = "Test" };
            foreach (var name in logicalNames)
                profile.Entities.Add(new ProfileEntity { LogicalName = name });
            return profile;
        }

        [Fact]
        public void SourceEqualsTarget_IsBlockingError()
        {
            var profile = ProfileWith("wit_tema");
            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "org-123",
                TargetOrganizationId = "org-123",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") }
            };

            var result = PreflightValidator.Validate(context);

            Assert.False(result.ReadyToExecute);
            Assert.Contains(result.Issues, i => i.Code == "SOURCE_EQUALS_TARGET" && i.Severity == IssueSeverity.Error);
        }

        [Fact]
        public void TableMissingInTarget_IsBlockingError()
        {
            var profile = ProfileWith("wit_tema");
            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "org-source",
                TargetOrganizationId = "org-target",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                TargetTables = new Dictionary<string, TableSummary>() // no existe en target
            };

            var result = PreflightValidator.Validate(context);

            Assert.False(result.ReadyToExecute);
            Assert.Contains(result.Issues, i => i.Code == "TABLE_NOT_IN_TARGET");
        }

        [Fact]
        public void ValidScenario_NoIssues_IsReadyToExecute()
        {
            var profile = ProfileWith("wit_tema");
            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "org-source",
                TargetOrganizationId = "org-target",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") }
            };

            var result = PreflightValidator.Validate(context);

            Assert.True(result.ReadyToExecute);
            Assert.False(result.HasErrors);
        }

        [Fact]
        public void RequiredLookupUnresolved_WithFailPreflightPolicy_IsError()
        {
            var profile = ProfileWith("wit_tema");
            profile.Options.RequiredLookupPolicy = LookupPolicy.FailPreflight;

            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "s",
                TargetOrganizationId = "t",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                UnresolvedExternalLookups = new List<UnresolvedLookupFinding>
                {
                    new UnresolvedLookupFinding
                    {
                        TableLogicalName = "wit_tema",
                        AttributeLogicalName = "ownerid",
                        IsRequired = true,
                        AffectedRecordCount = 3
                    }
                }
            };

            var result = PreflightValidator.Validate(context);

            Assert.False(result.ReadyToExecute);
            Assert.Contains(result.Issues, i => i.Code == "REQUIRED_LOOKUP_UNRESOLVED" && i.Severity == IssueSeverity.Error);
        }

        [Fact]
        public void OptionalLookupUnresolved_IsWarningNotError()
        {
            var profile = ProfileWith("wit_tema");

            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "s",
                TargetOrganizationId = "t",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                UnresolvedExternalLookups = new List<UnresolvedLookupFinding>
                {
                    new UnresolvedLookupFinding
                    {
                        TableLogicalName = "wit_tema",
                        AttributeLogicalName = "wit_relacionopcionalid",
                        IsRequired = false,
                        AffectedRecordCount = 1
                    }
                }
            };

            var result = PreflightValidator.Validate(context);

            Assert.True(result.ReadyToExecute); // warnings no bloquean
            Assert.Contains(result.Issues, i => i.Code == "OPTIONAL_LOOKUP_UNRESOLVED" && i.Severity == IssueSeverity.Warning);
        }

        [Fact]
        public void AttributeMissingInTarget_IsWarningNotError()
        {
            // Regression test for a real bug: this went undetected until Execute, where it
            // surfaced as "entity doesn't contain attribute ... NameMapping = 'Logical'" for 37
            // of 56 failed records in one run — Target's solution was behind Source's.
            var sourceTable = new TableSummary
            {
                LogicalName = "wit_tema",
                DisplayName = "wit_tema",
                Attributes = new List<AttributeSummary>
                {
                    new AttributeSummary { LogicalName = "wit_nuevocampo", Kind = AttributeKind.Primitive, IsValidForCreate = true, IsValidForUpdate = true }
                }
            };
            var targetTable = new TableSummary { LogicalName = "wit_tema", DisplayName = "wit_tema" }; // no tiene el atributo

            var profile = ProfileWith("wit_tema");
            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "s",
                TargetOrganizationId = "t",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = sourceTable },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = targetTable }
            };

            var result = PreflightValidator.Validate(context);

            Assert.True(result.ReadyToExecute); // advertencia, no bloquea
            Assert.Contains(result.Issues, i => i.Code == "ATTRIBUTE_NOT_IN_TARGET" && i.Severity == IssueSeverity.Warning);
        }

        [Fact]
        public void AttributePresentInTarget_NoSchemaDriftWarning()
        {
            var attr = new AttributeSummary { LogicalName = "wit_campo", Kind = AttributeKind.Primitive, IsValidForCreate = true, IsValidForUpdate = true };
            var sourceTable = new TableSummary { LogicalName = "wit_tema", DisplayName = "wit_tema", Attributes = new List<AttributeSummary> { attr } };
            var targetTable = new TableSummary { LogicalName = "wit_tema", DisplayName = "wit_tema", Attributes = new List<AttributeSummary> { attr } };

            var profile = ProfileWith("wit_tema");
            var context = new PreflightContext
            {
                Profile = profile,
                SourceOrganizationId = "s",
                TargetOrganizationId = "t",
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = sourceTable },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = targetTable }
            };

            var result = PreflightValidator.Validate(context);

            Assert.DoesNotContain(result.Issues, i => i.Code == "ATTRIBUTE_NOT_IN_TARGET");
        }

        [Fact]
        public void NoTablesEnabled_IsBlockingError()
        {
            var profile = ProfileWith("wit_tema");
            profile.Entities[0].Enabled = false;

            var context = new PreflightContext
            {
                Profile = profile,
                SourceTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") },
                TargetTables = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") }
            };

            var result = PreflightValidator.Validate(context);

            Assert.False(result.ReadyToExecute);
            Assert.Contains(result.Issues, i => i.Code == "NO_TABLES_SELECTED");
        }
    }
}
