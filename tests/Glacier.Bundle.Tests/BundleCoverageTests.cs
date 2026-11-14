using System;
using System.Buffers;
using System.Collections.Generic;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;
using Xunit;

namespace Glacier.Bundle.Tests
{
    public class BundleCoverageTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1.5)]
        public void HeuristicTokenEstimator_InvalidCharsPerToken_Throws(double charsPerToken)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new HeuristicTokenEstimator(charsPerToken));
        }

        [Fact]
        public void HeuristicTokenEstimator_TruncateToTokens_EdgeCases()
        {
            var estimator = new HeuristicTokenEstimator(4.0);

            Assert.Equal(string.Empty, estimator.TruncateToTokens(string.Empty, 10));
            Assert.Equal(string.Empty, estimator.TruncateToTokens(null!, 10));
            Assert.Equal(string.Empty, estimator.TruncateToTokens("hello world", 0));
            Assert.Equal(string.Empty, estimator.TruncateToTokens("hello world", -5));
            Assert.Equal("hello", estimator.TruncateToTokens("hello", 10));
        }

        [Fact]
        public void TokenBudget_ReserveAndRelease_NegativeAndOverRelease_EdgeCases()
        {
            var budget = new TokenBudget(50);

            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Reserve(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => budget.Release(-1));

            Assert.True(budget.Reserve(20));
            budget.Release(100);
            Assert.Equal(0, budget.ReservedTokens);
            Assert.Equal(50, budget.AvailableBudget);
        }

        [Fact]
        public void TokenBudget_TruncateHeadTail_EdgeCases()
        {
            var budget = new TokenBudget(100);

            Assert.Equal(string.Empty, budget.TruncateHeadTail(string.Empty, 10));
            Assert.Equal(string.Empty, budget.TruncateHeadTail("abcdef", 0));
            Assert.Equal("short", budget.TruncateHeadTail("short", 10));

            // Budget too small for marker -> falls back to TruncateToTokens
            string longText = new string('X', 100);
            string tiny = budget.TruncateHeadTail(longText, 2);
            Assert.Equal(8, tiny.Length);
        }

        [Fact]
        public void BundleSection_ConstructorsAndEstimateTokens()
        {
            var empty = new BundleSection();
            Assert.Equal(string.Empty, empty.Name);
            Assert.Equal(string.Empty, empty.Content);
            Assert.False(empty.IsFixed);

            var section = new BundleSection(null!, 5, null!, isFixed: true, strategyOverride: CompactionStrategy.HeadTailTruncate);
            Assert.Equal(string.Empty, section.Name);
            Assert.Equal(string.Empty, section.Content);
            Assert.True(section.IsFixed);
            Assert.Equal(CompactionStrategy.HeadTailTruncate, section.StrategyOverride);

            Assert.Throws<ArgumentNullException>(() => section.EstimateTokens(null!));

            section.Content = "12345678";
            Assert.Equal(2, section.EstimateTokens(new HeuristicTokenEstimator(4.0)));
        }

        [Fact]
        public void BundleContext_GetTabularData_TypeMismatch_ThrowsKeyNotFound()
        {
            var context = new BundleContext();
            context.RegisterTabularData("table", new List<string> { "a" });

            Assert.Throws<KeyNotFoundException>(() => context.GetTabularData<Dictionary<string, string>>("table"));
        }

        [Fact]
        public void BundleBuilder_NullContext_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new BundleBuilder(null!));
        }

        [Fact]
        public void CompactingBundleBuilder_NullGuardsAndWriteTo_WorkAsExpected()
        {
            Assert.Throws<ArgumentNullException>(() => new CompactingBundleBuilder((TokenBudget)null!));

            var builder = new CompactingBundleBuilder(new TokenBudget(100));
            Assert.Throws<ArgumentNullException>(() => builder.WriteTo(null!));
            Assert.Throws<InvalidOperationException>(() => builder.AppendGraphTopology("g", 1, "graph", "n1", 1));
            Assert.Throws<InvalidOperationException>(() => builder.AppendDocumentTreeSection("d", 1, "doc", "sec"));
            Assert.Throws<InvalidOperationException>(() => builder.AppendVectorContext("v", 1, "vec", new float[] { 1f }, 1));

            builder.AddSection("System", 10, "Hello World", isFixed: true);
            var writer = new ArrayBufferWriter<char>();
            builder.WriteTo(writer);

            string output = new string(writer.WrittenSpan);
            Assert.Contains("Hello World", output);
        }
    }
}
