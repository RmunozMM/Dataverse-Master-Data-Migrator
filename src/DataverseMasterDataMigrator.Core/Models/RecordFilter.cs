using System.Collections.Generic;

namespace DataverseMasterDataMigrator.Core.Models
{
    public enum FilterOperator
    {
        Equal,
        NotEqual,
        In
    }

    /// <summary>Una condición sobre un atributo (p.ej. "customerid = X"). <see cref="Value"/> se
    /// compara tal cual contra el valor real del registro; para <see cref="FilterOperator.In"/>
    /// debe ser una colección de valores posibles.</summary>
    public sealed class FilterCondition
    {
        public string AttributeName { get; set; }
        public FilterOperator Operator { get; set; } = FilterOperator.Equal;
        public object Value { get; set; }
    }

    public enum FilterLogicalOperator
    {
        And,
        Or
    }

    /// <summary>
    /// Filtro declarativo para acotar qué registros de una tabla entran en el alcance de una
    /// operación (Preflight, Preview Data, Execute) — activa <c>ProfileEntity.Filter</c>.
    /// <c>null</c>, o vacío (sin <see cref="Conditions"/> ni <see cref="SubFilters"/>), significa
    /// "sin filtro, todos los registros" — el comportamiento de siempre. Soporta anidamiento
    /// (<see cref="SubFilters"/>) para árboles booleanos arbitrarios, aunque en esta versión
    /// ningún llamador real todavía genera perfiles con <c>Filter</c> seteado — existe para que
    /// un selector futuro no necesite cambiar este modelo otra vez.
    /// </summary>
    public sealed class RecordFilter
    {
        public FilterLogicalOperator LogicalOperator { get; set; } = FilterLogicalOperator.And;
        public List<FilterCondition> Conditions { get; set; } = new List<FilterCondition>();
        public List<RecordFilter> SubFilters { get; set; } = new List<RecordFilter>();
    }
}
