using System;
using System.Collections.Generic;
using System.Linq;

namespace AIWeather.Services
{
    /// <summary>
    /// The Gemini Developer API uses the same endpoint for free and billed projects.
    /// This tier is therefore an explicit operator declaration that selects separate
    /// credentials, pacing and diagnostics; Google still enforces the actual tier on
    /// the project associated with the API key.
    /// </summary>
    public enum GeminiServiceTier
    {
        Free,
        Paid
    }

    internal static class GeminiProviderProfile
    {
        public const string PaidProviderId = "Gemini";
        public const string FreeProviderId = "GeminiFree";
        public const string FreeProviderDisplayName = "Gemini Free";

        public static IReadOnlyList<string> DefaultFreeModelOrder { get; } = new[]
        {
            "gemini-3.5-flash-lite",
            "gemini-3.1-flash-lite",
            "gemini-3.7-flash",
            "gemini-3.6-flash",
            "gemini-3.5-flash",
            "gemini-3-flash",
            "gemini-2.5-flash",
            "gemini-2.0-flash",
            "gemini-2.5-flash-lite",
            "gemini-2.0-flash-lite"
        };

        public static bool IsPaid(string? provider) =>
            string.Equals(provider?.Trim(), PaidProviderId, StringComparison.OrdinalIgnoreCase);

        public static bool IsFree(string? provider) =>
            string.Equals(provider?.Trim(), FreeProviderId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider?.Trim(), FreeProviderDisplayName, StringComparison.OrdinalIgnoreCase);

        public static bool IsGemini(string? provider) => IsPaid(provider) || IsFree(provider);

        public static GeminiServiceTier TierForProvider(string? provider) =>
            IsPaid(provider) ? GeminiServiceTier.Paid : GeminiServiceTier.Free;

        public static string DisplayName(GeminiServiceTier tier) =>
            tier == GeminiServiceTier.Paid ? PaidProviderId : FreeProviderDisplayName;

        public static IReadOnlyList<string> ParseFreeModelOrder(string? serialized)
        {
            var configured = (serialized ?? string.Empty)
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(model => model.Trim())
                .Where(model => model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // The queue is reorderable, not destructive. Append newly introduced defaults
            // after an upgrade so an older saved order cannot silently lose coverage.
            foreach (var model in DefaultFreeModelOrder)
            {
                if (!configured.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    configured.Add(model);
                }
            }

            return configured.Count > 0 ? configured : DefaultFreeModelOrder.ToArray();
        }

        public static string SerializeFreeModelOrder(IEnumerable<string> models) =>
            string.Join("\n", ParseFreeModelOrder(string.Join("\n", models ?? Array.Empty<string>())));

        /// <summary>
        /// Before the tier split, the only Gemini option was documented as the free API
        /// tier. The one-time settings migration preserves that meaning and never runs
        /// again, so a newly selected paid provider remains paid on subsequent starts.
        /// </summary>
        public static string MigrateLegacyProvider(string? provider, bool migrationCompleted)
        {
            var normalized = string.IsNullOrWhiteSpace(provider) ? "Local" : provider.Trim();
            return !migrationCompleted && IsPaid(normalized)
                ? FreeProviderId
                : normalized;
        }
    }
}
