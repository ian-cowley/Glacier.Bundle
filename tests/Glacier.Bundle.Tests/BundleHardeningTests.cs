using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;
using Xunit;

namespace Glacier.Bundle.Tests
{
    public class BundleHardeningTests
    {
        private sealed class OneCharTokenEstimator : ITokenEstimator
        {
            public int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : text.Length;

            public string TruncateToTokens(string text, int maxTokens)
            {
                if (string.IsNullOrEmpty(text) || maxTokens <= 0) return string.Empty;
                if (text.Length <= maxTokens) return text;
                int end = maxTokens;
                if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
                return end > 0 ? text.Substring(0, end) : string.Empty;
            }
        }

        private sealed class SuperAdditiveTokenEstimator : ITokenEstimator
        {
            private readonly Func<int, int> _costForLength;

            public SuperAdditiveTokenEstimator(Func<int, int> costForLength)
            {
                _costForLength = costForLength;
            }

            public int EstimateTokens(string text) => string.IsNullOrEmpty(text) ? 0 : _costForLength(text.Length);

            public string TruncateToTokens(string text, int maxTokens)
            {
                if (string.IsNullOrEmpty(text) || maxTokens <= 0) return string.Empty;
                if (EstimateTokens(text) <= maxTokens) return text;

                int end = text.Length;
                while (end > 0 && _costForLength(end) > maxTokens)
                {
                    end--;
                }
                if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
                return end > 0 ? text.Substring(0, end) : string.Empty;
            }
        }

        private sealed class FixedChunkBufferWriter : IBufferWriter<char>
        {
            private readonly int _chunkSize;
            private readonly List<char> _written = new();
            private char[] _current = Array.Empty<char>();

            public FixedChunkBufferWriter(int chunkSize)
            {
                _chunkSize = chunkSize;
            }

            public void Advance(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    _written.Add(_current[i]);
                }
            }

            public Memory<char> GetMemory(int sizeHint = 0) => GetSpan(sizeHint).ToArray();

            public Span<char> GetSpan(int sizeHint = 0)
            {
                int size = sizeHint > 0 ? Math.Max(sizeHint, _chunkSize) : _chunkSize;
                if (_current.Length < size)
                {
                    _current = new char[size];
                }
                return _current.AsSpan(0, _chunkSize);
            }

            public override string ToString() => new string(_written.ToArray());
        }

        [Fact]
        public void StrictPrune_SoleOversizedNonFixedSection_TruncatesInsteadOfReturningEmpty()
        {
            var budget = new TokenBudget(totalBudget: 40, defaultStrategy: CompactionStrategy.StrictPrune);
            var builder = new CompactingBundleBuilder(budget);

            string body = "IMPORTANT_SOLE_SECTION_" + new string('A', 600);
            builder.AddSection("Sole", priority: 10, content: body, isFixed: false);

            string result = builder.Build();
            Assert.NotEmpty(result);
            Assert.Contains("IMPORTANT_SOLE_SECTION", result);
            Assert.True(budget.EstimateTokens(result) <= 40, $"Got {budget.EstimateTokens(result)} > 40");
        }

        [Theory]
        [InlineData(CompactionStrategy.StrictPrune)]
        [InlineData(CompactionStrategy.HeadTailTruncate)]
        [InlineData(CompactionStrategy.ProportionalDecay)]
        public void Compaction_FixedHeaderAndFooter_NeverChopsFooterAcrossStrategies(CompactionStrategy strategy)
        {
            var budget = new TokenBudget(totalBudget: 60, defaultStrategy: strategy);
            var builder = new CompactingBundleBuilder(budget);

            builder.AddSection("Header", priority: 1000, content: "[FIXED_HEADER_RULE]\n", isFixed: true);
            builder.AddSection("Body", priority: 10, content: "BODY_START_" + new string('B', 1000) + "_BODY_END", isFixed: false);
            builder.AddSection("Footer", priority: 1000, content: "[FIXED_FOOTER_MANDATORY]", isFixed: true);

            string result = builder.Build();
            int tokens = budget.EstimateTokens(result);

            Assert.Contains("[FIXED_HEADER_RULE]", result);
            Assert.Contains("[FIXED_FOOTER_MANDATORY]", result);
            Assert.True(tokens <= 60, $"Strategy {strategy} exceeded budget: {tokens} > 60");
        }

        [Fact]
        public void Compaction_OversizedFixedHeaderAndFooter_PreservesBothPrefixesWithinBudget()
        {
            var budget = new TokenBudget(totalBudget: 40, defaultStrategy: CompactionStrategy.StrictPrune);
            var builder = new CompactingBundleBuilder(budget);

            builder.AddSection("Header", priority: 100, content: "FIXED_HEADER_PREFIX_" + new string('H', 400), isFixed: true);
            builder.AddSection("Body", priority: 50, content: "OPTIONAL_BODY_DATA", isFixed: false);
            builder.AddSection("Footer", priority: 100, content: "FIXED_FOOTER_PREFIX_" + new string('F', 400), isFixed: true);

            string result = builder.Build();
            int tokens = budget.EstimateTokens(result);

            Assert.Contains("FIXED_HEADER_PREFIX", result);
            Assert.Contains("FIXED_FOOTER_PREFIX", result);
            Assert.DoesNotContain("OPTIONAL_BODY_DATA", result);
            Assert.True(tokens <= 40, $"Exceeded fixed budget: {tokens} > 40");
        }

        [Theory]
        [InlineData(20)] // remainingBudget == 0 after fixed header
        [InlineData(24)] // remainingBudget < nonFixed section count (5)
        public void CompactHeadTail_TightOrZeroRemainingBudget_NeverOvershoots(int totalBudget)
        {
            var budget = new TokenBudget(totalBudget, defaultStrategy: CompactionStrategy.HeadTailTruncate);
            var builder = new CompactingBundleBuilder(budget);

            // ~20 tokens fixed header
            builder.AddSection("Header", priority: 1000, content: new string('S', 76), isFixed: true);
            for (int i = 0; i < 5; i++)
            {
                builder.AddSection($"Sec{i}", priority: i + 1, content: $"S{i}_" + new string((char)('A' + i), 120));
            }

            string result = builder.Build();
            int tokens = budget.EstimateTokens(result);

            Assert.True(tokens <= totalBudget, $"HeadTail overshot budget {totalBudget}: got {tokens}");
        }

        [Fact]
        public void CompactProportionalDecay_RedistributesUnusedSlackAndStrictPruneOverrides()
        {
            var budget = new TokenBudget(totalBudget: 100, defaultStrategy: CompactionStrategy.ProportionalDecay);
            var builder = new CompactingBundleBuilder(budget);

            // High priority short section only needs ~5 tokens, should transfer ~85+ tokens of slack to LongSec
            builder.AddSection("ShortHigh", priority: 90, content: "SHORT_OK\n");
            // StrictPrune override that cannot fit in its share should be dropped and free its budget too
            builder.AddSection("DropMe", priority: 50, content: "DROP_ME_" + new string('D', 800), isFixed: false, strategyOverride: CompactionStrategy.StrictPrune);
            // Low priority long section should absorb slack instead of getting starved to ~10 tokens
            builder.AddSection("LongLow", priority: 10, content: "LONG_" + new string('L', 800));

            string result = builder.Build();
            int lCount = 0;
            foreach (char c in result)
            {
                if (c == 'L') lCount++;
            }

            Assert.Contains("SHORT_OK", result);
            Assert.DoesNotContain("DROP_ME_", result);
            Assert.True(lCount >= 300, $"Expected slack redistribution to give LongLow >= 300 'L' chars (~75+ tokens), got {lCount}");
            Assert.True(budget.EstimateTokens(result) <= 100);
        }

        [Fact]
        public void WaterFilling_StrictPruneOverride_PreservedAfterSlackRedistribution_AcrossInsertionOrders()
        {
            var estimator = new OneCharTokenEstimator();

            // Challenger Probes 1a & 1b (and reverse): all sections '\n'-terminated (0 delimiter overhead)
            // SecA (prio 100, 5 tok) settles first, leaving 95 tokens for SecB (prio 50, 50 tok, StrictPrune)
            // and SecC (prio 1, 200 tok) => SecB's post-slack share = floor(95 * 50 / 51) = 93 >= 50.
            var secA = ("SecA", 100, "AAAA\n", (CompactionStrategy?)null);
            var secB = ("SecB", 50, new string('B', 49) + "\n", (CompactionStrategy?)CompactionStrategy.StrictPrune);
            var secC = ("SecC", 1, new string('C', 199) + "\n", (CompactionStrategy?)null);

            var orders = new[]
            {
                new[] { secA, secB, secC }, // Probe 1a
                new[] { secB, secA, secC }, // Probe 1b
                new[] { secC, secB, secA }
            };

            foreach (var order in orders)
            {
                var budget = new TokenBudget(totalBudget: 100, estimator, CompactionStrategy.ProportionalDecay);
                var builder = new CompactingBundleBuilder(budget);
                foreach (var (name, prio, content, ovr) in order)
                {
                    builder.AddSection(name, prio, content, isFixed: false, strategyOverride: ovr);
                }

                string result = builder.Build();
                Assert.Contains("AAAA", result);
                Assert.Contains(new string('B', 49), result);
                Assert.Contains("CCCC", result);
                Assert.True(estimator.EstimateTokens(result) <= 100, $"Exceeded budget 100: {estimator.EstimateTokens(result)}");
            }

            // Reviewer Probe 2: ShortHigh (prio 100, 10 tok) + StrictSec (prio 10, 30 tok, StrictPrune) + LongMed (prio 10, 100 tok)
            // Post-slack share of StrictSec = floor((100 - 10) * 10 / 20) = 45 >= 30 in both insertion orders.
            int delimTokens = estimator.EstimateTokens(Environment.NewLine);
            int probe2Budget = 100 + 3 * delimTokens;
            var shortHigh = ("ShortHigh", 100, new string('S', 10), (CompactionStrategy?)null);
            var strictSec = ("StrictSec", 10, new string('T', 30), (CompactionStrategy?)CompactionStrategy.StrictPrune);
            var longMed = ("LongMed", 10, new string('M', 100), (CompactionStrategy?)null);

            foreach (var order in new[]
            {
                new[] { shortHigh, strictSec, longMed },
                new[] { strictSec, shortHigh, longMed }
            })
            {
                var budget = new TokenBudget(probe2Budget, estimator, CompactionStrategy.ProportionalDecay);
                var builder = new CompactingBundleBuilder(budget);
                foreach (var (name, prio, content, ovr) in order)
                {
                    builder.AddSection(name, prio, content, isFixed: false, strategyOverride: ovr);
                }

                string result = builder.Build();
                Assert.Contains(new string('S', 10), result);
                Assert.Contains(new string('T', 30), result);
                Assert.True(estimator.EstimateTokens(result) <= probe2Budget);
            }
        }

        [Fact]
        public void WaterFilling_ExactBoundaryShare_DoesNotUseStaleActiveWeight()
        {
            var estimator = new OneCharTokenEstimator();
            int delimTokens = estimator.EstimateTokens(Environment.NewLine);
            int totalBudget = 100 + 3 * delimTokens;

            // Reviewer Probe 3: FitFirst (33 tok) settles on pass 1 (floor(100 * 10 / 30) = 33 >= 33).
            // StrictSecond (33 tok, StrictPrune) must also survive (never penalized by stale activeWeight = 30).
            var budget1 = new TokenBudget(totalBudget, estimator, CompactionStrategy.ProportionalDecay);
            string result1 = new CompactingBundleBuilder(budget1)
                .AddSection("FitFirst", 10, new string('A', 33))
                .AddSection("StrictSecond", 10, new string('B', 33), isFixed: false, strategyOverride: CompactionStrategy.StrictPrune)
                .AddSection("LongThird", 10, new string('C', 50))
                .Build();

            Assert.Contains(new string('A', 33), result1);
            Assert.Contains(new string('B', 33), result1);
            Assert.True(estimator.EstimateTokens(result1) <= totalBudget);

            // Cascading multi-pass boundary: FitFirst (32 tok) settles on pass 1 (share 33 >= 32),
            // leaving 68 across 2 sections => pass 2 share = floor(68 * 10 / 20) = 34 >= 34 for StrictSecond.
            var budget2 = new TokenBudget(totalBudget, estimator, CompactionStrategy.ProportionalDecay);
            string result2 = new CompactingBundleBuilder(budget2)
                .AddSection("FitFirst", 10, new string('A', 32))
                .AddSection("StrictSecond", 10, new string('B', 34), isFixed: false, strategyOverride: CompactionStrategy.StrictPrune)
                .AddSection("LongThird", 10, new string('C', 50))
                .Build();

            Assert.Contains(new string('A', 32), result2);
            Assert.Contains(new string('B', 34), result2);
            Assert.True(estimator.EstimateTokens(result2) <= totalBudget);
        }

        [Theory]
        [InlineData(CompactionStrategy.ProportionalDecay)]
        [InlineData(CompactionStrategy.HeadTailTruncate)]
        public void WaterFilling_MultiSectionNewlineTerminatedTightBudget_NeverWipesOutBundle(CompactionStrategy strategy)
        {
            // Challenger Probe 4c: default HeuristicTokenEstimator(4.0), 4 '\n'-terminated sections at budget=4
            var heur4 = new HeuristicTokenEstimator(4.0);
            var cb4c = new CompactingBundleBuilder(new TokenBudget(4, heur4, strategy));
            for (int i = 0; i < 4; i++)
            {
                cb4c.AddSection($"S{i}", priority: 10, content: new string((char)('A' + i), 39) + "\n");
            }

            string res4c = cb4c.Build();
            Assert.False(string.IsNullOrEmpty(res4c), $"Probe 4c ({strategy}) returned empty string.");
            Assert.True(heur4.EstimateTokens(res4c) <= 4, $"Probe 4c ({strategy}) exceeded budget 4: {heur4.EstimateTokens(res4c)}");

            var abw4c = new ArrayBufferWriter<char>();
            cb4c.WriteTo(abw4c);
            Assert.Equal(res4c, abw4c.WrittenSpan.ToString());

            // Challenger Probe 4b: OneCharTokenEstimator, 2 '\n'-terminated sections at budget=4
            var oneChar = new OneCharTokenEstimator();
            var cb4b = new CompactingBundleBuilder(new TokenBudget(4, oneChar, strategy))
                .AddSection("S0", priority: 100, content: new string('A', 20) + "\n")
                .AddSection("S1", priority: 10, content: new string('B', 20) + "\n");

            string res4b = cb4b.Build();
            Assert.False(string.IsNullOrEmpty(res4b), $"Probe 4b ({strategy}) returned empty string.");
            Assert.Contains("A", res4b);
            Assert.True(oneChar.EstimateTokens(res4b) <= 4, $"Probe 4b ({strategy}) exceeded budget 4: {oneChar.EstimateTokens(res4b)}");

            // Challenger Probe 4d: OneCharTokenEstimator, Sec1(prio=100, '\n'-term) + Sec2(prio=1, no-'\n'), budget=3
            var cb4d = new CompactingBundleBuilder(new TokenBudget(3, oneChar, strategy))
                .AddSection("Sec1", priority: 100, content: new string('X', 20) + "\n")
                .AddSection("Sec2", priority: 1, content: new string('Y', 20));

            string res4d = cb4d.Build();
            Assert.False(string.IsNullOrEmpty(res4d), $"Probe 4d ({strategy}) returned empty string.");
            Assert.Contains("X", res4d);
            Assert.True(oneChar.EstimateTokens(res4d) <= 3, $"Probe 4d ({strategy}) exceeded budget 3: {oneChar.EstimateTokens(res4d)}");
        }

        [Fact]
        public void WaterFilling_Ieee754ReciprocalSensitiveWeights_PreservesExactFitStrictPruneSections()
        {
            var oneChar = new OneCharTokenEstimator();

            // pool = 49, weights = 1 and 48 (1/49 in IEEE-754: 49 * (1/49.0) = 0.9999999999999999)
            string res49 = new CompactingBundleBuilder(new TokenBudget(49, oneChar, CompactionStrategy.ProportionalDecay))
                .AddSection("SecA", priority: 1, content: "\n", isFixed: false, strategyOverride: CompactionStrategy.StrictPrune)
                .AddSection("SecB", priority: 48, content: new string('B', 99) + "\n")
                .Build();

            Assert.StartsWith("\n", res49, StringComparison.Ordinal);
            Assert.True(oneChar.EstimateTokens(res49) <= 49);

            // pool = 98, weights = 1 and 48 (2/49 in IEEE-754: 98 * (1/49.0) = 1.9999999999999998)
            string res98 = new CompactingBundleBuilder(new TokenBudget(98, oneChar, CompactionStrategy.ProportionalDecay))
                .AddSection("SecA", priority: 1, content: "A\n", isFixed: false, strategyOverride: CompactionStrategy.StrictPrune)
                .AddSection("SecB", priority: 48, content: new string('B', 199) + "\n")
                .Build();

            Assert.StartsWith("A\n", res98, StringComparison.Ordinal);
            Assert.True(oneChar.EstimateTokens(res98) <= 98);

            // Fixed sections shrink path with remainingExtra = 49 and weights 1/49:
            // FixedA needs 2 tokens (1 base + 1 extra); FixedB needs 100 tokens. Both '\n'-terminated.
            // totalBudget = 51 => m = 2, remainingExtra = 49, exact extraShare for FixedA = 49 * 1 / 49 = 1.
            string resFixed = new CompactingBundleBuilder(new TokenBudget(51, oneChar, CompactionStrategy.ProportionalDecay))
                .AddSection("FixedA", priority: 1, content: "A\n", isFixed: true)
                .AddSection("FixedB", priority: 48, content: new string('B', 99) + "\n", isFixed: true)
                .AddSection("Body", priority: 100, content: new string('C', 50) + "\n", isFixed: false)
                .Build();

            Assert.StartsWith("A\n", resFixed, StringComparison.Ordinal);
            Assert.True(oneChar.EstimateTokens(resFixed) <= 51);
        }

        [Theory]
        [InlineData(CompactionStrategy.StrictPrune)]
        [InlineData(CompactionStrategy.HeadTailTruncate)]
        [InlineData(CompactionStrategy.ProportionalDecay)]
        public void ShrinkFixedSectionsToFit_TightMultiFixedNewlineTerminated_NeverWipesOutBundle(CompactionStrategy strategy)
        {
            // 1. OneCharEstimator, bud=2: FixA(prio=1, "AA\n") + FixB(prio=2, "BB\n") => "B\n" (2 <= 2)
            var oneChar = new OneCharTokenEstimator();
            var bOneChar = new CompactingBundleBuilder(new TokenBudget(2, oneChar, strategy))
                .AddSection("FixA", 1, "AA\n", isFixed: true)
                .AddSection("FixB", 2, "BB\n", isFixed: true);
            string rOneChar = bOneChar.Build();
            Assert.False(string.IsNullOrEmpty(rOneChar));
            Assert.Contains("B", rOneChar);
            Assert.True(oneChar.EstimateTokens(rOneChar) <= 2, $"OneChar exceeded budget 2: {oneChar.EstimateTokens(rOneChar)}");
            var abwOneChar = new ArrayBufferWriter<char>();
            bOneChar.WriteTo(abwOneChar);
            Assert.Equal(rOneChar, abwOneChar.WrittenSpan.ToString());

            // 2. HeuristicTokenEstimator(1.4), bud=2: FixA(prio=1, "AAAA\n") + FixB(prio=2, "BBBB\n") => "B\n" (ceil(2/1.4)=2 <= 2)
            var heur14 = new HeuristicTokenEstimator(1.4);
            var bHeur14 = new CompactingBundleBuilder(new TokenBudget(2, heur14, strategy))
                .AddSection("FixA", 1, "AAAA\n", isFixed: true)
                .AddSection("FixB", 2, "BBBB\n", isFixed: true);
            string rHeur14 = bHeur14.Build();
            Assert.False(string.IsNullOrEmpty(rHeur14));
            Assert.Contains("B", rHeur14);
            Assert.True(heur14.EstimateTokens(rHeur14) <= 2, $"Heur(1.4) exceeded budget 2: {heur14.EstimateTokens(rHeur14)}");
            var abwHeur14 = new ArrayBufferWriter<char>();
            bHeur14.WriteTo(abwHeur14);
            Assert.Equal(rHeur14, abwHeur14.WrittenSpan.ToString());

            // 3. SuperAdditive estimators: 6 fixed sections at avail=9 & 4 fixed sections at avail=10
            ITokenEstimator[] superEstimators =
            {
                new SuperAdditiveTokenEstimator(len => len == 0 ? 0 : 2 * len + (len > 1 ? len / 2 : 0)),
                new SuperAdditiveTokenEstimator(len => len == 0 ? 0 : len + 1)
            };

            foreach (var est in superEstimators)
            {
                var b6 = new CompactingBundleBuilder(new TokenBudget(9, est, strategy));
                for (int i = 0; i < 6; i++)
                {
                    b6.AddSection($"S{i}", i == 4 ? int.MaxValue : i + 1, new string((char)('A' + i), 12) + "\n", isFixed: true);
                }
                string r6 = b6.Build();
                Assert.False(string.IsNullOrEmpty(r6), $"6-fixed super-additive ({strategy}) wiped out");
                Assert.Contains("E", r6);
                Assert.True(est.EstimateTokens(r6) <= 9, $"6-fixed super-additive exceeded 9: {est.EstimateTokens(r6)}");
                var abw6 = new ArrayBufferWriter<char>();
                b6.WriteTo(abw6);
                Assert.Equal(r6, abw6.WrittenSpan.ToString());

                var b4 = new CompactingBundleBuilder(new TokenBudget(10, est, strategy));
                for (int i = 0; i < 4; i++)
                {
                    b4.AddSection($"S{i}", i == 0 ? int.MaxValue : i + 1, new string((char)('A' + i), 12) + "\n", isFixed: true);
                }
                string r4 = b4.Build();
                Assert.False(string.IsNullOrEmpty(r4), $"4-fixed super-additive ({strategy}) wiped out");
                Assert.Contains("A", r4);
                Assert.True(est.EstimateTokens(r4) <= 10, $"4-fixed super-additive exceeded 10: {est.EstimateTokens(r4)}");
            }

            // 4. Single non-newline-terminated 1-char section at bud=2 with OneCharEstimator
            var bSingle = new CompactingBundleBuilder(new TokenBudget(2, oneChar, strategy))
                .AddSection("FixSingle", 1, "B", isFixed: true);
            string rSingle = bSingle.Build();
            Assert.False(string.IsNullOrEmpty(rSingle));
            Assert.Contains("B", rSingle);
            Assert.True(oneChar.EstimateTokens(rSingle) <= 2);
        }

        [Theory]
        [InlineData(CompactionStrategy.ProportionalDecay)]
        [InlineData(CompactionStrategy.HeadTailTruncate)]
        public void WaterFilling_SoleNonFixedSectionWithStrictPruneOverride_TruncatesInsteadOfReturningEmpty(CompactionStrategy defaultStrategy)
        {
            // Reviewer Probe 4: sole oversized non-fixed section with StrictPrune override and no fixed sections
            // truncates to fit <= budget rather than dropping to empty string.
            var budget = new TokenBudget(totalBudget: 40, defaultStrategy: defaultStrategy);
            var builder = new CompactingBundleBuilder(budget);
            builder.AddSection("SoleStrict", priority: 10, content: "SOLE_STRICT_PREFIX_" + new string('X', 386), isFixed: false, strategyOverride: CompactionStrategy.StrictPrune);

            string result = builder.Build();
            Assert.NotEmpty(result);
            Assert.Contains("SOLE_STRICT_PREFIX_", result);
            Assert.True(budget.EstimateTokens(result) <= 40, $"Got {budget.EstimateTokens(result)} > 40");
        }

        [Fact]
        public void CompactingBundleBuilder_UpfrontDelimiterAccounting_CustomOneCharEstimator_EnforcesBudget()
        {
            var estimator = new OneCharTokenEstimator();
            const int totalBudget = 120;
            var budget = new TokenBudget(totalBudget, estimator, CompactionStrategy.ProportionalDecay);
            var builder = new CompactingBundleBuilder(budget);

            for (int i = 0; i < 25; i++)
            {
                builder.AddSection($"S{i}", priority: i + 1, content: new string((char)('A' + (i % 26)), 12));
            }

            string result = builder.Build();
            Assert.True(estimator.EstimateTokens(result) <= totalBudget,
                $"Custom 1-char/token estimator exceeded budget {totalBudget}: got {estimator.EstimateTokens(result)}");
        }

        [Theory]
        [InlineData(CompactionStrategy.StrictPrune)]
        [InlineData(CompactionStrategy.HeadTailTruncate)]
        [InlineData(CompactionStrategy.ProportionalDecay)]
        public void CompactingBundleBuilder_TwoThousandSections_CompletesFastWithinBudget(CompactionStrategy strategy)
        {
            const int totalBudget = 500;
            var budget = new TokenBudget(totalBudget, defaultStrategy: strategy);
            var builder = new CompactingBundleBuilder(budget);

            builder.AddSection("Header", priority: int.MaxValue, content: "HEADER_PIN\n", isFixed: true);
            for (int i = 0; i < 2000; i++)
            {
                builder.AddSection($"Sec{i}", priority: i % 100, content: $"Row {i}: payload data block\n");
            }

            var sw = Stopwatch.StartNew();
            string result = builder.Build();
            sw.Stop();

            Assert.Contains("HEADER_PIN", result);
            Assert.True(budget.EstimateTokens(result) <= totalBudget);
            Assert.True(sw.ElapsedMilliseconds < 2000, $"2,000 sections under {strategy} took {sw.ElapsedMilliseconds} ms");
        }

        [Theory]
        [InlineData(CompactionStrategy.StrictPrune)]
        [InlineData(CompactionStrategy.HeadTailTruncate)]
        [InlineData(CompactionStrategy.ProportionalDecay)]
        public void WriteTo_MatchesBuildExactly_AndHandlesSmallChunks(CompactionStrategy strategy)
        {
            var budget = new TokenBudget(totalBudget: 80, defaultStrategy: strategy);
            var builder = new CompactingBundleBuilder(budget);

            builder.AddSection("Header", 100, "[HEAD]\n", isFixed: true);
            builder.AddSection("Alpha", 20, "ALPHA " + new string('a', 200));
            builder.AddSection("Beta", 50, "BETA " + new string('b', 200));
            builder.AddSection("Footer", 100, "[TAIL]", isFixed: true);

            string built = builder.Build();

            var arrayWriter = new ArrayBufferWriter<char>();
            builder.WriteTo(arrayWriter);
            Assert.Equal(built, new string(arrayWriter.WrittenSpan));

            var chunkWriter = new FixedChunkBufferWriter(chunkSize: 16);
            builder.WriteTo(chunkWriter);
            Assert.Equal(built, chunkWriter.ToString());
        }

        [Fact]
        public void WriteTo_UncompactedHeuristic_ZeroManagedAllocationsAfterWarmup()
        {
            var budget = new TokenBudget(totalBudget: 500, defaultStrategy: CompactionStrategy.StrictPrune);
            var builder = new CompactingBundleBuilder(budget);
            builder.AddSection("Header", 100, "System Header\n", isFixed: true);
            builder.AddSection("Body", 50, "Payload body content without compaction.\n");

            var writer = new ArrayBufferWriter<char>(256);
            builder.WriteTo(writer); // Warmup internal buffers & writer capacity
            writer.Clear();

            long before = GC.GetAllocatedBytesForCurrentThread();
            builder.WriteTo(writer);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
            Assert.Contains("System Header", new string(writer.WrittenSpan));
        }

        [Fact]
        public void Truncation_PreservesGraphemeClusters_And_HandlesNonIntegerCharsPerToken()
        {
            var fractionalEstimator = new HeuristicTokenEstimator(charsPerToken: 1.7);
            string combining = "Cafe\u0301 na\u00EFve re\u0301sume\u0301";
            string truncCombining = fractionalEstimator.TruncateToTokens(combining, 4);
            Assert.True(fractionalEstimator.EstimateTokens(truncCombining) <= 4);
            AssertValidTextElements(truncCombining);

            // ZWJ family emoji (11 UTF-16 chars) and flag (4 UTF-16 chars) + CRLF
            var oneChar = new HeuristicTokenEstimator(charsPerToken: 1.0);
            string family = "Hi \U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 end";
            string truncFamily = oneChar.TruncateToTokens(family, maxTokens: 8);
            Assert.Equal("Hi ", truncFamily);
            AssertValidTextElements(truncFamily);

            string flag = "US\U0001F1FA\U0001F1F8ok";
            string truncFlag = oneChar.TruncateToTokens(flag, maxTokens: 4);
            Assert.Equal("US", truncFlag);
            AssertValidTextElements(truncFlag);

            string crlf = "ab\r\ncd";
            string truncCrlf = oneChar.TruncateToTokens(crlf, maxTokens: 3);
            Assert.Equal("ab", truncCrlf);

            // TruncateHeadTail with fractional charsPerToken and complex graphemes
            var budget = new TokenBudget(200, new HeuristicTokenEstimator(charsPerToken: 1.8));
            string complex = "Start word " + string.Concat(System.Linq.Enumerable.Repeat("\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 e\u0301 \U0001F1FA\U0001F1F8 ", 15)) + "final words";
            string ht = budget.TruncateHeadTail(complex, maxTokens: 45, headRatio: 0.5);
            Assert.True(budget.EstimateTokens(ht) <= 45);
            AssertValidTextElements(ht);

            // availableContentTokens < 2 fallback
            string tiny = budget.TruncateHeadTail("Alpha Beta Gamma Delta Epsilon Zeta", maxTokens: 15);
            Assert.True(budget.EstimateTokens(tiny) <= 15);
            AssertValidTextElements(tiny);

            // Word-boundary snap on spaced text
            var est4 = new HeuristicTokenEstimator(4.0);
            string spaced = "Hello wonderful world";
            string snapped = est4.TruncateToTokens(spaced, maxTokens: 2); // 8 chars -> "Hello wo" -> snaps to "Hello"
            Assert.Equal("Hello", snapped);
        }

        [Fact]
        public void BundleBuilder_TurkishCultureInvariance_UsesInvariantUppercase()
        {
            string? capturedBundle = null;
            string? capturedCompacting = null;
            Exception? threadEx = null;

            var thread = new Thread(() =>
            {
                try
                {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
                    Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");

                    var ctx = new BundleContext();
                    using var storage = new InMemoryVectorStorage(4);
                    using var vec = new VectorIndex(storage);
                    vec.Add(new float[] { 1f, 0f, 0f, 0f }, "ticket");
                    ctx.RegisterVectorIndex("vec", vec);

                    using var graph = new GraphStore(16, 32);
                    graph.AddEdge("n1", "n2", "REL");
                    ctx.RegisterGraphStore("graph", graph);

                    var doc = new MarkdownTreeParser().Parse("# Root\n## identity policy\nrule text");
                    ctx.RegisterDocTree("doc", doc);

                    capturedBundle = new BundleBuilder(ctx)
                        .BeginBundle("incident_info")
                        .AppendTabularRow("identity_info", sb => sb.AppendLine("ok"))
                        .AppendGraphTopology("graph", "n1", 1, "infrastructure_info")
                        .AppendDocumentTreeSection("doc", "identity policy", "internal_policy")
                        .AppendVectorContext("vec", new float[] { 1f, 0f, 0f, 0f }, 1, "history_info")
                        .Build();

                    capturedCompacting = new CompactingBundleBuilder(ctx, totalBudget: 500)
                        .BeginBundle("incident_info")
                        .AppendTabularRow("row", 10, "identity_info", sb => sb.AppendLine("ok"))
                        .AppendGraphTopology("g", 10, "graph", "n1", 1, "infrastructure_info")
                        .AppendDocumentTreeSection("d", 10, "doc", "identity policy", "internal_policy")
                        .AppendVectorContext("v", 10, "vec", new float[] { 1f, 0f, 0f, 0f }, 1, "history_info")
                        .Build();
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.Start();
            thread.Join();

            Assert.Null(threadEx);
            Assert.NotNull(capturedBundle);
            Assert.NotNull(capturedCompacting);

            foreach (string output in new[] { capturedBundle!, capturedCompacting! })
            {
                Assert.DoesNotContain("İ", output);
                Assert.Contains("INCIDENT_INFO", output);
                Assert.Contains("IDENTITY_INFO", output);
                Assert.Contains("INFRASTRUCTURE_INFO", output);
                Assert.Contains("INTERNAL_POLICY", output);
                Assert.Contains("HISTORY_INFO", output);
            }
        }

        private sealed record ClientProfile(string Id, string Name, string Tier);

        [Fact]
        public void BundleContext_AndBuilders_SupportPolarisDataFrame_HnswVectorIndex_And_CsrGraphStore()
        {
            var ctx = new BundleContext();

            // 1. Polaris DataFrame interop (both RegisterDataFrame and RegisterTabularData<DataFrame>)
            using var df = new DataFrame(new ISeries[]
            {
                Utf8StringSeries.FromStrings("Id", new[] { "C1", "C2", "C3" }),
                Utf8StringSeries.FromStrings("Tier", new[] { "VIP", "Pro", "Free" }),
                new Int32Series("Score", new[] { 98, 75, 42 })
            });
            ctx.RegisterDataFrame("clients_df", df);
            Assert.Same(df, ctx.GetDataFrame("clients_df"));
            Assert.Same(df, ctx.GetTabularData<DataFrame>("clients_df"));
            Assert.True(ctx.TryGetDataFrame("clients_df", out var tryDf) && ReferenceEquals(df, tryDf));

            ctx.RegisterTabularData("clients_via_generic", df);
            Assert.Same(df, ctx.GetDataFrame("clients_via_generic"));

            // Also verify AOT-safe typed tabular model
            ctx.RegisterTabularData("active_user", new ClientProfile("C1", "Alice", "VIP"));
            Assert.Equal("Alice", ctx.GetTabularData<ClientProfile>("active_user").Name);

            // 2. HnswVectorIndex
            using var hnswStorage = new InMemoryVectorStorage(4);
            using var hnsw = new HnswVectorIndex(hnswStorage, m: 8, efConstruction: 32, initialCapacity: 16);
            hnsw.Add(new float[] { 1f, 0f, 0f, 0f }, "HNSW Match: Enterprise Escalation");
            hnsw.Add(new float[] { 0f, 1f, 0f, 0f }, "HNSW Match: Billing Inquiry");
            ctx.RegisterHnswVectorIndex("hnsw_tickets", hnsw);
            Assert.Same(hnsw, ctx.GetHnswVectorIndex("hnsw_tickets"));

            // 3. CsrGraphStore
            string tempDir = Path.Combine(AppContext.BaseDirectory, "glacier_bundle_csr_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string csrPath = Path.Combine(tempDir, "test.gcsr");
            try
            {
                using (var srcGraph = new GraphStore(32, 64))
                {
                    srcGraph.AddEdge("Account_1", "Node_A", "OWNS");
                    srcGraph.AddEdge("Node_A", "Node_B", "LINKS_TO");
                    CsrGraphCompiler.CompileFromForwardStar(srcGraph, csrPath);
                }

                using var csrStore = new CsrGraphStore(csrPath, useMemoryMapping: false);
                ctx.RegisterCsrGraphStore("csr_net", csrStore);
                Assert.Same(csrStore, ctx.GetCsrGraphStore("csr_net"));

                // BundleBuilder integration
                string bundleText = new BundleBuilder(ctx)
                    .BeginBundle("Multi-Engine Integration")
                    .AppendDataFrame("clients_df", maxRows: 2, label: "Polaris Clients")
                    .AppendCsrGraphTopology("csr_net", "Account_1", maxHops: 2, label: "CSR Topology")
                    .AppendHnswVectorContext("hnsw_tickets", new float[] { 1f, 0f, 0f, 0f }, topK: 1, label: "HNSW Matches")
                    .Build();

                Assert.Contains("--- [DATA LAYER] POLARIS CLIENTS ---", bundleText);
                Assert.Contains("C1 | VIP | 98", bundleText);
                Assert.Contains("... (1 more rows omitted)", bundleText);
                Assert.Contains("--- [RELATIONAL LAYER] CSR TOPOLOGY (2 HOPS FROM Account_1) ---", bundleText);
                Assert.Contains("Node_A", bundleText);
                Assert.Contains("Node_B", bundleText);
                Assert.Contains("--- [SEMANTIC LAYER] HNSW MATCHES ---", bundleText);
                Assert.Contains("HNSW Match: Enterprise Escalation", bundleText);

                // CompactingBundleBuilder integration + WriteTo
                var compactBuilder = new CompactingBundleBuilder(ctx, totalBudget: 300, CompactionStrategy.ProportionalDecay)
                    .BeginBundle("Compacted Multi-Engine")
                    .AppendDataFrame("df", priority: 90, "clients_df", maxRows: 3)
                    .AppendCsrGraphTopology("csr", priority: 80, "csr_net", "Account_1", maxHops: 2)
                    .AppendHnswVectorContext("hnsw", priority: 70, "hnsw_tickets", new float[] { 1f, 0f, 0f, 0f }, topK: 1);

                var abw = new ArrayBufferWriter<char>();
                compactBuilder.WriteTo(abw);
                string compactText = abw.WrittenSpan.ToString();

                Assert.Equal(compactBuilder.Build(), compactText);
                Assert.Contains("C3 | Free | 42", compactText);
                Assert.Contains("Node_B", compactText);
                Assert.Contains("HNSW Match: Enterprise Escalation", compactText);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }

        private static void AssertValidTextElements(string value)
        {
            var enumerator = StringInfo.GetTextElementEnumerator(value);
            int reconstructedLen = 0;
            while (enumerator.MoveNext())
            {
                string elem = enumerator.GetTextElement();
                Assert.False(elem.Length == 1 && char.IsSurrogate(elem[0]),
                    $"Found lone surrogate U+{(int)elem[0]:X4} in truncated string.");
                reconstructedLen += elem.Length;
            }
            Assert.Equal(value.Length, reconstructedLen);
        }
    }
}
