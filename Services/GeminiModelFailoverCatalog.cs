using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AIWeather.Services
{
    /// <summary>
    /// Process-local catalog of Gemini models that the provider has advertised as
    /// generateContent-capable. The selected model remains the permanent primary; this
    /// catalog is used only for short-lived HTTP 503 failover.
    /// </summary>
    internal static class GeminiModelFailoverCatalog
    {
        private static readonly object Gate = new object();
        private static string[] _models =
        {
            "gemini-flash-latest",
            "gemini-3.7-flash",
            "gemini-3.6-flash",
            "gemini-3.5-flash",
            "gemini-3.5-flash-lite",
            "gemini-3.1-flash-lite",
            "gemini-2.5-flash",
            "gemini-2.5-flash-lite"
        };

        public static void Update(IEnumerable<string>? models)
        {
            var filtered = (models ?? Array.Empty<string>())
                .Where(IsWeatherFlashModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (filtered.Length == 0)
            {
                return;
            }

            lock (Gate)
            {
                _models = filtered;
            }
        }

        public static IReadOnlyList<string> GetFailoverCandidates(string primaryModel)
        {
            string[] snapshot;
            lock (Gate)
            {
                snapshot = _models.ToArray();
            }

            var primaryVersion = VersionScore(primaryModel);
            return snapshot
                .Where(model => !string.Equals(model, primaryModel, StringComparison.OrdinalIgnoreCase))
                .Where(IsWeatherFlashModel)
                .OrderBy(model => VersionDistance(primaryVersion, VersionScore(model)))
                .ThenBy(model => LiteMismatch(primaryModel, model))
                .ThenByDescending(VersionScore)
                .ThenBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsWeatherFlashModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)
                || !model.StartsWith("gemini", StringComparison.OrdinalIgnoreCase)
                || model.IndexOf("flash", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            var excluded = new[] { "image", "live", "audio", "tts", "embedding" };
            return !excluded.Any(value => model.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static double VersionScore(string? model)
        {
            var match = Regex.Match(model ?? string.Empty, @"gemini-(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return double.MaxValue;
            }

            var major = int.Parse(match.Groups[1].Value);
            var minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
            return major + minor / 100.0;
        }

        private static double VersionDistance(double primary, double candidate)
        {
            if (primary == double.MaxValue || candidate == double.MaxValue)
            {
                return double.MaxValue;
            }

            return Math.Abs(primary - candidate);
        }

        private static int LiteMismatch(string primary, string candidate)
        {
            var primaryLite = primary.IndexOf("lite", StringComparison.OrdinalIgnoreCase) >= 0;
            var candidateLite = candidate.IndexOf("lite", StringComparison.OrdinalIgnoreCase) >= 0;
            return primaryLite == candidateLite ? 0 : 1;
        }
    }
}
