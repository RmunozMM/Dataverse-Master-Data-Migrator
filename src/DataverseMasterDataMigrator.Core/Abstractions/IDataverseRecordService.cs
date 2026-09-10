using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Abstractions
{
    public sealed class RecordPage
    {
        public IReadOnlyList<DataRecord> Records { get; set; } = Array.Empty<DataRecord>();
        public bool HasMore { get; set; }
        public string NextPageToken { get; set; }
    }

    /// <summary>
    /// Puerto hacia lectura/escritura de datos. El Core siempre pagina (sección 10: "no asumir
    /// nunca que existen menos de 5.000 registros") y nunca asume que un mensaje bulk existe:
    /// primero pregunta capacidades vía <see cref="IDataverseMetadataProvider"/>.
    /// </summary>
    public interface IDataverseRecordService
    {
        Task<RecordPage> RetrievePageAsync(
            string logicalName,
            IReadOnlyList<string> columns,
            string pageToken,
            int pageSize,
            CancellationToken cancellationToken);

        /// <summary>
        /// Igual que <see cref="RetrievePageAsync"/> pero acotado por <paramref name="filter"/> —
        /// usado cuando <c>ProfileEntity.Filter</c> está seteado. <paramref name="filter"/> puede
        /// ser <c>null</c>, en cuyo caso el comportamiento debe ser idéntico a
        /// <see cref="RetrievePageAsync"/>.
        /// </summary>
        Task<RecordPage> RetrieveFilteredPageAsync(
            string logicalName,
            IReadOnlyList<string> columns,
            RecordFilter filter,
            string pageToken,
            int pageSize,
            CancellationToken cancellationToken);

        /// <summary>
        /// Targeted read of specific records by id — used by Retry Failed (ARCHITECTURE.md
        /// sección 7) so it can re-fetch just the previously-failed records from Source without
        /// re-paging tables that already completed successfully. Records that no longer exist are
        /// simply absent from the result, never an error.
        /// </summary>
        Task<IReadOnlyList<DataRecord>> RetrieveByIdsAsync(
            string logicalName,
            IReadOnlyList<Guid> ids,
            IReadOnlyList<string> columns,
            CancellationToken cancellationToken);

        /// <summary>Existencia puntual, usada por el resolver de lookups externos al perfil
        /// (sección 12: verificar si el GUID referenciado existe en Target).</summary>
        Task<bool> ExistsAsync(DataReference reference, CancellationToken cancellationToken);

        Task<int> GetApproximateCountAsync(string logicalName, CancellationToken cancellationToken);

        /// <summary>
        /// Escribe un lote homogéneo (misma tabla, misma operación) usando la mejor estrategia
        /// disponible para esa tabla. La decisión de QUÉ estrategia ya la tomó el Core
        /// (ver <see cref="Migration.WriteStrategy"/>); este método solo la ejecuta.
        /// </summary>
        Task<IReadOnlyList<Models.RecordOperationResult>> WriteBatchAsync(
            string logicalName,
            IReadOnlyList<DataRecord> batch,
            Migration.WriteStrategy strategy,
            int pass,
            CancellationToken cancellationToken);

        Task AssociateAsync(
            string relationshipSchemaName,
            DataReference from,
            IReadOnlyList<DataReference> to,
            CancellationToken cancellationToken);

        /// <summary>
        /// Elimina un lote de registros por id. Usado por herramientas de limpieza de datos de
        /// prueba (Umayor Test Data Seeder) para dejar Target en un estado limpio entre corridas —
        /// el migrador genérico no lo invoca hoy, pero cualquier implementador del puerto debe
        /// soportarlo. Debe ser idempotente: intentar borrar un registro que ya no existe cuenta
        /// como éxito (Outcome=Succeeded), no como falla — el objetivo ("este registro no está en
        /// Target") ya se cumple.
        /// </summary>
        Task<IReadOnlyList<Models.RecordOperationResult>> DeleteBatchAsync(
            string logicalName,
            IReadOnlyList<Guid> ids,
            CancellationToken cancellationToken);
    }
}
