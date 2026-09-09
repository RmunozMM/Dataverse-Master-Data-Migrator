using System;
using DataverseMasterDataMigrator.Core.Models;
using DataverseMasterDataMigrator.Core.Profiles;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    public class ProfileSerializationTests
    {
        private static MigrationProfile SampleProfile()
        {
            var profile = new MigrationProfile
            {
                Name = "Maestros Customer Service",
                Description = "Tablas maestras utilizadas por Customer Service Workspace"
            };
            profile.Entities.Add(new ProfileEntity
            {
                LogicalName = "wit_tema",
                DisplayName = "Tema",
                PreferredOrder = 10
            });
            return profile;
        }

        [Fact]
        public void RoundTrip_PreservesAllFields()
        {
            var original = SampleProfile();

            var json = MigrationProfileSerializer.Serialize(original);
            var ok = MigrationProfileSerializer.TryDeserialize(json, out var restored, out var error);

            Assert.True(ok, error);
            Assert.Equal(original.Id, restored.Id);
            Assert.Equal(original.Name, restored.Name);
            Assert.Single(restored.Entities);
            Assert.Equal("wit_tema", restored.Entities[0].LogicalName);
            Assert.True(restored.Options.PreserveSourceGuid);
        }

        [Fact]
        public void TryDeserialize_EmptyString_FailsGracefully()
        {
            var ok = MigrationProfileSerializer.TryDeserialize("", out var profile, out var error);

            Assert.False(ok);
            Assert.Null(profile);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }

        [Fact]
        public void TryDeserialize_MalformedJson_DoesNotThrow()
        {
            var ok = MigrationProfileSerializer.TryDeserialize("{ this is not json", out var profile, out var error);

            Assert.False(ok);
            Assert.Null(profile);
            Assert.Contains("JSON inválido", error);
        }

        [Fact]
        public void TryDeserialize_FutureSchemaVersion_IsRejectedExplicitly()
        {
            var json = @"{ ""schemaVersion"": 999, ""id"": """ + Guid.NewGuid() + @""", ""name"": ""x"", ""entities"": [] }";

            var ok = MigrationProfileSerializer.TryDeserialize(json, out var profile, out var error);

            Assert.False(ok);
            Assert.Contains("versión más reciente", error);
        }

        [Fact]
        public void TryDeserialize_DuplicateEntities_IsRejected()
        {
            var profile = SampleProfile();
            profile.Entities.Add(new ProfileEntity { LogicalName = "wit_tema" });
            var json = MigrationProfileSerializer.Serialize(profile);

            var ok = MigrationProfileSerializer.TryDeserialize(json, out _, out var error);

            Assert.False(ok);
            Assert.Contains("repetida", error);
        }
    }
}
