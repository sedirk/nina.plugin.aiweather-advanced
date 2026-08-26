using System;
using System.Security.Cryptography;
using System.Text;

namespace AIWeather.Services
{
    public static class AIWeatherClusterProtocol
    {
        public const int SchemaVersion = 1;
        public const string Product = "ai-weather-advanced";
        public const int MinimumTokenLength = 16;
        public const int MaximumRequestHeaderBytes = 16 * 1024;

        public static string GenerateSharedToken() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        public static bool IsTokenUsable(string? token) =>
            !string.IsNullOrWhiteSpace(token) && token.Trim().Length >= MinimumTokenLength;

        public static bool FixedTimeTokenEquals(string? expected, string? supplied)
        {
            if (expected == null || supplied == null)
            {
                return false;
            }

            var expectedBytes = Encoding.UTF8.GetBytes(expected);
            var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
            return expectedBytes.Length == suppliedBytes.Length
                   && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
        }
    }

    public sealed class AIWeatherClusterSnapshot
    {
        public int SchemaVersion { get; set; } = AIWeatherClusterProtocol.SchemaVersion;
        public string Product { get; set; } = AIWeatherClusterProtocol.Product;
        public string NodeId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public long Sequence { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public bool Connected { get; set; }
        public bool Monitoring { get; set; }
        public bool IsSafe { get; set; }
        public string SafetyReason { get; set; } = "unknown";
        public string WeatherCondition { get; set; } = "Unknown";
        public double CloudCoverage { get; set; }
        public double Confidence { get; set; }
        public bool RainDetected { get; set; }
        public bool FogDetected { get; set; }
        public string Provider { get; set; } = "Unknown";
        public string Model { get; set; } = "Unknown";
        public DateTime? AnalysisUtc { get; set; }
        public double? AnalysisAgeSeconds { get; set; }
        public bool SourceFresh { get; set; }
    }

    public sealed class AIWeatherClusterHealth
    {
        public int SchemaVersion { get; set; } = AIWeatherClusterProtocol.SchemaVersion;
        public string Product { get; set; } = AIWeatherClusterProtocol.Product;
        public string NodeId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public DateTime GeneratedUtc { get; set; }
    }

    public sealed class ClusterErrorResponse
    {
        public string Code { get; set; } = "unknown";
        public string Message { get; set; } = "Unknown error";
        public bool Retryable { get; set; }
    }

    public enum AIWeatherReplicaFailure
    {
        None,
        Waiting,
        Network,
        Authentication,
        Protocol,
        Stale
    }
}
