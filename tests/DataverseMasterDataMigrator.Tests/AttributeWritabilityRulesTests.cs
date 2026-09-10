using System.Collections.Generic;
using System.Linq;
using DataverseMasterDataMigrator.Core.Migration;
using DataverseMasterDataMigrator.Core.Models;
using Xunit;

namespace DataverseMasterDataMigrator.Tests
{
    /// <summary>
    /// Regresión de un crash real: "Retrieve can only return columns that are valid for read.
    /// Column: subscriptionid. Entity: contact" — Dataverse rechaza de plano cualquier Retrieve
    /// que pida en el ColumnSet un atributo con IsValidForRead = false, aunque ese mismo
    /// atributo sea válido para create/update (subscriptionid, el usado por la sincronización
    /// offline/Outlook, es exactamente así). GetWritableAttributes nunca chequeaba ese flag por
    /// separado — solo Create/Update — así que un atributo así terminaba en la lista de columnas
    /// a leer de Source y tumbaba la tabla entera antes de escribir un solo registro.
    /// </summary>
    public class AttributeWritabilityRulesTests
    {
        private static TableSummary Table(params AttributeSummary[] attributes) => new TableSummary
        {
            LogicalName = "contact",
            PrimaryIdAttribute = "contactid",
            Attributes = attributes.ToList()
        };

        private static ProfileEntity Entity() => new ProfileEntity { LogicalName = "contact" };

        [Fact]
        public void GetWritableAttributes_ExcludesAttributeNotValidForRead_EvenIfValidForCreateAndUpdate()
        {
            var readOnlyForSync = new AttributeSummary
            {
                LogicalName = "subscriptionid",
                Kind = AttributeKind.Primitive,
                IsValidForCreate = true,
                IsValidForUpdate = true,
                IsValidForRead = false
            };
            var normal = new AttributeSummary
            {
                LogicalName = "firstname",
                Kind = AttributeKind.Primitive,
                IsValidForCreate = true,
                IsValidForUpdate = true,
                IsValidForRead = true
            };

            var result = AttributeWritabilityRules.GetWritableAttributes(Table(readOnlyForSync, normal), Entity(), restoreState: true);

            Assert.DoesNotContain(result, a => a.LogicalName == "subscriptionid");
            Assert.Contains(result, a => a.LogicalName == "firstname");
        }

        [Fact]
        public void GetWritableAttributes_DefaultIsValidForRead_IncludesNormalAttribute()
        {
            // AttributeSummary.IsValidForRead default es true — un atributo construido sin
            // setearlo explícitamente (todo el código/tests existentes antes de este fix) debe
            // seguir comportándose exactamente igual que antes.
            var attribute = new AttributeSummary
            {
                LogicalName = "lastname",
                Kind = AttributeKind.Primitive,
                IsValidForCreate = true,
                IsValidForUpdate = true
            };

            var result = AttributeWritabilityRules.GetWritableAttributes(Table(attribute), Entity(), restoreState: true);

            Assert.Contains(result, a => a.LogicalName == "lastname");
        }
    }
}
