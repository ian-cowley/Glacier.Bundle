using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Glacier.Bundle.Budgeting;
using Glacier.DocTree.Traversal;
using Glacier.Graph.Traversal;

namespace Glacier.Bundle.Core
{
    /// <summary>
    /// An intelligent LLM context builder that respects token limits and reservations using active compaction strategies
    /// (<see cref="CompactionStrategy.StrictPrune"/>, <see cref="CompactionStrategy.HeadTailTruncate"/>, <see cref="CompactionStrategy.ProportionalDecay"/>)
    /// and per-section <see cref="BundleSection.StrategyOverride"/> policies.
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
            sb.AppendLine($" SYSTEM RESOLVED CONTEXT BUNDLE: {bundleName.ToUpperInvariant()}");
            sb.AppendLine($" GENERATED AT: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine("======================================================================");

            return AddSection("Header", priority, sb.ToString(), isFixed: true);
        }

        public CompactingBundleBuilder AppendTabularRow(string name, int priority, string header, Func<StringBuilder, StringBuilder> formatter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--- [DATA LAYER] {header.ToUpperInvariant()} ---");
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
            sb.AppendLine($"--- [RELATIONAL LAYER] {label.ToUpperInvariant()} ({maxHops} HOPS FROM {centerNodeId}) ---");
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
            sb.AppendLine($"--- [HIERARCHICAL LAYER] {label.ToUpperInvariant()} ---");
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
            sb.AppendLine($"--- [SEMANTIC LAYER] {label.ToUpperInvariant()} ---");
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
        /// Writes the compacted prompt bundle directly into a span-backed <see cref="IBufferWriter{T}"/>.
        /// </summary>
        public void WriteTo(IBufferWriter<char> writer, CompactionStrategy? overrideStrategy = null)
        {
            ArgumentNullException.ThrowIfNull(writer);
            string built = Build(overrideStrategy);
            if (built.Length == 0) return;
            Span<char> dest = writer.GetSpan(built.Length);
            built.AsSpan().CopyTo(dest);
            writer.Advance(built.Length);
        }

        /// <summary>
        /// Builds the finalized prompt context bundle by applying the specified or default compaction strategy
        /// against <see cref="TokenBudget.AvailableBudget"/> (honoring <see cref="TokenBudget.ReservedTokens"/>).
        /// </summary>
        public string Build(CompactionStrategy? overrideStrategy = null)
        {
            int effectiveBudget = Budget.AvailableBudget;
            if (effectiveBudget <= 0)
            {
                return string.Empty;
            }

            var strategy = overrideStrategy ?? Budget.DefaultStrategy;
            int totalTokens = _sections.Sum(s => s.EstimateTokens(Budget.Estimator));

            if (totalTokens <= effectiveBudget)
            {
                string assembledInitial = AssembleSections(_sections);
                if (Budget.EstimateTokens(assembledInitial) <= effectiveBudget)
                {
                    return assembledInitial;
                }
            }

            var compactedSections = strategy switch
            {
                CompactionStrategy.StrictPrune => CompactStrictPrune(_sections, effectiveBudget),
                CompactionStrategy.HeadTailTruncate => CompactHeadTail(_sections, effectiveBudget),
                CompactionStrategy.ProportionalDecay => CompactProportionalDecay(_sections, effectiveBudget),
                _ => CompactStrictPrune(_sections, effectiveBudget)
            };

            string finalResult = AssembleSections(compactedSections);
            while (Budget.EstimateTokens(finalResult) > effectiveBudget)
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
                else if (nonFixed.Count == 1)
                {
                    var sole = nonFixed[0];
                    int fixedTokens = compactedSections.Where(s => s.IsFixed).Sum(s => s.EstimateTokens(Budget.Estimator));
                    int allowed = Math.Max(0, effectiveBudget - fixedTokens - 1);
                    if (sole.StrategyOverride == CompactionStrategy.HeadTailTruncate && allowed > 0)
                    {
                        int idx = compactedSections.IndexOf(sole);
                        compactedSections[idx] = new BundleSection(sole.Name, sole.Priority, Budget.TruncateHeadTail(sole.Content, allowed), false, sole.StrategyOverride);
                        finalResult = AssembleSections(compactedSections);
                        if (Budget.EstimateTokens(finalResult) <= effectiveBudget)
                        {
                            break;
                        }
                    }

                    finalResult = Budget.TruncateToTokens(finalResult, effectiveBudget);
                    break;
                }
                else
                {
                    finalResult = Budget.TruncateToTokens(finalResult, effectiveBudget);
                    break;
                }
            }

            if (Budget.EstimateTokens(finalResult) > effectiveBudget)
            {
                finalResult = Budget.TruncateToTokens(finalResult, effectiveBudget);
            }

            return finalResult;
        }

        private List<BundleSection> CompactStrictPrune(List<BundleSection> source, int maxBudget)
        {
            var result = new List<BundleSection>(source);
            int currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));

            while (currentTokens > maxBudget)
            {
                var lowestNonFixed = result
                    .Where(s => !s.IsFixed)
                    .OrderBy(s => s.Priority)
                    .FirstOrDefault();

                if (lowestNonFixed == null)
                {
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

                // Respect per-section StrategyOverride before dropping the section outright
                if (lowestNonFixed.StrategyOverride is CompactionStrategy.HeadTailTruncate or CompactionStrategy.ProportionalDecay)
                {
                    int sectionTokens = lowestNonFixed.EstimateTokens(Budget.Estimator);
                    int excess = currentTokens - maxBudget;
                    int allowed = sectionTokens - excess - 1;
                    if (allowed > 0)
                    {
                        int idx = result.IndexOf(lowestNonFixed);
                        string compacted = lowestNonFixed.StrategyOverride == CompactionStrategy.HeadTailTruncate
                            ? Budget.TruncateHeadTail(lowestNonFixed.Content, allowed)
                            : Budget.TruncateToTokens(lowestNonFixed.Content, allowed);

                        // Clear StrategyOverride on the compacted clone so if further pruning is still needed, lower sections don't loop
                        result[idx] = new BundleSection(lowestNonFixed.Name, lowestNonFixed.Priority, compacted, false, null);
                        int updatedTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));
                        if (updatedTokens < currentTokens)
                        {
                            currentTokens = updatedTokens;
                            continue;
                        }
                    }
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
                    var secStrategy = section.StrategyOverride ?? CompactionStrategy.HeadTailTruncate;

                    if (secStrategy == CompactionStrategy.StrictPrune && origTokens > targetSectionTokens)
                    {
                        continue;
                    }

                    string compactedContent = secStrategy == CompactionStrategy.ProportionalDecay
                        ? Budget.TruncateToTokens(section.Content, targetSectionTokens)
                        : Budget.TruncateHeadTail(section.Content, targetSectionTokens);

                    result.Add(new BundleSection(section.Name, section.Priority, compactedContent, false, section.StrategyOverride));
                }
            }

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
                    var secStrategy = section.StrategyOverride ?? CompactionStrategy.ProportionalDecay;

                    if (secStrategy == CompactionStrategy.StrictPrune && allocatedTokens < sectionTokens)
                    {
                        continue;
                    }

                    string finalContent;
                    if (allocatedTokens < sectionTokens)
                    {
                        finalContent = secStrategy == CompactionStrategy.HeadTailTruncate
                            ? Budget.TruncateHeadTail(section.Content, allocatedTokens)
                            : Budget.TruncateToTokens(section.Content, allocatedTokens);
                    }
                    else
                    {
                        finalContent = section.Content;
                    }

                    result.Add(new BundleSection(section.Name, section.Priority, finalContent, false, section.StrategyOverride));
                }
            }

            int currentTokens = result.Sum(s => s.EstimateTokens(Budget.Estimator));
            if (currentTokens > maxBudget)
            {
                return CompactStrictPrune(result, maxBudget);
            }

            return result;
        }

        private static string AssembleSections(List<BundleSection> sections)
        {
            int estimatedLength = 0;
            for (int i = 0; i < sections.Count; i++)
            {
                string c = sections[i].Content;
                if (!string.IsNullOrEmpty(c))
                {
                    estimatedLength += c.Length + 2;
                }
            }

            var sb = new StringBuilder(estimatedLength);
            for (int i = 0; i < sections.Count; i++)
            {
                if (!string.IsNullOrEmpty(sections[i].Content))
                {
                    sb.Append(sections[i].Content);
                    if (!sections[i].Content.EndsWith('\n'))
                    {
                        sb.AppendLine();
                    }
                }
            }
            return sb.ToString();
        }
    }
}
