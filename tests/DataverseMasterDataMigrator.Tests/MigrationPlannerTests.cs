using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class MigrationPlannerTests
    {
        private static AttributeSummary Lookup(string logicalName, params string[] targets) => new AttributeSummary
        {
            LogicalName = logicalName,
            Kind = AttributeKind.Lookup,
            LookupTargets = targets
        };

        private static TableSummary Table(string logicalName, params AttributeSummary[] attributes) => new TableSummary
        {
            LogicalName = logicalName,
            DisplayName = logicalName,
            Attributes = attributes
        };

        [Fact]
        public void CreatePlan_TemaSubtemaDetalle_OrdersByDependency()
        {
            var metadata = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema"),
                ["wit_subtema"] = Table("wit_subtema", Lookup("wit_temaid", "wit_tema")),
                ["wit_detalle"] = Table("wit_detalle", Lookup("wit_subtemaid", "wit_subtema"))
            };

            var profile = new MigrationProfile
            {
                Name = "Maestros",
                Entities =
                {
                    new ProfileEntity { LogicalName = "wit_detalle", PreferredOrder = 30 },
                    new ProfileEntity { LogicalName = "wit_tema", PreferredOrder = 10 },
                    new ProfileEntity { LogicalName = "wit_subtema", PreferredOrder = 20 }
                }
            };

            var plan = MigrationPlanner.CreatePlan(profile, metadata);

            var order = plan.Steps.OrderBy(s => s.Order).Select(s => s.LogicalName).ToList();
            Assert.Equal(new[] { "wit_tema", "wit_subtema", "wit_detalle" }, order);
            Assert.False(plan.HasCycles);
        }

        [Fact]
        public void CreatePlan_DisabledEntity_IsExcludedFromPlan()
        {
            var metadata = new Dictionary<string, TableSummary>
            {
                ["wit_tema"] = Table("wit_tema"),
                ["wit_subtema"] = Table("wit_subtema", Lookup("wit_temaid", "wit_tema"))
            };

            var profile = new MigrationProfile
            {
                Name = "Maestros",
                Entities =
                {
                    new ProfileEntity { LogicalName = "wit_tema", Enabled = true },
                    new ProfileEntity { LogicalName = "wit_subtema", Enabled = false }
                }
            };

            var plan = MigrationPlanner.CreatePlan(profile, metadata);

            Assert.Single(plan.Steps);
            Assert.Equal("wit_tema", plan.Steps[0].LogicalName);
        }

        [Fact]
        public void CreatePlan_WithCycle_ReportsCycleButStillProducesFullPlan()
        {
            var metadata = new Dictionary<string, TableSummary>
            {
                ["a"] = Table("a", Lookup("b_id", "b")),
                ["b"] = Table("b", Lookup("c_id", "c")),
                ["c"] = Table("c", Lookup("a_id", "a"))
            };

            var profile = new MigrationProfile
            {
                Name = "Con ciclo",
                Entities =
                {
                    new ProfileEntity { LogicalName = "a" },
                    new ProfileEntity { LogicalName = "b" },
                    new ProfileEntity { LogicalName = "c" }
                }
            };

            var plan = MigrationPlanner.CreatePlan(profile, metadata);

            Assert.True(plan.HasCycles);
            Assert.Equal(3, plan.Steps.Count);
            Assert.All(plan.Steps, s => Assert.True(s.IsPartOfCycle));
        }
    }
}
