using System;
using System.Globalization;

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
            if (charsPerToken <= 0 || double.IsNaN(charsPerToken) || double.IsInfinity(charsPerToken))
            {
                throw new ArgumentOutOfRangeException(nameof(charsPerToken), "Chars per token must be a finite number greater than zero.");
            }
            CharsPerToken = charsPerToken;
        }

        public int EstimateTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            return EstimateTokensForLength(text.Length);
        }

        internal int EstimateTokensForLength(int charLength)
        {
            if (charLength <= 0)
            {
                return 0;
            }

            return Math.Max(1, (int)Math.Ceiling(charLength / CharsPerToken));
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

            int targetChars = (int)Math.Floor(maxTokens * CharsPerToken);
            if (targetChars >= text.Length)
            {
                targetChars = text.Length;
            }

            // Guard against IEEE-754 rounding overshoot where targetChars / CharsPerToken > maxTokens
            while (targetChars > 0 && EstimateTokensForLength(targetChars) > maxTokens)
            {
                targetChars--;
            }

            if (targetChars <= 0)
            {
                return string.Empty;
            }

            targetChars = SnapEndToGraphemeBoundary(text, targetChars);
            targetChars = SnapEndToWordBoundary(text, targetChars);

            return targetChars > 0 ? text.Substring(0, targetChars) : string.Empty;
        }

        internal static int SnapEndToGraphemeBoundary(string text, int endIndex)
        {
            if (endIndex <= 0) return 0;
            if (endIndex >= text.Length) return text.Length;

            // Fast path: two consecutive ASCII chars (excluding CRLF) always form a valid UAX #29 boundary
            if (text[endIndex - 1] < 0x80 && text[endIndex] < 0x80 &&
                !(text[endIndex - 1] == '\r' && text[endIndex] == '\n'))
            {
                return endIndex;
            }

            int anchor = 0;
            for (int i = endIndex - 1; i >= 1; i--)
            {
                char curr = text[i];
                char prev = text[i - 1];
                if (curr < 0x80 && curr != '\r' && curr != '\n' &&
                    prev < 0x80 && prev != '\r')
                {
                    anchor = i;
                    break;
                }
            }

            int pos = anchor;
            while (pos < endIndex)
            {
                int len = StringInfo.GetNextTextElementLength(text, pos);
                if (len <= 0 || pos + len > endIndex)
                {
                    break;
                }
                pos += len;
            }

            return pos;
        }

        internal static int SnapStartToGraphemeBoundary(string text, int startIndex)
        {
            if (startIndex <= 0) return 0;
            if (startIndex >= text.Length) return text.Length;

            if (text[startIndex - 1] < 0x80 && text[startIndex] < 0x80 &&
                !(text[startIndex - 1] == '\r' && text[startIndex] == '\n'))
            {
                return startIndex;
            }

            int anchor = 0;
            for (int i = startIndex - 1; i >= 1; i--)
            {
                char curr = text[i];
                char prev = text[i - 1];
                if (curr < 0x80 && curr != '\r' && curr != '\n' &&
                    prev < 0x80 && prev != '\r')
                {
                    anchor = i;
                    break;
                }
            }

            int pos = anchor;
            while (pos < startIndex)
            {
                int len = StringInfo.GetNextTextElementLength(text, pos);
                if (len <= 0)
                {
                    return startIndex;
                }
                pos += len;
            }

            return Math.Min(pos, text.Length);
        }

        internal static int SnapEndToWordBoundary(string text, int endIndex)
        {
            if (endIndex <= 0 || endIndex >= text.Length)
            {
                return endIndex;
            }

            // Already cleanly separated by whitespace
            if (char.IsWhiteSpace(text[endIndex - 1]) || char.IsWhiteSpace(text[endIndex]))
            {
                return endIndex;
            }

            int minKeep = Math.Max(1, (int)(endIndex * 0.7));
            for (int i = endIndex - 1; i >= minKeep; i--)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    int trimmed = i;
                    while (trimmed > 0 && char.IsWhiteSpace(text[trimmed - 1]))
                    {
                        trimmed--;
                    }
                    if (trimmed > 0)
                    {
                        return SnapEndToGraphemeBoundary(text, trimmed);
                    }
                    break;
                }
            }

            return endIndex;
        }
    }
}
