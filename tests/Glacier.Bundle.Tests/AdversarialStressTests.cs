namespace Glacier.Bundle.Tests;

using System;
using System.Text;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;
using Xunit;

public class AdversarialStressTests
{
    [Theory]
    [InlineData(CompactionStrategy.StrictPrune)]
    [InlineData(CompactionStrategy.HeadTailTruncate)]
    [InlineData(CompactionStrategy.ProportionalDecay)]
    public void CompactingBundleBuilder_AdversarialOversizedPayload_EnforcesBudgetStrictly(CompactionStrategy strategy)
    {
        const int totalBudget = 250; // ~1000 chars
        var budget = new TokenBudget(totalBudget, defaultStrategy: strategy);
        var builder = new CompactingBundleBuilder(budget);

        builder.AddSection("Header", 1000, "SYSTEM: Critical Rules\n", isFixed: true);

        // Add massive oversized payload: 40 sections, ~40,000 chars (~10,000 tokens)
        for (int i = 0; i < 40; i++)
        {
            int priority = (i * 17) % 50;
            string content = $"Section {i} Data: " + new string((char)('A' + (i % 26)), 800) + "\n";
            builder.AddSection($"Sec_{i}", priority, content);
        }

        string result = builder.Build();

        Assert.NotNull(result);
        Assert.NotEmpty(result);

        // Header must be preserved
        Assert.Contains("SYSTEM: Critical Rules", result);

        // Calculate total tokens of the assembled bundle
        int assembledTokens = budget.EstimateTokens(result);

        // Strict assertion: assembled tokens must not exceed budget (<= 250)
        Assert.True(assembledTokens <= totalBudget,
            $"Assembled tokens ({assembledTokens}) exceeded total budget ({totalBudget}) under {strategy}. Result length: {result.Length}");
    }

    [Theory]
    [InlineData(CompactionStrategy.StrictPrune)]
    [InlineData(CompactionStrategy.HeadTailTruncate)]
    [InlineData(CompactionStrategy.ProportionalDecay)]
    public void CompactingBundleBuilder_MassiveSingleSection_EnforcesBudget(CompactionStrategy strategy)
    {
        const int totalBudget = 100;
        var budget = new TokenBudget(totalBudget, defaultStrategy: strategy);
        var builder = new CompactingBundleBuilder(budget);

        // Single 10,000 char section (~2,500 tokens)
        string giantContent = "START_OF_MASSIVE_SECTION " + new string('X', 10_000) + " END_OF_MASSIVE_SECTION\n";
        builder.AddSection("GiantPayload", 10, giantContent);

        string result = builder.Build();
        int resultTokens = budget.EstimateTokens(result);

        Assert.True(resultTokens <= totalBudget + 2,
            $"Tokens ({resultTokens}) exceeded total budget ({totalBudget}) under {strategy}");
        if (strategy != CompactionStrategy.StrictPrune)
        {
            Assert.NotEmpty(result);
        }
    }

    [Fact]
    public void CompactingBundleBuilder_FixedSectionsExceedBudget_TruncatesGracefully()
    {
        const int totalBudget = 50;
        var budget = new TokenBudget(totalBudget, defaultStrategy: CompactionStrategy.StrictPrune);
        var builder = new CompactingBundleBuilder(budget);

        // Fixed section by itself has 800 chars (~200 tokens), exceeding total budget of 50
        string oversizedFixed = "FIXED_POLICY: " + new string('F', 800);
        builder.AddSection("FixedRules", 100, oversizedFixed, isFixed: true);
        builder.AddSection("OptionalData", 1, "Some optional data");

        string result = builder.Build();
        int resultTokens = budget.EstimateTokens(result);

        Assert.True(resultTokens <= totalBudget + 2,
            $"Fixed section compaction failed to limit tokens ({resultTokens} > {totalBudget})");
        Assert.Contains("FIXED_POLICY", result);
        Assert.DoesNotContain("Some optional data", result);
    }

    [Theory]
    [InlineData(CompactionStrategy.StrictPrune)]
    [InlineData(CompactionStrategy.HeadTailTruncate)]
    [InlineData(CompactionStrategy.ProportionalDecay)]
    public void CompactingBundleBuilder_AdversarialSpecialCharactersAndUnicode_NoExceptions(CompactionStrategy strategy)
    {
        const int totalBudget = 120;
        var budget = new TokenBudget(totalBudget, defaultStrategy: strategy);
        var builder = new CompactingBundleBuilder(budget);

        // Edge-case strings: empty, whitespace, surrogate pairs, Chinese, Arabic, emojis
        builder.AddSection("Empty", 1, "");
        builder.AddSection("Whitespace", 2, "   \t\r\n   ");
        builder.AddSection("Surrogates", 3, "\U0001F600\U0001F680\U0001F525\u26A1".PadRight(500, 'X'));
        builder.AddSection("CJK", 4, "这是一段中文测试数据，包含大量的汉字以及全角标点符号。".PadRight(600, '中'));
        builder.AddSection("Arabic", 5, "هذا نص تجريبي باللغة العربية لاختبار الضغط".PadRight(400, 'ع'));

        string result = builder.Build();
        int tokens = budget.EstimateTokens(result);

        Assert.True(tokens <= totalBudget + 2);
    }
}
