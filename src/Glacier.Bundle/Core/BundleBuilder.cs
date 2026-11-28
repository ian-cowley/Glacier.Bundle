using System;
using System.Text;

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
            BundleFormatting.AppendBundleHeader(_sb, bundleName);
            _sb.AppendLine();
            return this;
        }

        /// <summary>
        /// Appends structured metadata (such as customer metrics or CRM status) resolved from tabular data sources.
        /// </summary>
        public BundleBuilder AppendTabularRow(string header, Func<StringBuilder, StringBuilder> formatter)
        {
            BundleFormatting.AppendTabularRow(_sb, header, formatter);
            return this;
        }

        /// <summary>
        /// Formats rows from a registered Glacier.Polaris <see cref="Glacier.Polaris.DataFrame"/> into the context bundle.
        /// </summary>
        public BundleBuilder AppendDataFrame(string dataFrameKey, int maxRows = 20, string label = "DATA LAYER")
        {
            BundleFormatting.AppendDataFrame(_sb, _context, dataFrameKey, maxRows, label);
            return this;
        }

        /// <summary>
        /// Executes a fast, local relationship neighborhood scan on Glacier.Graph and appends the topology.
        /// </summary>
        public BundleBuilder AppendGraphTopology(string graphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP")
        {
            BundleFormatting.AppendGraphTopology(_sb, _context, graphKey, centerNodeId, maxHops, label);
            return this;
        }

        /// <summary>
        /// Executes a zero-allocation neighborhood scan on a registered <see cref="Glacier.Graph.Storage.CsrGraphStore"/> and appends the topology.
        /// </summary>
        public BundleBuilder AppendCsrGraphTopology(string csrGraphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP")
        {
            BundleFormatting.AppendCsrGraphTopology(_sb, _context, csrGraphKey, centerNodeId, maxHops, label);
            return this;
        }

        /// <summary>
        /// Instantly finds a clean, structurally bounded policy section in Glacier.DocTree and pulls it in.
        /// </summary>
        public BundleBuilder AppendDocumentTreeSection(string treeKey, string sectionHeader, string label = "DOCUMENT TREE POLICY")
        {
            BundleFormatting.AppendDocumentTreeSection(_sb, _context, treeKey, sectionHeader, label);
            return this;
        }

        /// <summary>
        /// Performs a hardware-accelerated semantic scan over Glacier.Vector and formats the Top-K results.
        /// </summary>
        public BundleBuilder AppendVectorContext(string vectorKey, float[] queryVector, int topK, string label = "SEMANTIC HISTORY")
        {
            BundleFormatting.AppendVectorContext(_sb, _context, vectorKey, queryVector, topK, label);
            return this;
        }

        /// <summary>
        /// Performs a pooled, zero-array-allocation HNSW similarity scan over <see cref="Glacier.Vector.Index.HnswVectorIndex"/> and formats the Top-K results.
        /// </summary>
        public BundleBuilder AppendHnswVectorContext(string hnswKey, ReadOnlySpan<float> queryVector, int topK, string label = "SEMANTIC HISTORY", int efSearch = 64)
        {
            BundleFormatting.AppendHnswVectorContext(_sb, _context, hnswKey, queryVector, topK, label, efSearch);
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
