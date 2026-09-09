using System.Collections.Generic;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Planning;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class DependencyGraphTests
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
        public void Build_CreatesEdge_OnlyBetweenSelectedTables()
        {
            // subtema -> tema (incluida), subtema -> "tabla_externa" (NO incluida en el perfil)
            var subtema = Table("wit_subtema", Lookup("wit_temaid", "wit_tema"), Lookup("wit_externoid", "tabla_externa"));
            var tema = Table("wit_tema");

            var selected = new Dictionary<string, TableSummary>
            {
                ["wit_subtema"] = subtema,
                ["wit_tema"] = tema
            };

            var graph = DependencyGraphBuilder.Build(selected);

            Assert.Contains("wit_tema", graph.DependenciesOf("wit_subtema"));
            Assert.DoesNotContain("tabla_externa", graph.DependenciesOf("wit_subtema"));
            Assert.Equal(2, graph.Nodes.Count);
        }

        [Fact]
        public void Build_TableWithoutLookups_HasNoDependencies()
        {
            var selected = new Dictionary<string, TableSummary> { ["wit_tema"] = Table("wit_tema") };

            var graph = DependencyGraphBuilder.Build(selected);

            Assert.Empty(graph.DependenciesOf("wit_tema"));
        }

        [Fact]
        public void Build_PolymorphicLookup_AddsEdgeToEachSelectedTarget()
        {
            var caso = Table("incident", Lookup("customerid", "account", "contact"));
            var selected = new Dictionary<string, TableSummary>
            {
                ["incident"] = caso,
                ["account"] = Table("account"),
                ["contact"] = Table("contact")
            };

            var graph = DependencyGraphBuilder.Build(selected);

            Assert.Contains("account", graph.DependenciesOf("incident"));
            Assert.Contains("contact", graph.DependenciesOf("incident"));
        }
    }
}
