using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Glacier.Bundle.Budgeting;
using Glacier.DocTree.Traversal;
using Glacier.Graph.Traversal;

namespace Glacier.Bundle.Core
{
    /// <summary>
    /// An intelligent LLM context builder that respects token limits using active compaction strategies
    /// (StrictPrune, HeadTailTruncate, ProportionalDecay).
    /// </summary>
    public class CompactingBundleBuilder
    {
        private readonly List<BundleSection> _sections = new();
        public TokenBudget Budget { get; }
        public BundleContext? Context { get; }

        public IReadOnlyList<BundleSection> Sections => _sections;

        public CompactingBundleBuilder(TokenBudget budget, BundleContext? context = null)
        {
            Budget = budget ?? throw new ArgumentNullException(nameof(budget));
            Context = context;
        }

        public CompactingBundleBuilder(BundleContext context, int totalBudget, CompactionStrategy defaultStrategy = CompactionStrategy.StrictPrune)
            : this(new TokenBudget(totalBudget, defaultStrategy: defaultStrategy), context)
        {
        }

        public CompactingBundleBuilder AddSection(string name, int priority, string content, bool isFixed = false, CompactionStrategy? strategyOverride = null)
        {
            _sections.Add(new BundleSection(name, priority, content, isFixed, strategyOverride));
            return this;
        }

        public CompactingBundleBuilder BeginBundle(string bundleName, int priority = int.MaxValue)
        {
            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine($" SYSTEM RESOLVED CONTEXT BUNDLE: {bundleName.ToUpper()}");
            sb.AppendLine($" GENERATED AT: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("======================================================================");

            return AddSection("Header", priority, sb.ToString(), isFixed: true);
        }

        public CompactingBundleBuilder AppendTabularRow(string name, int priority, string header, Func<StringBuilder, StringBuilder> formatter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- [DATA LAYER] {header.ToUpper()} ---");
            formatter(sb);
            sb.AppendLine();
            return AddSection(name, priority, sb.ToString());
        }

        public CompactingBundleBuilder AppendGraphTopology(string name, int priority, string graphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query GraphStore.");

            var graph = Context.GetGraphStore(graphKey);
            var search = new GraphSearch(graph);
            var neighborhood = search.FindNeighborhood(centerNodeId, maxHops);

            var sb = new StringBuilder();
            sb.AppendLine($"--- [RELATIONAL LAYER] {label.ToUpper()} ({maxHops} HOPS FROM {centerNodeId}) ---");
            sb.AppendLine($"Target Entity: {centerNodeId}");
            sb.AppendLine($"Connected Network Entities ({neighborhood.Count}):");

            if (neighborhood.Count > 0)
            {
                sb.AppendLine($"  {string.Join(", ", neighborhood)}");
            }
            else
            {
                sb.AppendLine("  No relational connections found within this depth.");
            }
            sb.AppendLine();
            return AddSection(name, priority, sb.ToString());
        }

        public CompactingBundleBuilder AppendDocumentTreeSection(string name, int priority, string treeKey, string sectionHeader, string label = "DOCUMENT TREE POLICY")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query DocTree.");

            var root = Context.GetDocTree(treeKey);
            var search = new TreeSearch(root);
            var node = search.FindHeader(sectionHeader);

            var sb = new StringBuilder();
            sb.AppendLine($"--- [HIERARCHICAL LAYER] {label.ToUpper()} ---");
            if (node != null)
            {
                sb.AppendLine($"Document Path: {TreeSearch.GetSemanticPath(node)}");
                sb.AppendLine("Content Frame:");
                sb.AppendLine(node.GetFullText());
            }
            else
            {
                sb.AppendLine($"[Warning: Structural document section '{sectionHeader}' could not be resolved.]");
            }
            sb.AppendLine();
            return AddSection(name, priority, sb.ToString());
        }

        public CompactingBundleBuilder AppendVectorContext(string name, int priority, string vectorKey, float[] queryVector, int topK, string label = "SEMANTIC HISTORY")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query VectorIndex.");

            var vector = Context.GetVectorIndex(vectorKey);
            var results = vector.Search(queryVector, topK);

            var sb = new StringBuilder();
            sb.AppendLine($"--- [SEMANTIC LAYER] {label.ToUpper()} ---");
            if (results.Length > 0)
            {
                for (int i = 0; i < results.Length; i++)
                {
                    sb.AppendLine($"[Rank {i + 1} | Match Score: {results[i].Score:F4}] {results[i].Metadata}");
                }
            }
            else
            {
                sb.AppendLine("  No semantic matches resolved.");
            }
            sb.AppendLine();
            return AddSection(name, priority, sb.ToString());
        }

        /// <summary>
        /// Builds the finalized prompt context bundle by applying the specified or default compaction strategy
        /// if the contents exceed the token budget.
        /// </summary>
        public string Build(CompactionStrategy? overrideStrategy = null)
        {
            var strategy = overrideStrategy ?? Budget.DefaultStrategy;
            int totalTokens = _sections.Sum(s => s.EstimateTokens(Budget.Estimator));

            if (totalTokens <= Budget.TotalBudget)
            {
                string assembledInitial = AssembleSections(_sections);
                if (Budget.EstimateTokens(assembledInitial) <= Budget.TotalBudget)
                {
                    return assembledInitial;
                }
            }

            var compactedSections = strategy switch
            {
                CompactionStrategy.StrictPrune => CompactStrictPrune(_sections, Budget.TotalBudget),
                CompactionStrategy.HeadTailTruncate => CompactHeadTail(_sections, Budget.TotalBudget),
                CompactionStrategy.ProportionalDecay => CompactProportionalDecay(_sections, Budget.TotalBudget),
                _ => CompactStrictPrune(_sections, Budget.TotalBudget)
            };

            string finalResult = AssembleSections(compactedSections);
            while (Budget.EstimateTokens(finalResult) > Budget.TotalBudget)
            {
                var nonFixed = compactedSections
                    .Where(s => !s.IsFixed && !string.IsNullOrEmpty(s.Content))
                    .ToList();

                if (nonFixed.Count > 1)
                {
                    var lowest = nonFixed.OrderBy(s => s.Priority).First();
                    compactedSections.Remove(lowest);
                    finalResult = AssembleSections(compactedSections);
                }
                else
                {
                    finalResult = Budget.TruncateToTokens(finalResult, Budget.TotalBudget);
                    break;
                }
            }

            if (Budget.EstimateTokens(finalResult) > Budget.TotalBudget)
            {
                finalResult = Budget.TruncateToTokens(finalResult, Budget.TotalBudget);
            }

            return finalResult;
        }

        private List<BundleSection> CompactStrictPrune(List<BundleSection> source, int maxBudget)
        {
            var result = new List<BundleSection>(source);

            // Calculate total tokens
            int currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));

            // While over budget, remove lowest-priority non-fixed sections
            while (currentTokens > maxBudget)
            {
                // Find non-fixed section with lowest priority
                var lowestNonFixed = result
                    .Where(s => !s.IsFixed)
                    .OrderBy(s => s.Priority)
                    .FirstOrDefault();

                if (lowestNonFixed == null)
                {
                    // Only fixed sections remain; if still over budget, truncate them
                    for (int i = 0; i < result.Count && currentTokens > maxBudget; i++)
                    {
                        var section = result[i];
                        int sectionTokens = section.EstimateTokens(Budget.Estimator);
                        int excess = currentTokens - maxBudget;
                        int allowed = Math.Max(0, sectionTokens - excess);
                        string truncated = Budget.TruncateToTokens(section.Content, allowed);
                        result[i] = new BundleSection(section.Name, section.Priority, truncated, section.IsFixed, section.StrategyOverride);
                        currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));
                    }
                    break;
                }

                result.Remove(lowestNonFixed);
                currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));
            }

            return result;
        }

        private List<BundleSection> CompactHeadTail(List<BundleSection> source, int maxBudget)
        {
            var result = new List<BundleSection>();
            int fixedTokens = source.Where(s => s.IsFixed).Sum(s => s.EstimateTokens(Budget.Estimator));
            int delimiterReserve = source.Count > 1 ? (source.Count - 1) : 0;
            int remainingBudget = Math.Max(0, maxBudget - fixedTokens - delimiterReserve);

            var nonFixedSections = source.Where(s => !s.IsFixed).ToList();
            if (nonFixedSections.Count == 0)
            {
                return CompactStrictPrune(source, maxBudget);
            }

            // Distribute remaining budget among non-fixed sections based on their original token proportion
            int nonFixedOriginalTokens = Math.Max(1, nonFixedSections.Sum(s => s.EstimateTokens(Budget.Estimator)));

            foreach (var section in source)
            {
                if (section.IsFixed)
                {
                    result.Add(section);
                }
                else
                {
                    int origTokens = section.EstimateTokens(Budget.Estimator);
                    double ratio = (double)origTokens / nonFixedOriginalTokens;
                    int targetSectionTokens = Math.Max(1, (int)(remainingBudget * ratio));

                    string compactedContent = Budget.TruncateHeadTail(section.Content, targetSectionTokens);
                    result.Add(new BundleSection(section.Name, section.Priority, compactedContent, false, section.StrategyOverride));
                }
            }

            // Verify final token count fits within budget; if not, prune excess
            int currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));
            if (currentTokens > maxBudget)
            {
                return CompactStrictPrune(result, maxBudget);
            }

            return result;
        }

        private List<BundleSection> CompactProportionalDecay(List<BundleSection> source, int maxBudget)
        {
            var result = new List<BundleSection>();
            int fixedTokens = source.Where(s => s.IsFixed).Sum(s => s.EstimateTokens(Budget.Estimator));
            int delimiterReserve = source.Count > 1 ? (source.Count - 1) : 0;
            int availableBudget = Math.Max(0, maxBudget - fixedTokens - delimiterReserve);

            var nonFixed = source.Where(s => !s.IsFixed).ToList();
            if (nonFixed.Count == 0 || availableBudget <= 0)
            {
                return CompactStrictPrune(source, maxBudget);
            }

            // Weight calculation based on Priority
            long totalPriority = nonFixed.Sum(s => (long)Math.Max(1, s.Priority));

            foreach (var section in source)
            {
                if (section.IsFixed)
                {
                    result.Add(section);
                }
                else
                {
                    double weight = (double)Math.Max(1, section.Priority) / totalPriority;
                    int allocatedTokens = (int)(availableBudget * weight);
                    int sectionTokens = section.EstimateTokens(Budget.Estimator);

                    string finalContent;
                    if (allocatedTokens < sectionTokens)
                    {
                        finalContent = Budget.TruncateToTokens(section.Content, allocatedTokens);
                    }
                    else
                    {
                        finalContent = section.Content;
                    }

                    result.Add(new BundleSection(section.Name, section.Priority, finalContent, false, section.StrategyOverride));
                }
            }

            // If slight rounding overflow occurs, adjust with strict prune or trimming
            int currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));
            if (currentTokens > maxBudget)
            {
                return CompactStrictPrune(result, maxBudget);
            }

            return result;
        }

        private static string AssembleSections(List<BundleSection> sections)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < sections.Count; i++)
            {
                if (!string.IsNullOrEmpty(sections[i].Content))
                {
                    sb.Append(sections[i].Content);
                    if (!sections[i].Content.EndsWith("\n"))
                    {
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString();
        }
    }
}
