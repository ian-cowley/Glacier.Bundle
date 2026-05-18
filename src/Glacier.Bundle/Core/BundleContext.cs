using System;
using System.Collections.Concurrent;
using Glacier.Vector.Index;
using Glacier.Graph.Storage;
using Glacier.DocTree.Core;

namespace Glacier.Bundle.Core
{
    /// <summary>
    /// Acts as the central, thread-safe registry for the Glacier high-performance storage engines.
    /// Provides unified access for the Bundle Orchestrator to resolve data across different shapes.
    /// </summary>
    public class BundleContext
    {
        private readonly ConcurrentDictionary<string, VectorIndex> _vectors = new();
        private readonly ConcurrentDictionary<string, GraphStore> _graphs = new();
        private readonly ConcurrentDictionary<string, DocNode> _documentTrees = new();
        private readonly ConcurrentDictionary<string, object> _tabularData = new(); // Represents Glacier.Polaris DataFrames

        /// <summary>
        /// Registers a Glacier.Vector index.
        /// </summary>
        public void RegisterVectorIndex(string key, VectorIndex index)
        {
            _vectors[key] = index ?? throw new ArgumentNullException(nameof(index));
        }

        /// <summary>
        /// Registers a Glacier.Graph store.
        /// </summary>
        public void RegisterGraphStore(string key, GraphStore store)
        {
            _graphs[key] = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Registers a Glacier.DocTree root.
        /// </summary>
        public void RegisterDocTree(string key, DocNode rootNode)
        {
            _documentTrees[key] = rootNode ?? throw new ArgumentNullException(nameof(rootNode));
        }

        /// <summary>
        /// Registers a tabular dataset (representing a Glacier.Polaris DataFrame).
        /// </summary>
        public void RegisterTabularData<T>(string key, T dataFrame) where T : class
        {
            _tabularData[key] = dataFrame ?? throw new ArgumentNullException(nameof(dataFrame));
        }

        public VectorIndex GetVectorIndex(string key) =>
            _vectors.TryGetValue(key, out var index) ? index : throw new KeyNotFoundException($"Vector index '{key}' not found.");

        public GraphStore GetGraphStore(string key) =>
            _graphs.TryGetValue(key, out var store) ? store : throw new KeyNotFoundException($"Graph store '{key}' not found.");

        public DocNode GetDocTree(string key) =>
            _documentTrees.TryGetValue(key, out var root) ? root : throw new KeyNotFoundException($"Document tree '{key}' not found.");

        public T GetTabularData<T>(string key) where T : class =>
            _tabularData.TryGetValue(key, out var df) && df is T typedDf ? typedDf : throw new KeyNotFoundException($"Tabular data '{key}' not found or type mismatch.");
    }
}