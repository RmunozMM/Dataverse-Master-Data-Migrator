using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Abstractions
{
    /// <summary>
    /// Puerto hacia la metadata de un ambiente (Source o Target). La implementación real vive
    /// en la capa XrmToolBox y usa <c>IOrganizationService</c> / <c>RetrieveMetadataChangesRequest</c>.
    /// Una misma implementación sirve para Source y para Target; quién la instancia decide con
    /// qué conexión.
    /// </summary>
    public interface IDataverseMetadataProvider
    {
        /// <summary>
        /// Listado liviano de tablas (sin atributos/relaciones) para poblar el buscador de la
        /// sección TABLES. Debe evitar pedir metadata pesada (sección 8: "no cargar metadata
        /// pesada innecesariamente al inicio").
        /// </summary>
        Task<IReadOnlyList<TableSummary>> ListTablesAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Metadata completa de una tabla puntual (atributos, relaciones, capacidades bulk).
        /// Se llama bajo demanda: al armar un perfil sobre tablas ya elegidas, o durante Preflight.
        /// </summary>
        Task<TableSummary> GetTableDetailAsync(string logicalName, CancellationToken cancellationToken);

        /// <summary>Identificador estable del ambiente (OrganizationId), usado para bloquear
        /// Source == Target (sección 5 del requerimiento).</summary>
        Task<string> GetOrganizationIdAsync(CancellationToken cancellationToken);
    }
}
