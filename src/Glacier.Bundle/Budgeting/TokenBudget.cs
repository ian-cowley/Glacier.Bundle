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
                    return Math.Max(0, TotalBudget - _reservedTokens);
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
                if (_reservedTokens + tokens > TotalBudget)
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
            string marker = "\n... [TRUNCATED] ...\n";
            int markerTokens = Estimator.EstimateTokens(marker);
            int availableContentTokens = maxTokens - markerTokens;

            if (availableContentTokens <= 0)
            {
                return Estimator.TruncateToTokens(text, maxTokens);
            }

            int headTokens = Math.Max(1, (int)(availableContentTokens * headRatio));
            int tailTokens = Math.Max(1, availableContentTokens - headTokens);

            string head = Estimator.TruncateToTokens(text, headTokens);

            // For tail, take from the end of the text
            int charsPerToken = (int)Math.Max(1, (text.Length / (double)currentTokens));
            int tailCharEstimate = tailTokens * charsPerToken;
            int tailStartIndex = Math.Max(head.Length, text.Length - tailCharEstimate);
            if (tailStartIndex < text.Length && char.IsLowSurrogate(text[tailStartIndex]))
            {
                tailStartIndex++;
            }
            string tail = text.Substring(tailStartIndex);

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
