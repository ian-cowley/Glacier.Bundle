using System;

namespace Glacier.Bundle.Budgeting
{
    /// <summary>
    /// Manages LLM token constraints, reservations, and budgeting policies.
    /// </summary>
    public class TokenBudget
    {
        private readonly object _budgetLock = new();
        private int _reservedTokens;

        public int TotalBudget { get; }

        public int ReservedTokens
        {
            get
            {
                lock (_budgetLock)
                {
                    return _reservedTokens;
                }
            }
        }

        public ITokenEstimator Estimator { get; }
        public CompactionStrategy DefaultStrategy { get; set; }

        public int AvailableBudget
        {
            get
            {
                lock (_budgetLock)
                {
                    return (int)Math.Max(0L, (long)TotalBudget - _reservedTokens);
                }
            }
        }

        public TokenBudget(
            int totalBudget,
            ITokenEstimator? estimator = null,
            CompactionStrategy defaultStrategy = CompactionStrategy.StrictPrune)
        {
            if (totalBudget <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(totalBudget), "Total token budget must be greater than zero.");
            }

            TotalBudget = totalBudget;
            Estimator = estimator ?? new HeuristicTokenEstimator();
            DefaultStrategy = defaultStrategy;
        }

        public bool Reserve(int tokens)
        {
            if (tokens < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tokens), "Reserved tokens cannot be negative.");
            }

            lock (_budgetLock)
            {
                if ((long)_reservedTokens + tokens > TotalBudget)
                {
                    return false;
                }

                _reservedTokens += tokens;
                return true;
            }
        }

        public void Release(int tokens)
        {
            if (tokens < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(tokens), "Released tokens cannot be negative.");
            }

            lock (_budgetLock)
            {
                _reservedTokens = Math.Max(0, _reservedTokens - tokens);
            }
        }

        public void ResetReservation()
        {
            lock (_budgetLock)
            {
                _reservedTokens = 0;
            }
        }

        public int EstimateTokens(string text) => Estimator.EstimateTokens(text);

        public string TruncateToTokens(string text, int maxTokens) => Estimator.TruncateToTokens(text, maxTokens);

        /// <summary>
        /// Performs Head-Tail truncation preserving the beginning and ending portions with a truncation marker in the center.
        /// </summary>
        public string TruncateHeadTail(string text, int maxTokens, double headRatio = 0.6)
        {
            if (string.IsNullOrEmpty(text) || maxTokens <= 0)
            {
                return string.Empty;
            }

            int currentTokens = Estimator.EstimateTokens(text);
            if (currentTokens <= maxTokens)
            {
                return text;
            }

            headRatio = Math.Clamp(headRatio, 0.1, 0.9);
            const string marker = "\n... [TRUNCATED] ...\n";
            int markerTokens = Estimator.EstimateTokens(marker);
            int availableContentTokens = maxTokens - markerTokens;

            if (availableContentTokens < 2)
            {
                return Estimator.TruncateToTokens(text, maxTokens);
            }

            int headTokens = Math.Clamp((int)(availableContentTokens * headRatio), 1, availableContentTokens - 1);
            int tailTokens = Math.Max(1, availableContentTokens - headTokens);

            string head = Estimator.TruncateToTokens(text, headTokens);
            int safeHeadLen = HeuristicTokenEstimator.SnapEndToGraphemeBoundary(text, Math.Min(head.Length, text.Length));
            if (safeHeadLen == head.Length)
            {
                safeHeadLen = HeuristicTokenEstimator.SnapEndToWordBoundary(text, safeHeadLen);
            }
            if (safeHeadLen != head.Length)
            {
                head = text.Substring(0, safeHeadLen);
            }

            // Keep floating-point charsPerToken so tail capacity is not lost to integer truncation
            double charsPerToken = Math.Max(1.0, text.Length / (double)currentTokens);
            int tailCharEstimate = Math.Max(1, (int)Math.Floor(tailTokens * charsPerToken));
            int tailStartIndex = Math.Max(head.Length, text.Length - tailCharEstimate);
            tailStartIndex = HeuristicTokenEstimator.SnapStartToGraphemeBoundary(text, tailStartIndex);

            // Snap tailStartIndex forward to a word boundary within a bounded window if it landed mid-word
            if (tailStartIndex > head.Length &&
                tailStartIndex < text.Length &&
                !char.IsWhiteSpace(text[tailStartIndex - 1]) &&
                !char.IsWhiteSpace(text[tailStartIndex]))
            {
                int remainingTailChars = text.Length - tailStartIndex;
                int maxAdvance = Math.Min(text.Length, tailStartIndex + Math.Max(1, (int)(remainingTailChars * 0.3)));
                for (int i = tailStartIndex; i < maxAdvance; i++)
                {
                    if (char.IsWhiteSpace(text[i]))
                    {
                        int nextWord = i + 1;
                        while (nextWord < text.Length && char.IsWhiteSpace(text[nextWord]))
                        {
                            nextWord++;
                        }
                        if (nextWord < text.Length)
                        {
                            tailStartIndex = HeuristicTokenEstimator.SnapStartToGraphemeBoundary(text, nextWord);
                        }
                        break;
                    }
                }
            }

            string tail = tailStartIndex < text.Length ? text.Substring(tailStartIndex) : string.Empty;

            int omittedChars = Math.Max(0, tailStartIndex - head.Length);
            string detailedMarker = $"\n... [TRUNCATED {omittedChars} CHARACTERS] ...\n";

            string result = head + detailedMarker + tail;
            if (Estimator.EstimateTokens(result) <= maxTokens)
            {
                return result;
            }

            result = head + marker + tail;
            if (Estimator.EstimateTokens(result) <= maxTokens)
            {
                return result;
            }

            return Estimator.TruncateToTokens(text, maxTokens);
        }
    }
}
