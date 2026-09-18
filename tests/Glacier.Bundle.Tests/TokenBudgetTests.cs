using System;
using Glacier.Bundle.Budgeting;
using Xunit;

namespace Glacier.Bundle.Tests
{
    public class TokenBudgetTests
    {
        [Fact]
        public void Constructor_InvalidBudget_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudget(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudget(-100));
        }

        [Fact]
        public void HeuristicEstimator_EstimatesApproximatelyFourCharsPerToken()
        {
            var estimator = new HeuristicTokenEstimator(4.0);

            Assert.Equal(0, estimator.EstimateTokens(""));
            Assert.Equal(0, estimator.EstimateTokens(null!));
            Assert.Equal(1, estimator.EstimateTokens("abcd"));
            Assert.Equal(2, estimator.EstimateTokens("abcdefgh"));
            Assert.Equal(3, estimator.EstimateTokens("abcdefghi")); // 9 chars -> ceil(9/4) = 3
        }

        [Fact]
        public void ReserveAndRelease_TracksBudgetCorrectly()
        {
            var budget = new TokenBudget(100);
            Assert.Equal(100, budget.AvailableBudget);
            Assert.Equal(0, budget.ReservedTokens);

            bool reserved = budget.Reserve(40);
            Assert.True(reserved);
            Assert.Equal(40, budget.ReservedTokens);
            Assert.Equal(60, budget.AvailableBudget);

            // Attempt to reserve more than available
            bool overReserve = budget.Reserve(70);
            Assert.False(overReserve);
            Assert.Equal(40, budget.ReservedTokens); // Unchanged

            // Release
            budget.Release(15);
            Assert.Equal(25, budget.ReservedTokens);
            Assert.Equal(75, budget.AvailableBudget);

            // Reset
            budget.ResetReservation();
            Assert.Equal(0, budget.ReservedTokens);
            Assert.Equal(100, budget.AvailableBudget);
        }

        [Fact]
        public void TruncateToTokens_LimitsContent()
        {
            var budget = new TokenBudget(100);
            string longText = "1234567890123456789012345678901234567890"; // 40 chars -> ~10 tokens

            string truncated = budget.TruncateToTokens(longText, 5); // 5 tokens -> 20 chars
            Assert.Equal(20, truncated.Length);
            Assert.Equal("12345678901234567890", truncated);
        }

        [Fact]
        public void TruncateHeadTail_PreservesStartAndEndWithMarker()
        {
            var budget = new TokenBudget(100);
            // 400 characters string
            string text = new string('A', 200) + new string('Z', 200);

            // Truncate to 30 tokens (~120 chars)
            string result = budget.TruncateHeadTail(text, 30, headRatio: 0.5);

            Assert.StartsWith("AAAA", result);
            Assert.EndsWith("ZZZZ", result);
            Assert.Contains("[TRUNCATED", result);
        }
    }
}
