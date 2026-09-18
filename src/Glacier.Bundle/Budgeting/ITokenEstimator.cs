namespace Glacier.Bundle.Budgeting
{
    /// <summary>
    /// Provides token estimation and truncation capabilities for text budgeting.
    /// </summary>
    public interface ITokenEstimator
    {
        /// <summary>
        /// Estimates the number of tokens in the given text.
        /// </summary>
        int EstimateTokens(string text);

        /// <summary>
        /// Truncates the text so that its token count does not exceed maxTokens.
        /// </summary>
        string TruncateToTokens(string text, int maxTokens);
    }
}
