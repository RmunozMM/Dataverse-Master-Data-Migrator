using System.Collections.Generic;
using System.Linq;

namespace DataverseMasterDataMigrator.Core.Validation
{
    public enum IssueSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class PreflightIssue
    {
        public IssueSeverity Severity { get; set; }
        public string Code { get; set; }
        public string Message { get; set; }
        public string TableLogicalName { get; set; }

        public override string ToString() => $"[{Severity}] {Code}: {Message}";
    }

    public sealed class PreflightResult
    {
        public List<PreflightIssue> Issues { get; } = new List<PreflightIssue>();

        public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
        public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warning);

        /// <summary>Execute solo puede habilitarse si esto es true (sección 22: "Si existen
        /// errores críticos, debe permanecer deshabilitado").</summary>
        public bool ReadyToExecute => !HasErrors;

        public void AddError(string code, string message, string table = null) =>
            Issues.Add(new PreflightIssue { Severity = IssueSeverity.Error, Code = code, Message = message, TableLogicalName = table });

        public void AddWarning(string code, string message, string table = null) =>
            Issues.Add(new PreflightIssue { Severity = IssueSeverity.Warning, Code = code, Message = message, TableLogicalName = table });

        public void AddInfo(string code, string message, string table = null) =>
            Issues.Add(new PreflightIssue { Severity = IssueSeverity.Info, Code = code, Message = message, TableLogicalName = table });
    }
}
