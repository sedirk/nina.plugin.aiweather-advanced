using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIWeather.Services
{
    /// <summary>
    /// Provider-neutral metadata extracted from Gemini's google.rpc error envelope. Google
    /// can return HTTP 429 both for an ordinary short rate limit and for an explicit
    /// QuotaFailure. Only the latter opens the long-lived circuit.
    /// </summary>
    internal sealed class GeminiQuotaInfo
    {
        public bool IsQuotaFailure { get; init; }
        public string ProviderFailureCode { get; init; } = "rate_limited";
        public string? QuotaMetric { get; init; }
        public string? QuotaId { get; init; }
        public TimeSpan? RetryDelay { get; init; }
        public bool IsDailyQuota { get; init; }
    }

    internal static class GeminiQuotaParser
    {
        private static readonly Regex RetryMessagePattern = new Regex(
            @"(?i)\bretry\s+in\s+(?<seconds>\d+(?:\.\d+)?)s\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static GeminiQuotaInfo Parse(
            HttpStatusCode statusCode,
            string? responseBody,
            TimeSpan? headerRetryDelay = null)
        {
            var hasQuotaFailure = false;
            string? quotaMetric = null;
            string? quotaId = null;
            string? providerStatus = null;
            string? providerErrorCode = null;
            string? message = null;
            TimeSpan? bodyRetryDelay = null;

            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                try
                {
                    using var document = JsonDocument.Parse(responseBody);
                    if (document.RootElement.TryGetProperty("error", out var error)
                        && error.ValueKind == JsonValueKind.Object)
                    {
                        providerStatus = ReadString(error, "status");
                        providerErrorCode = ReadString(error, "code");
                        message = ReadString(error, "message");

                        if (error.TryGetProperty("details", out var details)
                            && details.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var detail in details.EnumerateArray())
                            {
                                if (detail.ValueKind != JsonValueKind.Object)
                                {
                                    continue;
                                }

                                var type = ReadString(detail, "@type") ?? string.Empty;
                                if (type.EndsWith("google.rpc.RetryInfo", StringComparison.Ordinal)
                                    && TryParseGoogleDuration(ReadString(detail, "retryDelay"), out var parsedDelay))
                                {
                                    bodyRetryDelay = parsedDelay;
                                }

                                if (!type.EndsWith("google.rpc.QuotaFailure", StringComparison.Ordinal))
                                {
                                    continue;
                                }

                                hasQuotaFailure = true;
                                if (!detail.TryGetProperty("violations", out var violations)
                                    || violations.ValueKind != JsonValueKind.Array)
                                {
                                    continue;
                                }

                                foreach (var violation in violations.EnumerateArray())
                                {
                                    if (violation.ValueKind != JsonValueKind.Object)
                                    {
                                        continue;
                                    }

                                    quotaMetric ??= ReadString(violation, "quotaMetric");
                                    quotaId ??= ReadString(violation, "quotaId");
                                }
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // A proxy can replace Google's JSON error with text or HTML. The text
                    // fallback below still extracts RetryInfo-like guidance when possible.
                }

                if (!bodyRetryDelay.HasValue
                    && TryParseRetryDelayFromMessage(message ?? responseBody, out var parsedMessageDelay))
                {
                    bodyRetryDelay = parsedMessageDelay;
                }
            }

            var messageSignalsQuota = ContainsQuotaSignal(message)
                                      || ContainsQuotaSignal(responseBody);
            var providerCodeSignalsQuota = string.Equals(
                providerErrorCode,
                "quota_exceeded",
                StringComparison.OrdinalIgnoreCase);
            var isQuotaFailure = statusCode == (HttpStatusCode)429
                                 && (hasQuotaFailure
                                     || providerCodeSignalsQuota
                                     || messageSignalsQuota);

            var providerCode = isQuotaFailure
                ? "quota_exhausted"
                : statusCode == (HttpStatusCode)429
                    ? "rate_limited"
                    : string.IsNullOrWhiteSpace(providerStatus)
                        ? $"http_{(int)statusCode}"
                        : providerStatus!.Trim().ToLowerInvariant();
            var isDailyQuota = ContainsDailySignal(quotaId)
                               || ContainsDailySignal(message)
                               // The current Gemini API error reference reserves this
                               // machine code for daily-quota exhaustion. Short-window
                               // throttling uses rate_limit_exceeded instead.
                               || providerCodeSignalsQuota;

            return new GeminiQuotaInfo
            {
                IsQuotaFailure = isQuotaFailure,
                ProviderFailureCode = providerCode,
                QuotaMetric = quotaMetric,
                QuotaId = quotaId,
                RetryDelay = NormalizeDelay(headerRetryDelay) ?? NormalizeDelay(bodyRetryDelay),
                IsDailyQuota = isDailyQuota
            };
        }

        private static string? ReadString(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var value)
                   && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static bool ContainsQuotaSignal(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && (value.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("QuotaFailure", StringComparison.OrdinalIgnoreCase));
        }

        private static bool ContainsDailySignal(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && (value.Contains("PerDay", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("RequestsPerDay", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("per day", StringComparison.OrdinalIgnoreCase)
                       || value.Contains("RPD", StringComparison.OrdinalIgnoreCase));
        }

        private static TimeSpan? NormalizeDelay(TimeSpan? delay)
        {
            return delay.HasValue && delay.Value > TimeSpan.Zero ? delay : null;
        }

        internal static bool TryParseGoogleDuration(string? value, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value)
                || !value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!double.TryParse(
                    value.Substring(0, value.Length - 1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds)
                || seconds <= 0)
            {
                return false;
            }

            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }

        internal static bool TryParseRetryDelayFromMessage(string? value, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = RetryMessagePattern.Match(value);
            if (!match.Success
                || !double.TryParse(
                    match.Groups["seconds"].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var seconds)
                || seconds <= 0)
            {
                return false;
            }

            delay = TimeSpan.FromSeconds(seconds);
            return true;
        }
    }

    internal sealed class GeminiQuotaCircuitState
    {
        public DateTimeOffset RetryAfterUtc { get; init; }
        public int ConsecutiveFailures { get; init; }
        public string ProviderFailureCode { get; init; } = "quota_exhausted";
        public string? QuotaMetric { get; init; }
        public string? QuotaId { get; init; }
        public bool IsDailyQuota { get; init; }
    }

    /// <summary>
    /// Stops a weather monitor from hammering an exhausted Gemini project. The first
    /// QuotaFailure honors Google's full RetryInfo. Repeated failures escalate to at least
    /// 10, 30 and 60 minutes. A successful/non-quota HTTP response resets the sequence.
    /// </summary>
    internal sealed class GeminiQuotaCircuitBreaker
    {
        private static readonly TimeSpan DefaultFirstDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan DailyResetSafetyMargin = TimeSpan.FromMinutes(2);
        private readonly object _gate = new object();
        private GeminiQuotaCircuitState? _state;

        public bool TryGetActive(DateTimeOffset nowUtc, out GeminiQuotaCircuitState state)
        {
            lock (_gate)
            {
                if (_state != null && nowUtc < _state.RetryAfterUtc)
                {
                    state = _state;
                    return true;
                }

                state = _state ?? new GeminiQuotaCircuitState();
                return false;
            }
        }

        public GeminiQuotaCircuitState RecordFailure(DateTimeOffset nowUtc, GeminiQuotaInfo quota)
        {
            lock (_gate)
            {
                var consecutiveFailures = (_state?.ConsecutiveFailures ?? 0) + 1;
                var minimumDelay = consecutiveFailures switch
                {
                    1 => TimeSpan.Zero,
                    2 => TimeSpan.FromMinutes(10),
                    3 => TimeSpan.FromMinutes(30),
                    _ => TimeSpan.FromMinutes(60)
                };
                var requestedDelay = quota.RetryDelay ?? DefaultFirstDelay;
                var effectiveDelay = requestedDelay > minimumDelay
                    ? requestedDelay
                    : minimumDelay;
                var retryAfterUtc = nowUtc + effectiveDelay;
                if (quota.IsDailyQuota)
                {
                    var dailyResetUtc = NextPacificMidnightUtc(nowUtc) + DailyResetSafetyMargin;
                    if (dailyResetUtc > retryAfterUtc)
                    {
                        retryAfterUtc = dailyResetUtc;
                    }
                }

                _state = new GeminiQuotaCircuitState
                {
                    RetryAfterUtc = retryAfterUtc,
                    ConsecutiveFailures = consecutiveFailures,
                    ProviderFailureCode = quota.ProviderFailureCode,
                    QuotaMetric = quota.QuotaMetric,
                    QuotaId = quota.QuotaId,
                    IsDailyQuota = quota.IsDailyQuota
                };
                return _state;
            }
        }

        public bool Reset()
        {
            lock (_gate)
            {
                var hadFailures = _state != null;
                _state = null;
                return hadFailures;
            }
        }

        private static DateTimeOffset NextPacificMidnightUtc(DateTimeOffset nowUtc)
        {
            var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var pacificNow = TimeZoneInfo.ConvertTime(nowUtc, pacific);
            var nextMidnight = DateTime.SpecifyKind(
                pacificNow.Date.AddDays(1),
                DateTimeKind.Unspecified);
            var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnight, pacific);
            return new DateTimeOffset(nextMidnightUtc, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// Keeps quota state when N.I.N.A. rebuilds a provider after switching models or keys.
    /// The API key is represented only by a one-way SHA-256 fingerprint and is never logged.
    /// </summary>
    internal static class GeminiQuotaCircuitRegistry
    {
        private static readonly ConcurrentDictionary<string, GeminiQuotaCircuitBreaker> Circuits =
            new ConcurrentDictionary<string, GeminiQuotaCircuitBreaker>(StringComparer.Ordinal);

        public static GeminiQuotaCircuitBreaker Get(string apiKey, string modelName)
        {
            var keyBytes = Encoding.UTF8.GetBytes(apiKey?.Trim() ?? string.Empty);
            var fingerprint = Convert.ToHexString(SHA256.HashData(keyBytes));
            var registryKey = fingerprint + "|" + (modelName?.Trim() ?? string.Empty).ToLowerInvariant();
            return Circuits.GetOrAdd(registryKey, _ => new GeminiQuotaCircuitBreaker());
        }
    }
}
