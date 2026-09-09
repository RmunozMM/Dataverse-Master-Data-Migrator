using System;
using System.Collections.Generic;

namespace DataverseMasterDataMigrator.Core.Models
{
    /// <summary>
    /// Representación de un registro Dataverse independiente del SDK. Equivalente conceptual
    /// a <c>Microsoft.Xrm.Sdk.Entity</c>, pero definido aquí para que el Core nunca referencie
    /// el SDK directamente (ver ARCHITECTURE.md sección 1).
    /// </summary>
    public sealed class DataRecord
    {
        public DataRecord(string logicalName, Guid id)
        {
            if (string.IsNullOrWhiteSpace(logicalName))
                throw new ArgumentException("logicalName es obligatorio.", nameof(logicalName));

            LogicalName = logicalName;
            Id = id;
            Attributes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public string LogicalName { get; }

        public Guid Id { get; }

        /// <summary>
        /// Valores por nombre lógico de atributo. Los lookups se representan como
        /// <see cref="DataReference"/>, nunca como el tipo nativo del SDK.
        /// </summary>
        public IDictionary<string, object> Attributes { get; }

        public bool TryGetValue<T>(string attributeName, out T value)
        {
            if (Attributes.TryGetValue(attributeName, out var raw) && raw != null && typeof(T).IsInstanceOfType(raw))
            {
                value = (T)raw;
                return true;
            }

            value = default(T);
            return false;
        }
    }

    /// <summary>
    /// Equivalente desacoplado de <c>Microsoft.Xrm.Sdk.EntityReference</c>.
    /// </summary>
    public sealed class DataReference : IEquatable<DataReference>
    {
        public DataReference(string logicalName, Guid id)
        {
            if (string.IsNullOrWhiteSpace(logicalName))
                throw new ArgumentException("logicalName es obligatorio.", nameof(logicalName));

            LogicalName = logicalName;
            Id = id;
        }

        public string LogicalName { get; }

        public Guid Id { get; }

        public bool Equals(DataReference other)
        {
            if (ReferenceEquals(other, null)) return false;
            return Id == other.Id &&
                   string.Equals(LogicalName, other.LogicalName, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj) => Equals(obj as DataReference);

        public override int GetHashCode()
        {
            unchecked
            {
                return (LogicalName.ToLowerInvariant().GetHashCode() * 397) ^ Id.GetHashCode();
            }
        }

        public override string ToString() => $"{LogicalName}:{Id}";
    }
}
