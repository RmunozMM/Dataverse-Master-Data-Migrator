using System;
using System.Collections.Generic;
using System.Linq;

namespace DataverseMasterDataMigrator.Core.Planning
{
    public static class CycleDetector
    {
        private sealed class Frame
        {
            public string Node;
            public IEnumerator<string> Neighbors;
        }

        /// <summary>
        /// Devuelve la lista de ciclos encontrados en el grafo. Cada ciclo es el conjunto de
        /// nombres lógicos que participan en él. Un nodo aislado o una cadena lineal no
        /// producen ningún ciclo.
        ///
        /// Implementación iterativa de Tarjan (componentes fuertemente conexas), no recursiva:
        /// un grafo de dependencias con cientos de tablas encadenadas podría, con la versión
        /// recursiva clásica, agotar la pila de llamadas. Aquí se mantiene una pila explícita en
        /// el heap en su lugar.
        /// </summary>
        public static IReadOnlyList<IReadOnlyList<string>> FindCycles(DependencyGraph graph)
        {
            if (graph == null) throw new ArgumentNullException(nameof(graph));

            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var lowLink = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var onStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tarjanStack = new Stack<string>();
            var cycles = new List<IReadOnlyList<string>>();
            int nextIndex = 0;

            foreach (var start in graph.Nodes)
            {
                if (index.ContainsKey(start))
                    continue;

                var callStack = new Stack<Frame>();
                callStack.Push(new Frame { Node = start, Neighbors = graph.DependenciesOf(start).GetEnumerator() });
                index[start] = nextIndex;
                lowLink[start] = nextIndex;
                nextIndex++;
                tarjanStack.Push(start);
                onStack.Add(start);

                while (callStack.Count > 0)
                {
                    var frame = callStack.Peek();
                    var v = frame.Node;

                    if (frame.Neighbors.MoveNext())
                    {
                        var w = frame.Neighbors.Current;

                        if (!index.ContainsKey(w))
                        {
                            index[w] = nextIndex;
                            lowLink[w] = nextIndex;
                            nextIndex++;
                            tarjanStack.Push(w);
                            onStack.Add(w);
                            callStack.Push(new Frame { Node = w, Neighbors = graph.DependenciesOf(w).GetEnumerator() });
                        }
                        else if (onStack.Contains(w))
                        {
                            lowLink[v] = Math.Min(lowLink[v], index[w]);
                        }
                    }
                    else
                    {
                        callStack.Pop();

                        if (callStack.Count > 0)
                        {
                            var parent = callStack.Peek().Node;
                            lowLink[parent] = Math.Min(lowLink[parent], lowLink[v]);
                        }

                        if (lowLink[v] == index[v])
                        {
                            var component = new List<string>();
                            string w;
                            do
                            {
                                w = tarjanStack.Pop();
                                onStack.Remove(w);
                                component.Add(w);
                            } while (!string.Equals(w, v, StringComparison.OrdinalIgnoreCase));

                            bool isRealCycle = component.Count > 1 ||
                                graph.DependenciesOf(component[0]).Contains(component[0], StringComparer.OrdinalIgnoreCase);

                            if (isRealCycle)
                                cycles.Add(component);
                        }
                    }
                }
            }

            return cycles;
        }
    }
}
