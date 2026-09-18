using System;

namespace Glacier.Bundle.Budgeting
{
    /// <summary>
    /// Represents a discrete section of context with associated priority and budgeting rules.
    /// </summary>
    public class BundleSection
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Relative priority of the section. Higher values indicate greater importance.
        /// </summary>
        public int Priority { get; set; }

        public string Content { get; set; } = string.Empty;

        /// <summary>
        /// When true, this section is immune to pruning or decay (e.g. system instructions or mandatory headers).
        /// </summary>
        public bool IsFixed { get; set; }

        /// <summary>
        /// Optional per-section strategy override.
        /// </summary>
        public CompactionStrategy? StrategyOverride { get; set; }

        public BundleSection()
        {
        }

        public BundleSection(string name, int priority, string content, bool isFixed = false, CompactionStrategy? strategyOverride = null)
        {
            Name = name ?? string.Empty;
            Priority = priority;
            Content = content ?? string.Empty;
            IsFixed = isFixed;
            StrategyOverride = strategyOverride;
        }

        public int EstimateTokens(ITokenEstimator estimator)
        {
            if (estimator == null) throw new ArgumentNullException(nameof(estimator));
            return estimator.EstimateTokens(Content);
        }
    }
}
