using System;
using System.Text.RegularExpressions;

namespace AIWeather.Services
{
    /// <summary>
    /// Removes credentials from URLs before they are written to NINA's persistent log.
    /// This also handles malformed URLs that <see cref="Uri"/> cannot parse reliably.
    /// </summary>
    internal static class LogRedactor
    {
        public static string RedactRtspUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value ?? string.Empty;
            }

            try
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                    && (uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
                        || uri.Scheme.Equals("rtsps", StringComparison.OrdinalIgnoreCase))
                    && !string.IsNullOrEmpty(uri.UserInfo))
                {
                    var builder = new UriBuilder(uri)
                    {
                        UserName = "***",
                        Password = "***"
                    };
                    return builder.Uri.ToString();
                }
            }
            catch
            {
                // Fall through to a best-effort redaction for malformed URLs.
            }

            try
            {
                return Regex.Replace(
                    value,
                    @"(?i)\b(rtsps?://)[^\s@]+@",
                    "$1***:***@");
            }
            catch
            {
                return "[invalid RTSP URL redacted]";
            }
        }

        /// <summary>
        /// Best-effort sanitization for optional raw provider responses and failure text.
        /// Provider output should not contain credentials, but the dataset contract makes
        /// that an enforced boundary rather than an assumption.
        /// </summary>
        public static string? RedactSensitiveText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var result = RedactRtspUrl(value);
            try
            {
                result = Regex.Replace(
                    result,
                    @"(?i)(authorization\s*[:=]\s*bearer\s+)[A-Za-z0-9._~+/=-]+",
                    "$1[REDACTED]");
                result = Regex.Replace(
                    result,
                    @"(?i)([?&](?:key|api_key|apikey)=)[^&\s""']+",
                    "$1[REDACTED]");
                result = Regex.Replace(
                    result,
                    @"(?i)(""(?:apiKey|api_key|token|password)""\s*:\s*"")[^""]+("")",
                    "$1[REDACTED]$2");
                result = Regex.Replace(
                    result,
                    @"(?i)(\b(?:api[_-]?key|apikey|access[_-]?token|token|password|secret)\s*[:=]\s*)(?!\[REDACTED\])[^\s,;&""']+",
                    "$1[REDACTED]");
            }
            catch
            {
                return "[sensitive text redacted after parse failure]";
            }

            const int maximumLength = 65536;
            return result.Length <= maximumLength
                ? result
                : result.Substring(0, maximumLength) + "...[truncated]";
        }
    }
}
