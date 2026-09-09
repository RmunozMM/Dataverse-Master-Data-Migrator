using System;
using System.Collections.Generic;
using System.Linq;

namespace DataverseMasterDataMigrator.Core.Planning
{
    /// <summary>
    /// Grafo dirigido simple: una arista A -> B significa "A tiene un lookup obligatorio o
    /// esperable hacia B, por lo que B conviene procesarse antes que A" (B es la dependencia).
    /// Los nodos son nombres lógicos de tabla, todos dentro del mismo perfil.
    /// </summary>
    public sealed class DependencyGraph
    {
        private readonly Dictionary<string, HashSet<string>> _edges =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> Nodes => _edges.Keys;

        public void AddNode(string logicalName)
        {
            if (!_edges.ContainsKey(logicalName))
                _edges[logicalName] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Agrega la arista from -> dependsOn (from depende de dependsOn).</summary>
        public void AddDependency(string from, string dependsOn)
        {
            AddNode(from);
            AddNode(dependsOn);

            if (!string.Equals(from, dependsOn, StringComparison.OrdinalIgnoreCase))
                _edges[from].Add(dependsOn);
        }

        public IReadOnlyCollection<string> DependenciesOf(string node)
        {
            return _edges.TryGetValue(node, out var deps)
                ? (IReadOnlyCollection<string>)deps
                : Array.Empty<string>();
        }

        public DependencyGraph Clone()
        {
            var clone = new DependencyGraph();
            foreach (var kvp in _edges)
            {
                clone.AddNode(kvp.Key);
                foreach (var dep in kvp.Value)
                    clone.AddDependency(kvp.Key, dep);
            }
            return clone;
        }
    }
}
