using System;
using DataverseMasterDataMigrator.Core.Abstractions;

namespace DataverseMasterDataMigrator.XrmToolBox.Services
{
    /// <summary>
    /// Forwards <see cref="IExecutionLogger"/> calls from <c>Core.Migration.MigrationExecutor</c>
    /// (which runs on a background worker thread) to the plugin's log box. The callback is
    /// responsible for its own thread marshalling (see <c>PluginControl.AppendLog</c>).
    /// </summary>
    public sealed class PluginExecutionLogger : IExecutionLogger
    {
        private readonly Action<string> _sink;

        public PluginExecutionLogger(Action<string> sink)
        {
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public void LogInfo(string message) => _sink(message);
        public void LogWarning(string message) => _sink("WARN: " + message);
        public void LogError(string message) => _sink("ERROR: " + message);
    }
}
