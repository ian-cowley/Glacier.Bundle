using System;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;
using Xunit;

namespace Glacier.Bundle.Tests
{
    public class CompactionTests
    {
        [Fact]
        public void StrictPrune_DropsLowestPrioritySectionFirst()
        {
            var budget = new TokenBudget(totalBudget: 60, defaultStrategy: CompactionStrategy.StrictPrune);
            var builder = new CompactingBundleBuilder(budget);

            // Each string is 100 characters => 25 tokens
            string content1 = "LowPriority: " + new string('A', 87);
            string content2 = "MedPriority: " + new string('B', 87);
            string content3 = "HighPriority: " + new string('C', 86);

            builder.AddSection("LowSec", 10, content1);
            builder.AddSection("MedSec", 50, content2);
            builder.AddSection("HighSec", 100, content3);

            // Total tokens = 75 > 60
            string result = builder.Build();

            // LowSec should be pruned; MedSec and HighSec should remain
            Assert.DoesNotContain("LowPriority", result);
            Assert.Contains("MedPriority", result);
            Assert.Contains("HighPriority", result);
        }

        [Fact]
        public void StrictPrune_PreservesFixedSectionsEvenWithLowerPriority()
        {
            var budget = new TokenBudget(totalBudget: 35, defaultStrategy: CompactionStrategy.StrictPrune);
            var builder = new CompactingBundleBuilder(budget);

            string fixedHeader = "SYSTEM FIXED HEADER: " + new string('H', 20); // ~11 tokens
            string normalSection = "NORMAL BODY SECTION: " + new string('N', 120); // ~35 tokens

            builder.AddSection("Header", 1, fixedHeader, isFixed: true);
            builder.AddSection("Body", 100, normalSection, isFixed: false);

            string result = builder.Build();

            // Fixed header must survive despite having lower priority than Body
            Assert.Contains("SYSTEM FIXED HEADER", result);
            Assert.DoesNotContain("NORMAL BODY SECTION", result);
        }

        [Fact]
        public void HeadTailTruncate_PreservesStartAndEndPortions()
        {
            var budget = new TokenBudget(totalBudget: 50, defaultStrategy: CompactionStrategy.HeadTailTruncate);
            var builder = new CompactingBundleBuilder(budget);

            // Long section with distinct start and end
            string longBody = "BEGINNING_OF_POLICY_" + new string('X', 400) + "_END_OF_POLICY";
            builder.AddSection("Policy", 100, longBody);

            string result = builder.Build();

            Assert.Contains("BEGINNING_OF_POLICY", result);
            Assert.Contains("END_OF_POLICY", result);
            Assert.Contains("[TRUNCATED", result);

            int finalTokens = budget.EstimateTokens(result);
            Assert.True(finalTokens <= 50, $"Expected <= 50 tokens, but got {finalTokens}");
        }

        [Fact]
        public void ProportionalDecay_AllocatesTokensAccordingToPriorityWeights()
        {
            var budget = new TokenBudget(totalBudget: 50, defaultStrategy: CompactionStrategy.ProportionalDecay);
            var builder = new CompactingBundleBuilder(budget);

            // Two sections of equal length (200 chars each = 50 tokens each, total 100 tokens > 50)
            string sec1Content = "SEC1_" + new string('1', 195);
            string sec2Content = "SEC2_" + new string('2', 195);

            // Priority 10 vs 30 => sec2 has 3x weight
            builder.AddSection("Sec1", 10, sec1Content);
            builder.AddSection("Sec2", 30, sec2Content);

            string result = builder.Build();

            // Both sections should exist in output
            Assert.Contains("SEC1_", result);
            Assert.Contains("SEC2_", result);

            // Count occurrences of '1' and '2' to verify Sec2 received greater allocation
            int count1 = 0;
            int count2 = 0;
            foreach (char c in result)
            {
                if (c == '1') count1++;
                else if (c == '2') count2++;
            }

            Assert.True(count2 > count1, $"Expected count2 ({count2}) > count1 ({count1}) due to higher priority");
            int finalTokens = budget.EstimateTokens(result);
            Assert.True(finalTokens <= 50, $"Expected <= 50 tokens, but got {finalTokens}");
        }
    }
}
