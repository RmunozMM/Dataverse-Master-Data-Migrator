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
    }
}
