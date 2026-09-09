using DataverseMasterDataMigrator.Core.Models;

namespace DataverseMasterDataMigrator.Core.Migration
{
    /// <summary>
    /// Decides the best write strategy for a table without knowing the concrete SOAP/Web API
    /// message (section 18: "the user shouldn't have to know this decision").
    /// </summary>
    public static class WriteStrategySelector
    {
        public static WriteStrategy SelectFor(TableSummary table)
        {
            if (table.SupportsCreateMultiple && table.SupportsUpdateMultiple)
                return WriteStrategy.BulkCreateThenUpdate;

            // ExecuteMultiple wrapping individual UpsertRequest calls is the default fallback:
            // it needs no existence pre-check (Upsert resolves create-vs-update itself) and
            // still batches everything into one client round trip. The caller may downgrade to
            // IndividualRequests at runtime if ExecuteMultiple itself turns out to be disabled
            // for the organization.
            return WriteStrategy.ExecuteMultipleUpsert;
        }
    }
}
