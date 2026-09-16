using System;

namespace AIWeather.Services
{
    /// <summary>
    /// Builds the analysis service the options describe. One place, used by the safety
    /// monitor and by the "test analysis" button alike, so a test exercises exactly the
    /// request the monitor will send at night - a test that goes through a different path
    /// proves nothing about the night (a lesson from a Test button that once checked a
    /// hardcoded model).
    /// </summary>
    public static class AnalysisServiceFactory
    {
        /// <summary>The provider the options currently select, normalised.</summary>
        public static string SelectedProvider()
        {
            var provider = Properties.Settings.Default.AnalysisProvider;
            if (string.IsNullOrWhiteSpace(provider))
            {
                provider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
            }
            return provider.Trim();
        }

        public static IWeatherAnalysisService CreateFromSettings(AIWeatherFailoverConfiguration? failover = null)
        {
            var provider = failover?.AnalysisProvider ?? Properties.Settings.Default.AnalysisProvider;
            if (string.IsNullOrWhiteSpace(provider))
            {
                provider = (failover?.UseGitHubModels ?? Properties.Settings.Default.UseGitHubModels)
                    ? "GitHubModels"
                    : "Local";
            }

            provider = provider.Trim();
            var model = failover?.SelectedModel ?? Properties.Settings.Default.SelectedModel;

            if (string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
            {
                return new GitHubModelsAnalysisService(
                    failover?.GitHubToken ?? Properties.Settings.Default.GitHubToken,
                    model);
            }

            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return new OpenAIAnalysisService(
                    failover?.OpenAIKey ?? Properties.Settings.Default.OpenAIKey,
                    model);
            }

            if (GeminiProviderProfile.IsFree(provider))
            {
                return new GeminiFreePoolAnalysisService(
                    failover?.GeminiKey ?? Properties.Settings.Default.GeminiKey,
                    GeminiProviderProfile.ParseFreeModelOrder(
                        failover?.GeminiFreeModelOrder
                        ?? Properties.Settings.Default.GeminiFreeModelOrder),
                    failover?.GeminiFreeCycleCount
                        ?? Properties.Settings.Default.GeminiFreeCycleCount,
                    failover?.GeminiRequestEveryChecks
                        ?? Properties.Settings.Default.GeminiRequestEveryChecks);
            }

            if (GeminiProviderProfile.IsPaid(provider))
            {
                return new GeminiAnalysisService(
                    failover?.GeminiPaidKey ?? Properties.Settings.Default.GeminiPaidKey,
                    model,
                    failover?.GeminiPaidRequestEveryChecks
                        ?? Properties.Settings.Default.GeminiPaidRequestEveryChecks,
                    GeminiServiceTier.Paid);
            }

            if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return new AnthropicAnalysisService(
                    failover?.AnthropicKey ?? Properties.Settings.Default.AnthropicKey,
                    model);
            }

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                return new OllamaAnalysisService(
                    failover?.OllamaBaseUrl ?? Properties.Settings.Default.OllamaBaseUrl,
                    model,
                    failover?.OllamaDisableThinking ?? Properties.Settings.Default.OllamaDisableThinking);
            }

            return new LocalWeatherAnalysisService();
        }
    }
}
