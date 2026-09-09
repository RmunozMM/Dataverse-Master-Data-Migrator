using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Abstractions
{
    /// <summary>
    /// Transforma un registro antes de escribirlo en Target. En esta versión, la única
    /// implementación es <c>IdentityRecordTransformer</c> (no-op). Existe para que el futuro
    /// plugin UMayor pueda anonimizar sin tocar el motor genérico (sección 23).
    /// </summary>
    public interface IRecordTransformer
    {
        DataRecord Transform(DataRecord source, TableSummary tableMetadata);
    }

    public sealed class IdentityRecordTransformer : IRecordTransformer
    {
        public DataRecord Transform(DataRecord source, TableSummary tableMetadata) => source;
    }

    /// <summary>
    /// Resuelve si un lookup que apunta FUERA del perfil existe en Target. La implementación
    /// default consulta por GUID igual (sección 12). Un futuro proveedor UMayor podría resolver
    /// por un mapping alternativo entre ambientes.
    /// </summary>
    public interface IReferenceResolver
    {
        Task<bool> ExistsInTargetAsync(DataReference externalReference, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Abstrae de dónde viene un perfil de migración. Hoy: archivo JSON local
    /// (<c>Profiles.MigrationProfileRepository</c>). El plugin UMayor podría, en el futuro,
    /// proveer un perfil generado en código en vez de leído de disco.
    /// </summary>
    public interface IMigrationProfileProvider
    {
        Task<MigrationProfile> GetProfileAsync(string identifier, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Decide qué registros de una tabla entran en el alcance de la migración. En V1 siempre
    /// "todos" (ver <c>Filter == null</c> en el perfil). El plugin UMayor implementará aquí el
    /// recorrido del grafo desde un Contact raíz.
    /// </summary>
    public interface IRecordSelector
    {
        Task<IReadOnlyList<DataReference>> SelectAsync(
            ProfileEntity entityConfig,
            CancellationToken cancellationToken);
    }

    public interface IExecutionLogger
    {
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
    }
}
