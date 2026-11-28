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

        [Fact]
        public void Truncation_WithSurrogatePairs_NeverSplitsUtf16CodePoint()
        {
            var estimator = new HeuristicTokenEstimator(charsPerToken: 1.0);
            // Surrogate pair '🚀' (2 UTF-16 chars each) at boundary index 3..4 and in tail
            string text = "abc🚀xyz";
            string truncated = estimator.TruncateToTokens(text, maxTokens: 4); // targetChars = 4 splits '🚀'
            Assert.Equal("abc", truncated);
            Assert.False(char.IsHighSurrogate(truncated[^1]));

            var budget = new TokenBudget(100, new HeuristicTokenEstimator(1.0));
            string longEmoji = new string('a', 40) + string.Concat(System.Linq.Enumerable.Repeat("🚀", 25));
            string headTail = budget.TruncateHeadTail(longEmoji, maxTokens: 50, headRatio: 0.5);
            foreach (int idx in System.Linq.Enumerable.Range(0, headTail.Length))
            {
                if (char.IsHighSurrogate(headTail[idx]))
                    Assert.True(idx + 1 < headTail.Length && char.IsLowSurrogate(headTail[idx + 1]));
                if (char.IsLowSurrogate(headTail[idx]))
                    Assert.True(idx - 1 >= 0 && char.IsHighSurrogate(headTail[idx - 1]));
            }
        }

        [Fact]
        public void TokenBudget_ConcurrentReserve_NeverExceedsTotalBudget()
        {
            var budget = new TokenBudget(100);
            int succeeded = 0;
            System.Threading.Tasks.Parallel.For(0, 200, _ =>
            {
                if (budget.Reserve(10))
                {
                    System.Threading.Interlocked.Increment(ref succeeded);
                }
            });

            Assert.Equal(10, succeeded);
            Assert.Equal(100, budget.ReservedTokens);
            Assert.Equal(0, budget.AvailableBudget);
        }

        [Fact]
        public void Reserve_SignedInt32Overflow_ReturnsFalseAndPreservesReservedTokens()
        {
            var smallBudget = new TokenBudget(100);
            Assert.True(smallBudget.Reserve(1));
            Assert.False(smallBudget.Reserve(int.MaxValue));
            Assert.Equal(1, smallBudget.ReservedTokens);
            Assert.Equal(99, smallBudget.AvailableBudget);

            var maxBudget = new TokenBudget(int.MaxValue);
            Assert.True(maxBudget.Reserve(int.MaxValue - 5));
            Assert.False(maxBudget.Reserve(10));
            Assert.Equal(int.MaxValue - 5, maxBudget.ReservedTokens);
            Assert.Equal(5, maxBudget.AvailableBudget);

            Assert.True(maxBudget.Reserve(5));
            Assert.Equal(int.MaxValue, maxBudget.ReservedTokens);
            Assert.Equal(0, maxBudget.AvailableBudget);
        }
    }
}
