namespace Glacier.Bundle.Budgeting
{
    /// <summary>
    /// Specifies the compaction strategy when prompt context exceeds the allocated token budget.
    /// </summary>
    public enum CompactionStrategy
    {
        /// <summary>
        /// Drops entire sections in ascending order of priority (lowest priority dropped first)
        /// until the total token count fits within budget.
        /// </summary>
        StrictPrune,

        /// <summary>
        /// For sections or context exceeding budget, preserves the head (beginning) and tail (end)
        /// with an informational truncation notice in between.
        /// </summary>
        HeadTailTruncate,

        /// <summary>
        /// Decays and compresses non-fixed sections proportionally according to their priority weights
        /// to fit within the remaining available budget.
        /// </summary>
        ProportionalDecay
    }
}
