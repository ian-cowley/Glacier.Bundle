using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using Glacier.Bundle.Budgeting;

namespace Glacier.Bundle.Core
{
    /// <summary>
    /// An intelligent LLM context builder that respects token limits and reservations using active compaction strategies
    /// (<see cref="CompactionStrategy.StrictPrune"/>, <see cref="CompactionStrategy.HeadTailTruncate"/>, <see cref="CompactionStrategy.ProportionalDecay"/>)
    /// and per-section <see cref="BundleSection.StrategyOverride"/> policies.
    /// </summary>
    public class CompactingBundleBuilder
    {
        private struct SectionEntry
        {
            public string Name;
            public int Priority;
            public string Content;
            public bool IsFixed;
            public CompactionStrategy? StrategyOverride;
            public int OriginalIndex;
            public int CachedTokens;
        }

        private readonly List<BundleSection> _sections = new();
        private readonly List<SectionEntry> _workEntries = new();
        private readonly List<int> _workIndices = new();
        private readonly StringBuilder _scratchSb = new(1024);

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
            _scratchSb.Clear();
            BundleFormatting.AppendBundleHeader(_scratchSb, bundleName);
            return AddSection("Header", priority, _scratchSb.ToString(), isFixed: true);
        }

        public CompactingBundleBuilder AppendTabularRow(string name, int priority, string header, Func<StringBuilder, StringBuilder> formatter)
        {
            _scratchSb.Clear();
            BundleFormatting.AppendTabularRow(_scratchSb, header, formatter);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        public CompactingBundleBuilder AppendDataFrame(string name, int priority, string dataFrameKey, int maxRows = 20, string label = "DATA LAYER")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query DataFrame.");

            _scratchSb.Clear();
            BundleFormatting.AppendDataFrame(_scratchSb, Context, dataFrameKey, maxRows, label);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        public CompactingBundleBuilder AppendGraphTopology(string name, int priority, string graphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query GraphStore.");

            _scratchSb.Clear();
            BundleFormatting.AppendGraphTopology(_scratchSb, Context, graphKey, centerNodeId, maxHops, label);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        public CompactingBundleBuilder AppendCsrGraphTopology(string name, int priority, string csrGraphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query CsrGraphStore.");

            _scratchSb.Clear();
            BundleFormatting.AppendCsrGraphTopology(_scratchSb, Context, csrGraphKey, centerNodeId, maxHops, label);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        public CompactingBundleBuilder AppendDocumentTreeSection(string name, int priority, string treeKey, string sectionHeader, string label = "DOCUMENT TREE POLICY")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query DocTree.");

            _scratchSb.Clear();
            BundleFormatting.AppendDocumentTreeSection(_scratchSb, Context, treeKey, sectionHeader, label);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        public CompactingBundleBuilder AppendVectorContext(string name, int priority, string vectorKey, float[] queryVector, int topK, string label = "SEMANTIC HISTORY")
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query VectorIndex.");

            _scratchSb.Clear();
            BundleFormatting.AppendVectorContext(_scratchSb, Context, vectorKey, queryVector, topK, label);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        public CompactingBundleBuilder AppendHnswVectorContext(string name, int priority, string hnswKey, ReadOnlySpan<float> queryVector, int topK, string label = "SEMANTIC HISTORY", int efSearch = 64)
        {
            if (Context == null) throw new InvalidOperationException("BundleContext must be provided to query HnswVectorIndex.");

            _scratchSb.Clear();
            BundleFormatting.AppendHnswVectorContext(_scratchSb, Context, hnswKey, queryVector, topK, label, efSearch);
            return AddSection(name, priority, _scratchSb.ToString());
        }

        /// <summary>
        /// Writes the compacted prompt bundle directly into a span-backed <see cref="IBufferWriter{T}"/>
        /// without intermediate <see cref="StringBuilder"/> or <see cref="string"/> allocations when using <see cref="HeuristicTokenEstimator"/>.
        /// </summary>
        public void WriteTo(IBufferWriter<char> writer, CompactionStrategy? overrideStrategy = null)
        {
            ArgumentNullException.ThrowIfNull(writer);

            int count = PrepareCompactedEntries(overrideStrategy);
            if (count == 0)
            {
                return;
            }

            ReadOnlySpan<char> newlineSpan = Environment.NewLine.AsSpan();
            for (int i = 0; i < count; i++)
            {
                string content = _workEntries[i].Content;
                if (string.IsNullOrEmpty(content))
                {
                    continue;
                }

                WriteSpan(writer, content.AsSpan());
                if (!content.EndsWith('\n'))
                {
                    WriteSpan(writer, newlineSpan);
                }
            }
        }

        private static void WriteSpan(IBufferWriter<char> writer, ReadOnlySpan<char> span)
        {
            while (!span.IsEmpty)
            {
                Span<char> dest = writer.GetSpan();
                if (dest.IsEmpty)
                {
                    dest = writer.GetSpan(span.Length);
                }
                int toCopy = Math.Min(span.Length, dest.Length);
                span.Slice(0, toCopy).CopyTo(dest);
                writer.Advance(toCopy);
                span = span.Slice(toCopy);
            }
        }

        /// <summary>
        /// Builds the finalized prompt context bundle by applying the specified or default compaction strategy
        /// against <see cref="TokenBudget.AvailableBudget"/> (honoring <see cref="TokenBudget.ReservedTokens"/>).
        /// </summary>
        public string Build(CompactionStrategy? overrideStrategy = null)
        {
            int count = PrepareCompactedEntries(overrideStrategy);
            if (count == 0)
            {
                return string.Empty;
            }

            int newlineLen = Environment.NewLine.Length;
            int totalChars = 0;
            for (int i = 0; i < count; i++)
            {
                string c = _workEntries[i].Content;
                if (!string.IsNullOrEmpty(c))
                {
                    totalChars += c.Length + (c.EndsWith('\n') ? 0 : newlineLen);
                }
            }

            if (totalChars == 0)
            {
                return string.Empty;
            }

            _scratchSb.Clear();
            _scratchSb.EnsureCapacity(totalChars);
            for (int i = 0; i < count; i++)
            {
                string c = _workEntries[i].Content;
                if (!string.IsNullOrEmpty(c))
                {
                    _scratchSb.Append(c);
                    if (!c.EndsWith('\n'))
                    {
                        _scratchSb.AppendLine();
                    }
                }
            }

            return _scratchSb.ToString();
        }

        private int PrepareCompactedEntries(CompactionStrategy? overrideStrategy)
        {
            _workEntries.Clear();
            int effectiveBudget = Budget.AvailableBudget;
            if (effectiveBudget <= 0 || _sections.Count == 0)
            {
                return 0;
            }

            int newlineLen = Environment.NewLine.Length;
            int newlineTokens = Math.Max(1, Budget.Estimator.EstimateTokens(Environment.NewLine));
            int totalChars = 0;
            int additiveTokens = 0;

            for (int i = 0; i < _sections.Count; i++)
            {
                BundleSection s = _sections[i];
                string content = s.Content ?? string.Empty;
                int tokens = string.IsNullOrEmpty(content) ? 0 : Budget.Estimator.EstimateTokens(content);
                _workEntries.Add(new SectionEntry
                {
                    Name = s.Name,
                    Priority = s.Priority,
                    Content = content,
                    IsFixed = s.IsFixed,
                    StrategyOverride = s.StrategyOverride,
                    OriginalIndex = i,
                    CachedTokens = tokens
                });

                if (!string.IsNullOrEmpty(content))
                {
                    int dChars = content.EndsWith('\n') ? 0 : newlineLen;
                    int dTokens = content.EndsWith('\n') ? 0 : newlineTokens;
                    totalChars += content.Length + dChars;
                    additiveTokens += tokens + dTokens;
                }
            }

            if (totalChars == 0)
            {
                return 0;
            }

            if (Budget.Estimator is HeuristicTokenEstimator heuristic)
            {
                if (heuristic.EstimateTokensForLength(totalChars) <= effectiveBudget)
                {
                    return _workEntries.Count;
                }
            }
            else if (additiveTokens <= effectiveBudget)
            {
                if (EstimateAssembledExact(newlineLen) <= effectiveBudget)
                {
                    return _workEntries.Count;
                }
            }

            CompactionStrategy strategy = overrideStrategy ?? Budget.DefaultStrategy;
            switch (strategy)
            {
                case CompactionStrategy.StrictPrune:
                    CompactStrictPrune(effectiveBudget, newlineLen, newlineTokens);
                    break;
                case CompactionStrategy.HeadTailTruncate:
                    CompactHeadTail(effectiveBudget, newlineLen, newlineTokens);
                    break;
                case CompactionStrategy.ProportionalDecay:
                    CompactProportionalDecay(effectiveBudget, newlineLen, newlineTokens);
                    break;
                default:
                    CompactStrictPrune(effectiveBudget, newlineLen, newlineTokens);
                    break;
            }

            EnsureWithinBudget(strategy, effectiveBudget, newlineLen, newlineTokens);
            return _workEntries.Count;
        }

        private static int DelimiterCharsFor(string content, int newlineLen) =>
            !string.IsNullOrEmpty(content) && !content.EndsWith('\n') ? newlineLen : 0;

        private static int DelimiterTokensFor(string content, int newlineTokens) =>
            !string.IsNullOrEmpty(content) && !content.EndsWith('\n') ? newlineTokens : 0;

        private int EstimateWorkingTokens(int totalChars, int additiveTokens) =>
            Budget.Estimator is HeuristicTokenEstimator heuristic
                ? heuristic.EstimateTokensForLength(totalChars)
                : additiveTokens;

        private int EstimateAssembledExact(int newlineLen)
        {
            if (Budget.Estimator is HeuristicTokenEstimator heuristic)
            {
                int totalChars = 0;
                for (int i = 0; i < _workEntries.Count; i++)
                {
                    string c = _workEntries[i].Content;
                    if (!string.IsNullOrEmpty(c))
                    {
                        totalChars += c.Length + (c.EndsWith('\n') ? 0 : newlineLen);
                    }
                }
                return heuristic.EstimateTokensForLength(totalChars);
            }

            _scratchSb.Clear();
            for (int i = 0; i < _workEntries.Count; i++)
            {
                string c = _workEntries[i].Content;
                if (!string.IsNullOrEmpty(c))
                {
                    _scratchSb.Append(c);
                    if (!c.EndsWith('\n'))
                    {
                        _scratchSb.AppendLine();
                    }
                }
            }
            return _scratchSb.Length == 0 ? 0 : Budget.EstimateTokens(_scratchSb.ToString());
        }

        private void CompactStrictPrune(int maxBudget, int newlineLen, int newlineTokens)
        {
            _workIndices.Clear();
            int nonEmptyFixedCount = 0;
            int activeNonFixedCount = 0;
            int totalChars = 0;
            int additiveTokens = 0;

            for (int i = 0; i < _workEntries.Count; i++)
            {
                var entry = _workEntries[i];
                if (string.IsNullOrEmpty(entry.Content)) continue;

                totalChars += entry.Content.Length + DelimiterCharsFor(entry.Content, newlineLen);
                additiveTokens += entry.CachedTokens + DelimiterTokensFor(entry.Content, newlineTokens);

                if (entry.IsFixed)
                {
                    nonEmptyFixedCount++;
                }
                else
                {
                    activeNonFixedCount++;
                    _workIndices.Add(i);
                }
            }

            _workIndices.Sort((a, b) =>
            {
                int cmp = _workEntries[a].Priority.CompareTo(_workEntries[b].Priority);
                return cmp != 0 ? cmp : _workEntries[a].OriginalIndex.CompareTo(_workEntries[b].OriginalIndex);
            });

            int cursor = 0;
            while (EstimateWorkingTokens(totalChars, additiveTokens) > maxBudget && cursor < _workIndices.Count)
            {
                int idx = _workIndices[cursor];
                var entry = _workEntries[idx];
                if (string.IsNullOrEmpty(entry.Content))
                {
                    cursor++;
                    continue;
                }

                // If this is the sole remaining non-fixed section and no fixed sections exist,
                // truncate it to fit budget rather than dropping the entire bundle to empty string.
                if (activeNonFixedCount == 1 && nonEmptyFixedCount == 0)
                {
                    ShrinkSingleEntryToFit(idx, maxBudget, entry.StrategyOverride ?? CompactionStrategy.StrictPrune, newlineLen, newlineTokens);
                    break;
                }

                int currentWorking = EstimateWorkingTokens(totalChars, additiveTokens);
                int oldDChars = DelimiterCharsFor(entry.Content, newlineLen);
                int oldDTokens = DelimiterTokensFor(entry.Content, newlineTokens);

                if (entry.StrategyOverride is CompactionStrategy.HeadTailTruncate or CompactionStrategy.ProportionalDecay)
                {
                    int excess = currentWorking - maxBudget;
                    int allowed = entry.CachedTokens - excess;
                    if (allowed > 0)
                    {
                        string compacted = entry.StrategyOverride == CompactionStrategy.HeadTailTruncate
                            ? Budget.TruncateHeadTail(entry.Content, allowed)
                            : Budget.TruncateToTokens(entry.Content, allowed);

                        if (!string.IsNullOrEmpty(compacted))
                        {
                            int newTokens = Budget.Estimator.EstimateTokens(compacted);
                            int newDChars = DelimiterCharsFor(compacted, newlineLen);
                            int newDTokens = DelimiterTokensFor(compacted, newlineTokens);

                            int newTotalChars = totalChars - (entry.Content.Length + oldDChars) + (compacted.Length + newDChars);
                            int newAdditiveTokens = additiveTokens - (entry.CachedTokens + oldDTokens) + (newTokens + newDTokens);

                            if (EstimateWorkingTokens(newTotalChars, newAdditiveTokens) < currentWorking)
                            {
                                entry.Content = compacted;
                                entry.CachedTokens = newTokens;
                                entry.StrategyOverride = null;
                                _workEntries[idx] = entry;
                                totalChars = newTotalChars;
                                additiveTokens = newAdditiveTokens;
                                continue;
                            }
                        }
                    }
                }

                totalChars -= entry.Content.Length + oldDChars;
                additiveTokens -= entry.CachedTokens + oldDTokens;
                entry.Content = string.Empty;
                entry.CachedTokens = 0;
                _workEntries[idx] = entry;
                activeNonFixedCount--;
                cursor++;
            }
        }

        private void CompactHeadTail(int maxBudget, int newlineLen, int newlineTokens)
        {
            CompactWeightedWithWaterFilling(
                maxBudget,
                newlineLen,
                newlineTokens,
                defaultSectionStrategy: CompactionStrategy.HeadTailTruncate,
                weightByPriority: false);
        }

        private void CompactProportionalDecay(int maxBudget, int newlineLen, int newlineTokens)
        {
            CompactWeightedWithWaterFilling(
                maxBudget,
                newlineLen,
                newlineTokens,
                defaultSectionStrategy: CompactionStrategy.ProportionalDecay,
                weightByPriority: true);
        }

        private void CompactWeightedWithWaterFilling(
            int maxBudget,
            int newlineLen,
            int newlineTokens,
            CompactionStrategy defaultSectionStrategy,
            bool weightByPriority)
        {
            _workIndices.Clear();
            int nonEmptyFixedCount = 0;
            int fixedTokens = 0;
            int nonFixedDelimiterReserve = 0;

            for (int i = 0; i < _workEntries.Count; i++)
            {
                var entry = _workEntries[i];
                if (string.IsNullOrEmpty(entry.Content)) continue;

                int dTokens = DelimiterTokensFor(entry.Content, newlineTokens);
                if (entry.IsFixed)
                {
                    nonEmptyFixedCount++;
                    fixedTokens += entry.CachedTokens + dTokens;
                }
                else
                {
                    nonFixedDelimiterReserve += dTokens;
                    _workIndices.Add(i);
                }
            }

            int n = _workIndices.Count;
            int poolBudget = maxBudget - fixedTokens - nonFixedDelimiterReserve;
            if (n == 0 || poolBudget <= 0)
            {
                CompactStrictPrune(maxBudget, newlineLen, newlineTokens);
                return;
            }

            int bestNonFixedIdx = _workIndices[0];
            for (int k = 1; k < n; k++)
            {
                int candIdx = _workIndices[k];
                var cand = _workEntries[candIdx];
                var best = _workEntries[bestNonFixedIdx];
                if (cand.Priority > best.Priority || (cand.Priority == best.Priority && cand.OriginalIndex < best.OriginalIndex))
                {
                    bestNonFixedIdx = candIdx;
                }
            }
            string bestNonFixedOriginalContent = _workEntries[bestNonFixedIdx].Content;
            int bestNonFixedOriginalTokens = _workEntries[bestNonFixedIdx].CachedTokens;
            CompactionStrategy bestNonFixedStrategy = _workEntries[bestNonFixedIdx].StrategyOverride ?? defaultSectionStrategy;

            int[] alloc = ArrayPool<int>.Shared.Rent(n);
            bool[] settled = ArrayPool<bool>.Shared.Rent(n);
            long[] remNums = ArrayPool<long>.Shared.Rent(n);
            int[] remOrder = ArrayPool<int>.Shared.Rent(n);

            try
            {
                Array.Clear(alloc, 0, n);
                Array.Clear(settled, 0, n);
                int activeNonFixedCount = n;

                // Iterative water-filling:
                // 1) Converge unconstrained slack redistribution using consistent pass snapshots
                //    (passBudget / passWeight) so short sections release unused tokens first.
                // 2) Only after slack settles, drop the single lowest-priority unsatisfied
                //    StrictPrune-overridden section (unless it is the sole non-fixed section
                //    with no fixed sections, which is preserved for truncation), reclaim its
                //    delimiter tokens, and repeat water-filling from step 1.
                while (true)
                {
                    bool settledAny = true;
                    while (settledAny)
                    {
                        settledAny = false;
                        long passWeight = 0;
                        for (int k = 0; k < n; k++)
                        {
                            if (!settled[k])
                            {
                                var e = _workEntries[_workIndices[k]];
                                passWeight += weightByPriority ? Math.Max(1, (long)e.Priority) : Math.Max(1, (long)e.CachedTokens);
                            }
                        }

                        if (passWeight <= 0 || poolBudget <= 0)
                        {
                            break;
                        }

                        int passBudget = poolBudget;
                        for (int k = 0; k < n; k++)
                        {
                            if (settled[k]) continue;

                            int idx = _workIndices[k];
                            var e = _workEntries[idx];
                            long w = weightByPriority ? Math.Max(1, (long)e.Priority) : Math.Max(1, (long)e.CachedTokens);
                            int floorShare = (int)Math.Min(int.MaxValue, ((long)passBudget * w) / passWeight);

                            if (floorShare >= e.CachedTokens)
                            {
                                alloc[k] = e.CachedTokens;
                                poolBudget -= e.CachedTokens;
                                settled[k] = true;
                                settledAny = true;
                            }
                        }
                    }

                    if (activeNonFixedCount == 1 && nonEmptyFixedCount == 0)
                    {
                        break;
                    }

                    long remActiveWeight = 0;
                    for (int k = 0; k < n; k++)
                    {
                        if (!settled[k])
                        {
                            var e = _workEntries[_workIndices[k]];
                            remActiveWeight += weightByPriority ? Math.Max(1, (long)e.Priority) : Math.Max(1, (long)e.CachedTokens);
                        }
                    }

                    if (remActiveWeight <= 0)
                    {
                        break;
                    }

                    int dropK = -1;
                    int dropPriority = 0;
                    int dropOriginalIndex = 0;
                    for (int k = 0; k < n; k++)
                    {
                        if (settled[k]) continue;

                        var e = _workEntries[_workIndices[k]];
                        var secStrategy = e.StrategyOverride ?? defaultSectionStrategy;
                        if (secStrategy != CompactionStrategy.StrictPrune) continue;

                        long w = weightByPriority ? Math.Max(1, (long)e.Priority) : Math.Max(1, (long)e.CachedTokens);
                        int floorShare = poolBudget > 0
                            ? (int)Math.Min(int.MaxValue, ((long)poolBudget * w) / remActiveWeight)
                            : 0;

                        if (floorShare < e.CachedTokens)
                        {
                            if (dropK == -1 ||
                                e.Priority < dropPriority ||
                                (e.Priority == dropPriority && e.OriginalIndex < dropOriginalIndex))
                            {
                                dropK = k;
                                dropPriority = e.Priority;
                                dropOriginalIndex = e.OriginalIndex;
                            }
                        }
                    }

                    if (dropK == -1)
                    {
                        break;
                    }

                    int dropIdx = _workIndices[dropK];
                    var dropEntry = _workEntries[dropIdx];
                    poolBudget += DelimiterTokensFor(dropEntry.Content, newlineTokens);
                    dropEntry.Content = string.Empty;
                    dropEntry.CachedTokens = 0;
                    _workEntries[dropIdx] = dropEntry;
                    alloc[dropK] = 0;
                    settled[dropK] = true;
                    activeNonFixedCount--;
                }

                // Distribute remaining poolBudget via floor + largest-remainder across remaining constrained sections
                long remWeight = 0;
                int constrainedCount = 0;
                for (int k = 0; k < n; k++)
                {
                    if (!settled[k])
                    {
                        var e = _workEntries[_workIndices[k]];
                        remWeight += weightByPriority ? Math.Max(1, (long)e.Priority) : Math.Max(1, (long)e.CachedTokens);
                        remOrder[constrainedCount++] = k;
                    }
                }

                if (constrainedCount > 0)
                {
                    int allocatedSum = 0;
                    int effectivePool = Math.Max(0, poolBudget);
                    for (int m = 0; m < constrainedCount; m++)
                    {
                        int k = remOrder[m];
                        var e = _workEntries[_workIndices[k]];
                        long w = weightByPriority ? Math.Max(1, (long)e.Priority) : Math.Max(1, (long)e.CachedTokens);
                        long num = (long)effectivePool * w;
                        int flUnbounded = remWeight > 0 ? (int)Math.Min(int.MaxValue, num / remWeight) : 0;
                        int fl = Math.Min(e.CachedTokens, flUnbounded);
                        alloc[k] = fl;
                        remNums[k] = (remWeight > 0 && fl < e.CachedTokens) ? num % remWeight : 0;
                        allocatedSum += fl;
                    }

                    int remainder = effectivePool - allocatedSum;
                    if (remainder > 0)
                    {
                        Array.Sort(remOrder, 0, constrainedCount, Comparer<int>.Create((ka, kb) =>
                        {
                            int cmp = remNums[kb].CompareTo(remNums[ka]);
                            if (cmp != 0) return cmp;
                            var ea = _workEntries[_workIndices[ka]];
                            var eb = _workEntries[_workIndices[kb]];
                            cmp = eb.Priority.CompareTo(ea.Priority);
                            return cmp != 0 ? cmp : ea.OriginalIndex.CompareTo(eb.OriginalIndex);
                        }));

                        for (int m = 0; m < constrainedCount && remainder > 0; m++)
                        {
                            int k = remOrder[m];
                            var e = _workEntries[_workIndices[k]];
                            if (alloc[k] < e.CachedTokens)
                            {
                                alloc[k]++;
                                remainder--;
                            }
                        }
                    }

                    // Process lowest-priority sections first so if all but one section collapse,
                    // the highest-priority (Priority DESC, OriginalIndex ASC) section survives to ShrinkSingleEntryToFit.
                    Array.Sort(remOrder, 0, constrainedCount, Comparer<int>.Create((ka, kb) =>
                    {
                        var ea = _workEntries[_workIndices[ka]];
                        var eb = _workEntries[_workIndices[kb]];
                        int cmp = ea.Priority.CompareTo(eb.Priority);
                        return cmp != 0 ? cmp : eb.OriginalIndex.CompareTo(ea.OriginalIndex);
                    }));

                    for (int m = 0; m < constrainedCount; m++)
                    {
                        int k = remOrder[m];
                        int idx = _workIndices[k];
                        var e = _workEntries[idx];
                        int targetTokens = alloc[k];
                        var secStrategy = e.StrategyOverride ?? defaultSectionStrategy;
                        if (targetTokens <= 0)
                        {
                            if (activeNonFixedCount == 1 && nonEmptyFixedCount == 0)
                            {
                                ShrinkSingleEntryToFit(idx, maxBudget, secStrategy, newlineLen, newlineTokens);
                                continue;
                            }
                            e.Content = string.Empty;
                            e.CachedTokens = 0;
                            _workEntries[idx] = e;
                            activeNonFixedCount--;
                        }
                        else if (targetTokens < e.CachedTokens)
                        {
                            int oldDelim = DelimiterTokensFor(e.Content, newlineTokens);
                            string compacted = secStrategy == CompactionStrategy.HeadTailTruncate
                                ? Budget.TruncateHeadTail(e.Content, targetTokens)
                                : Budget.TruncateToTokens(e.Content, targetTokens);

                            if (oldDelim == 0 && !string.IsNullOrEmpty(compacted) && !compacted.EndsWith('\n'))
                            {
                                int adjustedTarget = targetTokens - newlineTokens;
                                if (adjustedTarget <= 0)
                                {
                                    if (activeNonFixedCount == 1 && nonEmptyFixedCount == 0)
                                    {
                                        ShrinkSingleEntryToFit(idx, maxBudget, secStrategy, newlineLen, newlineTokens);
                                        continue;
                                    }
                                    compacted = string.Empty;
                                }
                                else
                                {
                                    compacted = secStrategy == CompactionStrategy.HeadTailTruncate
                                        ? Budget.TruncateHeadTail(e.Content, adjustedTarget)
                                        : Budget.TruncateToTokens(e.Content, adjustedTarget);
                                }
                            }

                            if (string.IsNullOrEmpty(compacted))
                            {
                                if (activeNonFixedCount == 1 && nonEmptyFixedCount == 0)
                                {
                                    ShrinkSingleEntryToFit(idx, maxBudget, secStrategy, newlineLen, newlineTokens);
                                    continue;
                                }
                                e.Content = string.Empty;
                                e.CachedTokens = 0;
                                _workEntries[idx] = e;
                                activeNonFixedCount--;
                            }
                            else
                            {
                                e.Content = compacted;
                                e.CachedTokens = Budget.Estimator.EstimateTokens(compacted);
                                _workEntries[idx] = e;
                            }
                        }
                    }
                }

                if (nonEmptyFixedCount == 0 && maxBudget > 0 && string.IsNullOrEmpty(_workEntries[bestNonFixedIdx].Content) && bestNonFixedStrategy != CompactionStrategy.StrictPrune)
                {
                    for (int k = 0; k < n; k++)
                    {
                        int wi = _workIndices[k];
                        var cleared = _workEntries[wi];
                        cleared.Content = string.Empty;
                        cleared.CachedTokens = 0;
                        _workEntries[wi] = cleared;
                    }
                    var restored = _workEntries[bestNonFixedIdx];
                    restored.Content = bestNonFixedOriginalContent;
                    restored.CachedTokens = bestNonFixedOriginalTokens;
                    _workEntries[bestNonFixedIdx] = restored;
                    ShrinkSingleEntryToFit(bestNonFixedIdx, maxBudget, bestNonFixedStrategy, newlineLen, newlineTokens);
                }

                if (nonEmptyFixedCount == 0 && maxBudget > 0)
                {
                    bool anyNonEmpty = false;
                    for (int i = 0; i < _workEntries.Count; i++)
                    {
                        if (!string.IsNullOrEmpty(_workEntries[i].Content))
                        {
                            anyNonEmpty = true;
                            break;
                        }
                    }

                    if (!anyNonEmpty)
                    {
                        var restored = _workEntries[bestNonFixedIdx];
                        restored.Content = bestNonFixedOriginalContent;
                        restored.CachedTokens = bestNonFixedOriginalTokens;
                        _workEntries[bestNonFixedIdx] = restored;
                        ShrinkSingleEntryToFit(bestNonFixedIdx, maxBudget, bestNonFixedStrategy, newlineLen, newlineTokens);
                    }
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(alloc);
                ArrayPool<bool>.Shared.Return(settled);
                ArrayPool<long>.Shared.Return(remNums);
                ArrayPool<int>.Shared.Return(remOrder);
            }
        }

        private void EnsureWithinBudget(CompactionStrategy strategy, int effectiveBudget, int newlineLen, int newlineTokens)
        {
            while (EstimateAssembledExact(newlineLen) > effectiveBudget)
            {
                int activeNonFixedCount = 0;
                int activeFixedCount = 0;
                int lowestNonFixedIdx = -1;
                int lowestPriority = int.MaxValue;
                int lowestOrigIdx = int.MaxValue;

                for (int i = 0; i < _workEntries.Count; i++)
                {
                    var e = _workEntries[i];
                    if (string.IsNullOrEmpty(e.Content)) continue;

                    if (e.IsFixed)
                    {
                        activeFixedCount++;
                    }
                    else
                    {
                        activeNonFixedCount++;
                        if (lowestNonFixedIdx == -1 ||
                            e.Priority < lowestPriority ||
                            (e.Priority == lowestPriority && e.OriginalIndex < lowestOrigIdx))
                        {
                            lowestNonFixedIdx = i;
                            lowestPriority = e.Priority;
                            lowestOrigIdx = e.OriginalIndex;
                        }
                    }
                }

                if (activeNonFixedCount > 1)
                {
                    var e = _workEntries[lowestNonFixedIdx];
                    e.Content = string.Empty;
                    e.CachedTokens = 0;
                    _workEntries[lowestNonFixedIdx] = e;
                    continue;
                }

                if (activeNonFixedCount == 1)
                {
                    var sole = _workEntries[lowestNonFixedIdx];
                    int fixedTokens = EstimateFixedOnlyTokens(newlineLen, newlineTokens);
                    int soleDelim = DelimiterTokensFor(sole.Content, newlineTokens);
                    int allowed = effectiveBudget - fixedTokens - soleDelim;
                    CompactionStrategy soleStrategy = sole.StrategyOverride ?? strategy;

                    if (allowed <= 0)
                    {
                        if (activeFixedCount > 0)
                        {
                            sole.Content = string.Empty;
                            sole.CachedTokens = 0;
                            _workEntries[lowestNonFixedIdx] = sole;
                            continue;
                        }

                        ShrinkSingleEntryToFit(lowestNonFixedIdx, effectiveBudget, soleStrategy, newlineLen, newlineTokens);
                        break;
                    }

                    string originalContent = sole.Content;
                    string compacted = soleStrategy == CompactionStrategy.HeadTailTruncate
                        ? Budget.TruncateHeadTail(originalContent, allowed)
                        : Budget.TruncateToTokens(originalContent, allowed);

                    sole.Content = compacted;
                    sole.CachedTokens = string.IsNullOrEmpty(compacted) ? 0 : Budget.Estimator.EstimateTokens(compacted);
                    _workEntries[lowestNonFixedIdx] = sole;

                    if (EstimateAssembledExact(newlineLen) <= effectiveBudget)
                    {
                        break;
                    }

                    // Tighten allowed tokens if delimiter/estimator non-additivity pushed slightly over
                    int lo = 1;
                    int hi = allowed - 1;
                    string best = string.Empty;
                    while (lo <= hi)
                    {
                        int mid = lo + ((hi - lo) >> 1);
                        string candidate = soleStrategy == CompactionStrategy.HeadTailTruncate
                            ? Budget.TruncateHeadTail(originalContent, mid)
                            : Budget.TruncateToTokens(originalContent, mid);

                        sole.Content = candidate;
                        _workEntries[lowestNonFixedIdx] = sole;
                        if (EstimateAssembledExact(newlineLen) <= effectiveBudget)
                        {
                            best = candidate;
                            lo = mid + 1;
                        }
                        else
                        {
                            hi = mid - 1;
                        }
                    }

                    if (!string.IsNullOrEmpty(best))
                    {
                        sole.Content = best;
                        sole.CachedTokens = Budget.Estimator.EstimateTokens(best);
                        _workEntries[lowestNonFixedIdx] = sole;
                        break;
                    }

                    if (activeFixedCount > 0)
                    {
                        sole.Content = string.Empty;
                        sole.CachedTokens = 0;
                        _workEntries[lowestNonFixedIdx] = sole;
                        continue;
                    }

                    sole.Content = originalContent;
                    _workEntries[lowestNonFixedIdx] = sole;
                    ShrinkSingleEntryToFit(lowestNonFixedIdx, effectiveBudget, soleStrategy, newlineLen, newlineTokens);
                    break;
                }

                // Only fixed sections remain and exceed effectiveBudget: shrink fixed sections per-section
                // rather than tail-chopping the assembled buffer so fixed headers AND footers survive.
                ShrinkFixedSectionsToFit(effectiveBudget, newlineLen, newlineTokens);
                break;
            }
        }

        private int EstimateFixedOnlyTokens(int newlineLen, int newlineTokens)
        {
            if (Budget.Estimator is HeuristicTokenEstimator heuristic)
            {
                int chars = 0;
                for (int i = 0; i < _workEntries.Count; i++)
                {
                    var e = _workEntries[i];
                    if (e.IsFixed && !string.IsNullOrEmpty(e.Content))
                    {
                        chars += e.Content.Length + DelimiterCharsFor(e.Content, newlineLen);
                    }
                }
                return heuristic.EstimateTokensForLength(chars);
            }

            int sum = 0;
            for (int i = 0; i < _workEntries.Count; i++)
            {
                var e = _workEntries[i];
                if (e.IsFixed && !string.IsNullOrEmpty(e.Content))
                {
                    sum += e.CachedTokens + DelimiterTokensFor(e.Content, newlineTokens);
                }
            }
            return sum;
        }

        private void ShrinkSingleEntryToFit(int entryIndex, int maxBudget, CompactionStrategy strategy, int newlineLen, int newlineTokens)
        {
            var entry = _workEntries[entryIndex];
            string original = entry.Content;
            if (string.IsNullOrEmpty(original)) return;

            int dTokens = DelimiterTokensFor(original, newlineTokens);
            int maxTry = Math.Max(1, maxBudget - dTokens);

            // Binary search largest token target that fits including assembled delimiter
            int lo = 1;
            int hi = maxTry;
            string? best = null;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                string candidate = strategy == CompactionStrategy.HeadTailTruncate
                    ? Budget.TruncateHeadTail(original, mid)
                    : Budget.TruncateToTokens(original, mid);

                if (string.IsNullOrEmpty(candidate))
                {
                    lo = mid + 1;
                    continue;
                }

                entry.Content = candidate;
                _workEntries[entryIndex] = entry;
                if (EstimateAssembledExact(newlineLen) <= maxBudget)
                {
                    best = candidate;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }

            if (!string.IsNullOrEmpty(best))
            {
                entry.Content = best;
                entry.CachedTokens = Budget.Estimator.EstimateTokens(best);
                _workEntries[entryIndex] = entry;
                return;
            }

            // Fallback when maxBudget is tiny (e.g., 1 token) and Environment.NewLine pushes candidate over:
            // use TruncateToTokens up to maxBudget and end with '\n' if needed so AssembleSections omits CRLF.
            string fallback = Budget.TruncateToTokens(original, maxBudget);
            if (!string.IsNullOrEmpty(fallback))
            {
                entry.Content = fallback;
                _workEntries[entryIndex] = entry;
                if (EstimateAssembledExact(newlineLen) <= maxBudget)
                {
                    entry.CachedTokens = Budget.Estimator.EstimateTokens(fallback);
                    _workEntries[entryIndex] = entry;
                    return;
                }

                if (!fallback.EndsWith('\n'))
                {
                    string appendedNl = fallback + "\n";
                    entry.Content = appendedNl;
                    _workEntries[entryIndex] = entry;
                    if (EstimateAssembledExact(newlineLen) <= maxBudget)
                    {
                        entry.CachedTokens = Budget.Estimator.EstimateTokens(appendedNl);
                        _workEntries[entryIndex] = entry;
                        return;
                    }
                }

                if (fallback.Length > 1)
                {
                    int nlLen = HeuristicTokenEstimator.SnapEndToGraphemeBoundary(fallback, fallback.Length - 1);
                    if (nlLen > 0)
                    {
                        string nlFallback = fallback.Substring(0, nlLen) + "\n";
                        entry.Content = nlFallback;
                        _workEntries[entryIndex] = entry;
                        if (EstimateAssembledExact(newlineLen) <= maxBudget)
                        {
                            entry.CachedTokens = Budget.Estimator.EstimateTokens(nlFallback);
                            _workEntries[entryIndex] = entry;
                            return;
                        }

                        // Binary search shorter prefix + "\n" for super-additive or newline-heavy estimators
                        int nlLo = 1;
                        int nlHi = nlLen - 1;
                        string? bestNl = null;
                        while (nlLo <= nlHi)
                        {
                            int nlMid = nlLo + ((nlHi - nlLo) >> 1);
                            int snapped = HeuristicTokenEstimator.SnapEndToGraphemeBoundary(fallback, nlMid);
                            if (snapped <= 0)
                            {
                                nlLo = nlMid + 1;
                                continue;
                            }
                            string candNl = fallback.Substring(0, snapped) + "\n";
                            entry.Content = candNl;
                            _workEntries[entryIndex] = entry;
                            if (EstimateAssembledExact(newlineLen) <= maxBudget)
                            {
                                bestNl = candNl;
                                nlLo = nlMid + 1;
                            }
                            else
                            {
                                nlHi = nlMid - 1;
                            }
                        }

                        if (!string.IsNullOrEmpty(bestNl))
                        {
                            entry.Content = bestNl;
                            entry.CachedTokens = Budget.Estimator.EstimateTokens(bestNl);
                            _workEntries[entryIndex] = entry;
                            return;
                        }
                    }
                }
            }

            entry.Content = string.Empty;
            entry.CachedTokens = 0;
            _workEntries[entryIndex] = entry;
        }

        private void ShrinkFixedSectionsToFit(int effectiveBudget, int newlineLen, int newlineTokens)
        {
            _workIndices.Clear();
            for (int i = 0; i < _workEntries.Count; i++)
            {
                var e = _workEntries[i];
                if (e.IsFixed && !string.IsNullOrEmpty(e.Content))
                {
                    _workIndices.Add(i);
                }
            }

            if (_workIndices.Count == 0) return;
            if (_workIndices.Count == 1)
            {
                ShrinkSingleEntryToFit(_workIndices[0], effectiveBudget, CompactionStrategy.StrictPrune, newlineLen, newlineTokens);
                return;
            }

            int bestFixedIdx = _workIndices[0];
            for (int k = 1; k < _workIndices.Count; k++)
            {
                int candIdx = _workIndices[k];
                var cand = _workEntries[candIdx];
                var best = _workEntries[bestFixedIdx];
                if (cand.Priority > best.Priority || (cand.Priority == best.Priority && cand.OriginalIndex < best.OriginalIndex))
                {
                    bestFixedIdx = candIdx;
                }
            }
            string bestFixedOriginalContent = _workEntries[bestFixedIdx].Content;
            int bestFixedOriginalTokens = _workEntries[bestFixedIdx].CachedTokens;

            // Drop lowest-priority fixed sections only if effectiveBudget cannot even accommodate
            // 1 token + delimiter per remaining fixed section.
            while (_workIndices.Count > 1)
            {
                int minNeeded = 0;
                for (int k = 0; k < _workIndices.Count; k++)
                {
                    var e = _workEntries[_workIndices[k]];
                    minNeeded += 1 + DelimiterTokensFor(e.Content, newlineTokens);
                }
                if (minNeeded <= effectiveBudget) break;

                int dropPos = 0;
                for (int k = 1; k < _workIndices.Count; k++)
                {
                    var cand = _workEntries[_workIndices[k]];
                    var cur = _workEntries[_workIndices[dropPos]];
                    if (cand.Priority < cur.Priority || (cand.Priority == cur.Priority && cand.OriginalIndex < cur.OriginalIndex))
                    {
                        dropPos = k;
                    }
                }

                int dropIdx = _workIndices[dropPos];
                var dropped = _workEntries[dropIdx];
                dropped.Content = string.Empty;
                dropped.CachedTokens = 0;
                _workEntries[dropIdx] = dropped;
                _workIndices.RemoveAt(dropPos);
            }

            int m = _workIndices.Count;
            if (m == 1)
            {
                ShrinkSingleEntryToFit(_workIndices[0], effectiveBudget, CompactionStrategy.StrictPrune, newlineLen, newlineTokens);
                return;
            }

            int delimReserve = 0;
            for (int k = 0; k < m; k++)
            {
                delimReserve += DelimiterTokensFor(_workEntries[_workIndices[k]].Content, newlineTokens);
            }

            int contentBudget = Math.Max(m, effectiveBudget - delimReserve);
            int[] alloc = ArrayPool<int>.Shared.Rent(m);
            bool[] settled = ArrayPool<bool>.Shared.Rent(m);
            int[] remOrder = ArrayPool<int>.Shared.Rent(m);

            try
            {
                // Give every surviving fixed section at least 1 token so both headers and footers survive
                for (int k = 0; k < m; k++)
                {
                    alloc[k] = 1;
                    settled[k] = _workEntries[_workIndices[k]].CachedTokens <= 1;
                }

                int remainingExtra = Math.Max(0, contentBudget - m);
                bool changed = true;
                while (changed && remainingExtra > 0)
                {
                    changed = false;
                    long activeWeight = 0;
                    for (int k = 0; k < m; k++)
                    {
                        if (!settled[k])
                        {
                            activeWeight += Math.Max(1, (long)_workEntries[_workIndices[k]].Priority);
                        }
                    }
                    if (activeWeight <= 0) break;

                    for (int k = 0; k < m; k++)
                    {
                        if (settled[k]) continue;
                        var e = _workEntries[_workIndices[k]];
                        long w = Math.Max(1, (long)e.Priority);
                        int extraShare = (int)Math.Min(int.MaxValue, ((long)remainingExtra * w) / activeWeight);
                        int capExtra = Math.Max(0, e.CachedTokens - alloc[k]);
                        if (extraShare >= capExtra)
                        {
                            alloc[k] += capExtra;
                            remainingExtra -= capExtra;
                            settled[k] = true;
                            changed = true;
                        }
                    }
                }

                long finalWeight = 0;
                int activeCount = 0;
                for (int k = 0; k < m; k++)
                {
                    if (!settled[k])
                    {
                        finalWeight += Math.Max(1, (long)_workEntries[_workIndices[k]].Priority);
                        remOrder[activeCount++] = k;
                    }
                }

                if (activeCount > 0 && remainingExtra > 0)
                {
                    int used = 0;
                    for (int i = 0; i < activeCount; i++)
                    {
                        int k = remOrder[i];
                        var e = _workEntries[_workIndices[k]];
                        long w = Math.Max(1, (long)e.Priority);
                        int add = Math.Min(Math.Max(0, e.CachedTokens - alloc[k]), (int)Math.Min(int.MaxValue, ((long)remainingExtra * w) / finalWeight));
                        alloc[k] += add;
                        used += add;
                    }

                    int rem = remainingExtra - used;
                    for (int i = 0; i < activeCount && rem > 0; i++)
                    {
                        int k = remOrder[i];
                        if (alloc[k] < _workEntries[_workIndices[k]].CachedTokens)
                        {
                            alloc[k]++;
                            rem--;
                        }
                    }
                }

                for (int k = 0; k < m; k++)
                {
                    int idx = _workIndices[k];
                    var e = _workEntries[idx];
                    if (alloc[k] < e.CachedTokens)
                    {
                        string compacted = Budget.TruncateToTokens(e.Content, alloc[k]);
                        e.Content = compacted;
                        e.CachedTokens = string.IsNullOrEmpty(compacted) ? 0 : Budget.Estimator.EstimateTokens(compacted);
                        _workEntries[idx] = e;
                        if (string.IsNullOrEmpty(compacted))
                        {
                            alloc[k] = 0;
                        }
                    }
                }

                // Final safety tighten if custom estimator / delimiter interaction still exceeds effectiveBudget
                while (EstimateAssembledExact(newlineLen) > effectiveBudget)
                {
                    int nonEmptyFixed = 0;
                    int maxPos = -1;
                    int maxTok = 1;
                    for (int k = 0; k < m; k++)
                    {
                        if (!string.IsNullOrEmpty(_workEntries[_workIndices[k]].Content))
                        {
                            nonEmptyFixed++;
                        }
                        if (alloc[k] > maxTok)
                        {
                            maxTok = alloc[k];
                            maxPos = k;
                        }
                    }

                    if (nonEmptyFixed <= 1)
                    {
                        for (int k = 0; k < m; k++)
                        {
                            int idx = _workIndices[k];
                            var ce = _workEntries[idx];
                            ce.Content = string.Empty;
                            ce.CachedTokens = 0;
                            _workEntries[idx] = ce;
                        }

                        var restored = _workEntries[bestFixedIdx];
                        restored.Content = bestFixedOriginalContent;
                        restored.CachedTokens = bestFixedOriginalTokens;
                        _workEntries[bestFixedIdx] = restored;
                        ShrinkSingleEntryToFit(bestFixedIdx, effectiveBudget, CompactionStrategy.StrictPrune, newlineLen, newlineTokens);
                        break;
                    }

                    if (maxPos == -1)
                    {
                        // Drop lowest-priority fixed section if even 1 token each still exceeds
                        int dropPos = FindLowestPriorityNonEmptyFixedPos(m);
                        if (dropPos == -1) break;
                        int dropIdx = _workIndices[dropPos];
                        var de = _workEntries[dropIdx];
                        de.Content = string.Empty;
                        de.CachedTokens = 0;
                        _workEntries[dropIdx] = de;
                        alloc[dropPos] = 0;
                        continue;
                    }

                    alloc[maxPos]--;
                    int trimIdx = _workIndices[maxPos];
                    var te = _workEntries[trimIdx];
                    string trimmed = Budget.TruncateToTokens(_sections[te.OriginalIndex].Content ?? string.Empty, alloc[maxPos]);
                    if (string.IsNullOrEmpty(trimmed))
                    {
                        int dropPos = FindLowestPriorityNonEmptyFixedPos(m);
                        if (dropPos != -1 && dropPos != maxPos)
                        {
                            alloc[maxPos]++;
                            int dropIdx = _workIndices[dropPos];
                            var de = _workEntries[dropIdx];
                            de.Content = string.Empty;
                            de.CachedTokens = 0;
                            _workEntries[dropIdx] = de;
                            alloc[dropPos] = 0;
                            continue;
                        }

                        te.Content = string.Empty;
                        te.CachedTokens = 0;
                        _workEntries[trimIdx] = te;
                        alloc[maxPos] = 0;
                        continue;
                    }

                    te.Content = trimmed;
                    te.CachedTokens = Budget.Estimator.EstimateTokens(trimmed);
                    _workEntries[trimIdx] = te;
                }

                bool anyNonEmptyFixed = false;
                for (int k = 0; k < m; k++)
                {
                    if (!string.IsNullOrEmpty(_workEntries[_workIndices[k]].Content))
                    {
                        anyNonEmptyFixed = true;
                        break;
                    }
                }

                if (!anyNonEmptyFixed && effectiveBudget > 0)
                {
                    var restored = _workEntries[bestFixedIdx];
                    restored.Content = bestFixedOriginalContent;
                    restored.CachedTokens = bestFixedOriginalTokens;
                    _workEntries[bestFixedIdx] = restored;
                    ShrinkSingleEntryToFit(bestFixedIdx, effectiveBudget, CompactionStrategy.StrictPrune, newlineLen, newlineTokens);
                }
            }
            finally
            {
                ArrayPool<int>.Shared.Return(alloc);
                ArrayPool<bool>.Shared.Return(settled);
                ArrayPool<int>.Shared.Return(remOrder);
            }
        }

        private int FindLowestPriorityNonEmptyFixedPos(int m)
        {
            int dropPos = -1;
            for (int k = 0; k < m; k++)
            {
                var cand = _workEntries[_workIndices[k]];
                if (string.IsNullOrEmpty(cand.Content)) continue;
                if (dropPos == -1)
                {
                    dropPos = k;
                    continue;
                }
                var cur = _workEntries[_workIndices[dropPos]];
                if (cand.Priority < cur.Priority || (cand.Priority == cur.Priority && cand.OriginalIndex < cur.OriginalIndex))
                {
                    dropPos = k;
                }
            }
            return dropPos;
        }
    }
}
