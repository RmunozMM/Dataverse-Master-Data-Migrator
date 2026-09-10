using System;
using System.Collections.Generic;

namespace DataverseMasterDataMigrator.Core.Models
{
    public enum ExecutionStatus
    {
        Pending,
        Running,
        Completed,
        CompletedWithWarnings,
        Failed,
        Cancelled
    }

    public enum RecordOperation
    {
        Create,
        Update,
        AssociateManyToMany,
        RestoreStateStatus,
        Delete
    }

    public enum RecordOutcome
    {
        Succeeded,
        Failed,
        Skipped
    }

    /// <summary>
    /// Resultado de una operación sobre UN registro. Deliberadamente no incluye los atributos
    /// del registro (sección 20: "no guardar payloads completos").
    /// </summary>
    public sealed class RecordOperationResult
    {
        public Guid RecordId { get; set; }
        public string TableLogicalName { get; set; }
        public RecordOperation Operation { get; set; }
        public RecordOutcome Outcome { get; set; }
        public int Pass { get; set; }
        public int RetryCount { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Set by the adapter (never by the Core) when <see cref="Outcome"/> is <see cref="RecordOutcome.Failed"/>
        /// and the failure looks transient (network blip, throttling). Consumed by
        /// <see cref="Migration.RetryPolicy"/> — see ARCHITECTURE.md sección 8.
        /// </summary>
        public bool IsTransient { get; set; }

        /// <summary>Explicit Retry-After the adapter parsed from the fault, if any (sección 19).</summary>
        public TimeSpan? RetryAfterHint { get; set; }
    }

    public sealed class TableExecutionResult
    {
        public string LogicalName { get; set; }
        public int SourceRecordCount { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Failed { get; set; }
        public TimeSpan Duration { get; set; }
        public string WriteStrategyUsed { get; set; }
        public List<RecordOperationResult> Errors { get; set; } = new List<RecordOperationResult>();
    }

    /// <summary>
    /// Checkpoint de una ejecución completa. Se persiste incrementalmente (por tabla, y dentro
    /// de una tabla por chunk) para que un fallo a mitad de camino no pierda el progreso previo.
    /// </summary>
    public sealed class ExecutionManifest
    {
        public Guid ExecutionId { get; set; } = Guid.NewGuid();
        public Guid ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string SourceLabel { get; set; }
        public string TargetLabel { get; set; }
        public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? FinishedUtc { get; set; }
        public ExecutionStatus Status { get; set; } = ExecutionStatus.Pending;
        public List<TableExecutionResult> Tables { get; set; } = new List<TableExecutionResult>();

        public IEnumerable<RecordOperationResult> AllFailures()
        {
            foreach (var t in Tables)
                foreach (var e in t.Errors)
                    if (e.Outcome == RecordOutcome.Failed)
                        yield return e;
        }
    }
}
