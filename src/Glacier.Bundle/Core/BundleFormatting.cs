using System;
using System.Buffers;
using System.Globalization;
using System.Text;
using Glacier.DocTree.Traversal;
using Glacier.Polaris;
using Glacier.Vector.Index;

namespace Glacier.Bundle.Core
{
    internal static class BundleFormatting
    {
        public static void AppendBundleHeader(StringBuilder sb, string bundleName)
        {
            sb.AppendLine("======================================================================");
            sb.Append(" SYSTEM RESOLVED CONTEXT BUNDLE: ")
              .AppendLine((bundleName ?? string.Empty).ToUpperInvariant());
            sb.Append(" GENERATED AT: ")
              .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .AppendLine(" UTC");
            sb.AppendLine("======================================================================");
        }

        public static void AppendTabularRow(StringBuilder sb, string header, Func<StringBuilder, StringBuilder> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            sb.Append("--- [DATA LAYER] ")
              .Append((header ?? string.Empty).ToUpperInvariant())
              .AppendLine(" ---");
            formatter(sb);
            sb.AppendLine();
        }

        public static void AppendDataFrame(StringBuilder sb, BundleContext context, string dataFrameKey, int maxRows, string label)
        {
            DataFrame df = context.GetDataFrame(dataFrameKey);
            sb.Append("--- [DATA LAYER] ")
              .Append((label ?? string.Empty).ToUpperInvariant())
              .AppendLine(" ---");

            var columns = df.Columns;
            if (columns.Count == 0)
            {
                sb.AppendLine("  (empty DataFrame)");
                sb.AppendLine();
                return;
            }

            sb.Append("  ");
            for (int c = 0; c < columns.Count; c++)
            {
                if (c > 0) sb.Append(" | ");
                sb.Append(columns[c].Name);
            }
            sb.AppendLine();

            int rowCount = df.RowCount;
            int rowsToEmit = maxRows <= 0 ? 0 : Math.Min(maxRows, rowCount);
            for (int r = 0; r < rowsToEmit; r++)
            {
                sb.Append("  ");
                for (int c = 0; c < columns.Count; c++)
                {
                    if (c > 0) sb.Append(" | ");
                    object? val = columns[c].Get(r);
                    if (val == null)
                    {
                        sb.Append("null");
                    }
                    else if (val is IFormattable formattable)
                    {
                        sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(val.ToString());
                    }
                }
                sb.AppendLine();
            }

            if (rowCount > rowsToEmit)
            {
                sb.Append("  ... (")
                  .Append((rowCount - rowsToEmit).ToString(CultureInfo.InvariantCulture))
                  .AppendLine(" more rows omitted)");
            }

            sb.AppendLine();
        }

        public static void AppendGraphTopology(StringBuilder sb, BundleContext context, string graphKey, string centerNodeId, int maxHops, string label)
        {
            ArgumentNullException.ThrowIfNull(centerNodeId);
            var search = context.GetGraphSearch(graphKey);
            var neighborhood = search.FindNeighborhood(centerNodeId, maxHops);

            sb.Append("--- [RELATIONAL LAYER] ")
              .Append((label ?? string.Empty).ToUpperInvariant())
              .Append(" (")
              .Append(maxHops.ToString(CultureInfo.InvariantCulture))
              .Append(" HOPS FROM ")
              .Append(centerNodeId)
              .AppendLine(") ---");
            sb.Append("Target Entity: ").AppendLine(centerNodeId);
            sb.Append("Connected Network Entities (")
              .Append(neighborhood.Count.ToString(CultureInfo.InvariantCulture))
              .AppendLine("):");

            if (neighborhood.Count > 0)
            {
                sb.Append("  ");
                for (int i = 0; i < neighborhood.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(neighborhood[i]);
                }
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine("  No relational connections found within this depth.");
            }
            sb.AppendLine();
        }

        public static void AppendCsrGraphTopology(StringBuilder sb, BundleContext context, string csrGraphKey, string centerNodeId, int maxHops, string label)
        {
            ArgumentNullException.ThrowIfNull(centerNodeId);
            var store = context.GetCsrGraphStore(csrGraphKey);
            var search = context.GetCsrGraphSearch(csrGraphKey);

            int count = 0;
            int[]? rented = null;
            try
            {
                if (maxHops > 0 && store.NodeCount > 0 && store.TryGetInternalId(centerNodeId, out int startInternal))
                {
                    rented = ArrayPool<int>.Shared.Rent(store.NodeCount);
                    count = search.FindNeighborhood(startInternal, maxHops, rented.AsSpan(0, store.NodeCount));
                }

                sb.Append("--- [RELATIONAL LAYER] ")
                  .Append((label ?? string.Empty).ToUpperInvariant())
                  .Append(" (")
                  .Append(maxHops.ToString(CultureInfo.InvariantCulture))
                  .Append(" HOPS FROM ")
                  .Append(centerNodeId)
                  .AppendLine(") ---");
                sb.Append("Target Entity: ").AppendLine(centerNodeId);
                sb.Append("Connected Network Entities (")
                  .Append(count.ToString(CultureInfo.InvariantCulture))
                  .AppendLine("):");

                if (count > 0 && rented != null)
                {
                    sb.Append("  ");
                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(store.GetExternalId(rented[i]));
                    }
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine("  No relational connections found within this depth.");
                }
                sb.AppendLine();
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<int>.Shared.Return(rented);
                }
            }
        }

        public static void AppendDocumentTreeSection(StringBuilder sb, BundleContext context, string treeKey, string sectionHeader, string label)
        {
            ArgumentNullException.ThrowIfNull(sectionHeader);
            var search = context.GetTreeSearch(treeKey);
            var node = search.FindHeader(sectionHeader);

            sb.Append("--- [HIERARCHICAL LAYER] ")
              .Append((label ?? string.Empty).ToUpperInvariant())
              .AppendLine(" ---");
            if (node != null)
            {
                sb.Append("Document Path: ").AppendLine(TreeSearch.GetSemanticPath(node));
                sb.AppendLine("Content Frame:");
                sb.AppendLine(node.GetFullText());
            }
            else
            {
                sb.Append("[Warning: Structural document section '")
                  .Append(sectionHeader)
                  .AppendLine("' could not be resolved.]");
            }
            sb.AppendLine();
        }

        public static void AppendVectorContext(StringBuilder sb, BundleContext context, string vectorKey, float[] queryVector, int topK, string label)
        {
            var vector = context.GetVectorIndex(vectorKey);
            var results = vector.Search(queryVector, topK);

            sb.Append("--- [SEMANTIC LAYER] ")
              .Append((label ?? string.Empty).ToUpperInvariant())
              .AppendLine(" ---");
            if (results.Length > 0)
            {
                for (int i = 0; i < results.Length; i++)
                {
                    sb.Append("[Rank ")
                      .Append((i + 1).ToString(CultureInfo.InvariantCulture))
                      .Append(" | Match Score: ")
                      .Append(results[i].Score.ToString("F4", CultureInfo.InvariantCulture))
                      .Append("] ")
                      .AppendLine(results[i].Metadata);
                }
            }
            else
            {
                sb.AppendLine("  No semantic matches resolved.");
            }
            sb.AppendLine();
        }

        public static void AppendHnswVectorContext(
            StringBuilder sb,
            BundleContext context,
            string hnswKey,
            ReadOnlySpan<float> queryVector,
            int topK,
            string label,
            int efSearch)
        {
            var index = context.GetHnswVectorIndex(hnswKey);
            int rentedLen = Math.Max(1, topK);
            SearchResult[] rented = ArrayPool<SearchResult>.Shared.Rent(rentedLen);
            try
            {
                int count = topK > 0
                    ? index.Search(queryVector, rented.AsSpan(0, topK), efSearch)
                    : index.Search(queryVector, Span<SearchResult>.Empty, efSearch);

                sb.Append("--- [SEMANTIC LAYER] ")
                  .Append((label ?? string.Empty).ToUpperInvariant())
                  .AppendLine(" ---");

                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        sb.Append("[Rank ")
                          .Append((i + 1).ToString(CultureInfo.InvariantCulture))
                          .Append(" | Match Score: ")
                          .Append(rented[i].Score.ToString("F4", CultureInfo.InvariantCulture))
                          .Append("] ")
                          .AppendLine(rented[i].Metadata);
                    }
                }
                else
                {
                    sb.AppendLine("  No semantic matches resolved.");
                }
                sb.AppendLine();
            }
            finally
            {
                ArrayPool<SearchResult>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
