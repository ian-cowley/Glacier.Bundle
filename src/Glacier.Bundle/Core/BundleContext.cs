using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Glacier.DocTree.Core;
using Glacier.DocTree.Traversal;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Glacier.Polaris;
using Glacier.Vector.Index;

namespace Glacier.Bundle.Core
{
    /// <summary>
    /// Acts as the central, thread-safe registry for the Glacier high-performance storage engines.
    /// Provides unified access for the Bundle Orchestrator to resolve data across different shapes.
    /// Does not take disposal ownership of registered storage engines.
    /// </summary>
    public class BundleContext
    {
        private readonly ConcurrentDictionary<string, VectorIndex> _vectors = new();
        private readonly ConcurrentDictionary<string, HnswVectorIndex> _hnswVectors = new();
        private readonly ConcurrentDictionary<string, GraphStore> _graphs = new();
        private readonly ConcurrentDictionary<string, CsrGraphStore> _csrGraphs = new();
        private readonly ConcurrentDictionary<string, DocNode> _documentTrees = new();
        private readonly ConcurrentDictionary<string, DataFrame> _dataFrames = new();
        private readonly ConcurrentDictionary<string, object> _tabularData = new();

        private readonly ConditionalWeakTable<GraphStore, GraphSearch> _graphSearches = new();
        private readonly ConditionalWeakTable<CsrGraphStore, CsrGraphSearch> _csrGraphSearches = new();
        private readonly ConditionalWeakTable<DocNode, TreeSearch> _treeSearches = new();

        /// <summary>
        /// Registers a Glacier.Vector <see cref="VectorIndex"/>.
        /// </summary>
        public void RegisterVectorIndex(string key, VectorIndex index)
        {
            ArgumentNullException.ThrowIfNull(key);
            _vectors[key] = index ?? throw new ArgumentNullException(nameof(index));
        }

        /// <summary>
        /// Registers a Glacier.Vector <see cref="HnswVectorIndex"/>.
        /// </summary>
        public void RegisterHnswVectorIndex(string key, HnswVectorIndex index)
        {
            ArgumentNullException.ThrowIfNull(key);
            _hnswVectors[key] = index ?? throw new ArgumentNullException(nameof(index));
        }

        /// <summary>
        /// Registers a Glacier.Graph <see cref="GraphStore"/>.
        /// </summary>
        public void RegisterGraphStore(string key, GraphStore store)
        {
            ArgumentNullException.ThrowIfNull(key);
            _graphs[key] = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Registers a Glacier.Graph <see cref="CsrGraphStore"/>.
        /// </summary>
        public void RegisterCsrGraphStore(string key, CsrGraphStore store)
        {
            ArgumentNullException.ThrowIfNull(key);
            _csrGraphs[key] = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Registers a Glacier.DocTree <see cref="DocNode"/> root.
        /// </summary>
        public void RegisterDocTree(string key, DocNode rootNode)
        {
            ArgumentNullException.ThrowIfNull(key);
            _documentTrees[key] = rootNode ?? throw new ArgumentNullException(nameof(rootNode));
        }

        /// <summary>
        /// Registers a Glacier.Polaris <see cref="DataFrame"/>.
        /// Also makes it retrievable via <see cref="GetTabularData{T}(string)"/> with <see cref="DataFrame"/>.
        /// </summary>
        public void RegisterDataFrame(string key, DataFrame dataFrame)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(dataFrame);
            _dataFrames[key] = dataFrame;
            _tabularData[key] = dataFrame;
        }

        /// <summary>
        /// Registers a tabular dataset (such as a Glacier.Polaris <see cref="DataFrame"/> or custom tabular model).
        /// </summary>
        public void RegisterTabularData<T>(string key, T dataFrame) where T : class
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(dataFrame);
            _tabularData[key] = dataFrame;
            if (dataFrame is DataFrame df)
            {
                _dataFrames[key] = df;
            }
            else
            {
                _dataFrames.TryRemove(key, out _);
            }
        }

        public VectorIndex GetVectorIndex(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _vectors.TryGetValue(key, out var index)
                ? index
                : throw new KeyNotFoundException($"Vector index '{key}' not found.");
        }

        public bool TryGetVectorIndex(string key, [NotNullWhen(true)] out VectorIndex? index)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _vectors.TryGetValue(key, out index);
        }

        public HnswVectorIndex GetHnswVectorIndex(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _hnswVectors.TryGetValue(key, out var index)
                ? index
                : throw new KeyNotFoundException($"HNSW vector index '{key}' not found.");
        }

        public bool TryGetHnswVectorIndex(string key, [NotNullWhen(true)] out HnswVectorIndex? index)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _hnswVectors.TryGetValue(key, out index);
        }

        public GraphStore GetGraphStore(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _graphs.TryGetValue(key, out var store)
                ? store
                : throw new KeyNotFoundException($"Graph store '{key}' not found.");
        }

        public bool TryGetGraphStore(string key, [NotNullWhen(true)] out GraphStore? store)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _graphs.TryGetValue(key, out store);
        }

        public CsrGraphStore GetCsrGraphStore(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _csrGraphs.TryGetValue(key, out var store)
                ? store
                : throw new KeyNotFoundException($"CSR graph store '{key}' not found.");
        }

        public bool TryGetCsrGraphStore(string key, [NotNullWhen(true)] out CsrGraphStore? store)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _csrGraphs.TryGetValue(key, out store);
        }

        public DocNode GetDocTree(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _documentTrees.TryGetValue(key, out var root)
                ? root
                : throw new KeyNotFoundException($"Document tree '{key}' not found.");
        }

        public bool TryGetDocTree(string key, [NotNullWhen(true)] out DocNode? rootNode)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _documentTrees.TryGetValue(key, out rootNode);
        }

        public DataFrame GetDataFrame(string key)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _dataFrames.TryGetValue(key, out var df)
                ? df
                : throw new KeyNotFoundException($"DataFrame '{key}' not found.");
        }

        public bool TryGetDataFrame(string key, [NotNullWhen(true)] out DataFrame? dataFrame)
        {
            ArgumentNullException.ThrowIfNull(key);
            return _dataFrames.TryGetValue(key, out dataFrame);
        }

        public T GetTabularData<T>(string key) where T : class
        {
            ArgumentNullException.ThrowIfNull(key);
            return _tabularData.TryGetValue(key, out var df) && df is T typedDf
                ? typedDf
                : throw new KeyNotFoundException($"Tabular data '{key}' not found or type mismatch.");
        }

        public bool TryGetTabularData<T>(string key, [NotNullWhen(true)] out T? dataFrame) where T : class
        {
            ArgumentNullException.ThrowIfNull(key);
            if (_tabularData.TryGetValue(key, out var df) && df is T typedDf)
            {
                dataFrame = typedDf;
                return true;
            }
            dataFrame = null;
            return false;
        }

        internal GraphSearch GetGraphSearch(string key) =>
            _graphSearches.GetValue(GetGraphStore(key), static g => new GraphSearch(g));

        internal CsrGraphSearch GetCsrGraphSearch(string key) =>
            _csrGraphSearches.GetValue(GetCsrGraphStore(key), static g => new CsrGraphSearch(g));

        internal TreeSearch GetTreeSearch(string key) =>
            _treeSearches.GetValue(GetDocTree(key), static d => new TreeSearch(d));
    }
}
