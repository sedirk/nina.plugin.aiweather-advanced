using AIWeather.Models;

namespace AIWeather.Services
{
    /// <summary>
    /// Counts how many checks in a row the chosen AI provider failed to answer and the
    /// offline analyzer answered instead. The fallback is right for safety and wrong for
    /// the owner if it stays silent: one user ran a whole night on the offline heuristic,
    /// paying for a provider that rejected every request, and found out from the log the
    /// next day. This is what puts that fact in the panel.
    /// </summary>
    public sealed class ProviderHealthTracker
    {
        public string Provider { get; private set; } = string.Empty;
        public int ConsecutiveFallbacks { get; private set; }
        public string? LastError { get; private set; }

        public bool IsFailing => ConsecutiveFallbacks > 0;

        /// <summary>
        /// Record one analysis result. Returns true when the provider's health flipped -
        /// from answering to failing or back - so the caller can log the transition once
        /// instead of every cycle.
        /// </summary>
        public bool Record(AnalysisProvenance provenance)
        {
            var wasFailing = IsFailing;
            if (provenance.FailureCategory == AnalysisFailureCategory.ScheduledLocal) return false;
            Provider = provenance.Provider;

            if (!provenance.OnlineSucceeded && provenance.FailureCategory != AnalysisFailureCategory.None)
            {
                ConsecutiveFallbacks++;
                LastError = global::AIWeather.Localization.UiLocalization.FailureCategory(provenance);
            }
            else
            {
                ConsecutiveFallbacks = 0;
                LastError = null;
            }

            return wasFailing != IsFailing;
        }

        public void Reset()
        {
            ConsecutiveFallbacks = 0;
            LastError = null;
        }

        /// <summary>One line for the panel; empty while the provider is answering.</summary>
        public string Describe()
        {
            if (!IsFailing) { return string.Empty; }

            return global::AIWeather.Localization.UiLocalization.Text("Runtime.ProviderHealthCount", Provider, ConsecutiveFallbacks);
        }
    }
}
