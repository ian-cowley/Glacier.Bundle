using System;

namespace Glacier.Bundle.Budgeting
{
    /// <summary>
    /// Fast, zero-dependency token estimator based on character length heuristics (~4 characters per token).
    /// </summary>
    public class HeuristicTokenEstimator : ITokenEstimator
    {
        public double CharsPerToken { get; }

        public HeuristicTokenEstimator(double charsPerToken = 4.0)
        {
            if (charsPerToken <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(charsPerToken), "Chars per token must be greater than zero.");
            }
            CharsPerToken = charsPerToken;
        }

        public int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return Math.Max(1, (int)Math.Ceiling(text.Length / CharsPerToken));
        }

        public string TruncateToTokens(string text, int maxTokens)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            {
                return string.Empty;
            }

            if (EstimateTokens(text) <= maxTokens)
            {
                return text;
            }

            int targetChars = (int)(maxTokens * CharsPerToken);
            if (targetChars >= text.Length)
            {
                return text;
            }

            return text.Substring(0, targetChars);
        }
    }
}
