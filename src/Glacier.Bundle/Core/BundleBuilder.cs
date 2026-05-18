using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Glacier.Vector.Index;
using Glacier.Graph.Storage;
using Glacier.Graph.Traversal;
using Glacier.DocTree.Core;
using Glacier.DocTree.Traversal;

namespace Glacier.Bundle.Core
{
    /// <summary>
    /// A fluent, zero-allocation-focused builder that programmatically coordinates,
    /// filters, and aggregates data from multiple semantic engines into clean LLM prompt frames.
    /// </summary>
    public class BundleBuilder
    {
        private readonly StringBuilder _sb = new(2048);
        private readonly BundleContext _context;

        public BundleBuilder(BundleContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Begins the context frame with a clean header.
        /// </summary>
        public BundleBuilder BeginBundle(string bundleName)
        {
            _sb.Clear();
            _sb.AppendLine("======================================================================");
            _sb.AppendLine($" SYSTEM RESOLVED CONTEXT BUNDLE: {bundleName.ToUpper()}");
            _sb.AppendLine($" GENERATED AT: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            _sb.AppendLine("======================================================================\n");
            return this;
        }

        /// <summary>
        /// Appends structured metadata (such as customer metrics or CRM status) resolved from tabular data sources.
        /// </summary>
        public BundleBuilder AppendTabularRow(string header, Func<StringBuilder, StringBuilder> formatter)
        {
            _sb.AppendLine($"--- [DATA LAYER] {header.ToUpper()} ---");
            formatter(_sb);
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Executes a fast, local relationship neighborhood scan on Glacier.Graph and appends the topology.
        /// </summary>
        public BundleBuilder AppendGraphTopology(string graphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP")
        {
            var graph = _context.GetGraphStore(graphKey);
            var search = new GraphSearch(graph);
            var neighborhood = search.FindNeighborhood(centerNodeId, maxHops);

            _sb.AppendLine($"--- [RELATIONAL LAYER] {label.ToUpper()} ({maxHops} HOPS FROM {centerNodeId}) ---");
            _sb.AppendLine($"Target Entity: {centerNodeId}");
            _sb.AppendLine($"Connected Network Entities ({neighborhood.Count}):");

            if (neighborhood.Count > 0)
            {
                _sb.AppendLine($"  {string.Join(", ", neighborhood)}");
            }
            else
            {
                _sb.AppendLine("  No relational connections found within this depth.");
            }
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Instantly finds a clean, structurally bounded policy section in Glacier.DocTree and pulls it in.
        /// </summary>
        public BundleBuilder AppendDocumentTreeSection(string treeKey, string sectionHeader, string label = "DOCUMENT TREE POLICY")
        {
            var root = _context.GetDocTree(treeKey);
            var search = new TreeSearch(root);
            var node = search.FindHeader(sectionHeader);

            _sb.AppendLine($"--- [HIERARCHICAL LAYER] {label.ToUpper()} ---");
            if (node != null)
            {
                _sb.AppendLine($"Document Path: {TreeSearch.GetSemanticPath(node)}");
                _sb.AppendLine("Content Frame:");
                _sb.AppendLine(node.GetFullText());
            }
            else
            {
                _sb.AppendLine($"[Warning: Structural document section '{sectionHeader}' could not be resolved.]");
            }
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Performs a hardware-accelerated semantic scan over Glacier.Vector and formats the Top-K results.
        /// </summary>
        public BundleBuilder AppendVectorContext(string vectorKey, float[] queryVector, int topK, string label = "SEMANTIC HISTORY")
        {
            var vector = _context.GetVectorIndex(vectorKey);
            var results = vector.Search(queryVector, topK);

            _sb.AppendLine($"--- [SEMANTIC LAYER] {label.ToUpper()} ---");
            if (results.Length > 0)
            {
                for (int i = 0; i < results.Length; i++)
                {
                    _sb.AppendLine($"[Rank {i + 1} | Match Score: {results[i].Score:F4}] {results[i].Metadata}");
                }
            }
            else
            {
                _sb.AppendLine("  No semantic matches resolved.");
            }
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Compiles the builder states into a single, high-density prompt context block.
        /// </summary>
        public string Build()
        {
            _sb.AppendLine("======================================================================");
            _sb.AppendLine(" END OF RESOLVED CONTEXT BUNDLE");
            _sb.AppendLine("======================================================================");
            return _sb.ToString();
        }
    }
}