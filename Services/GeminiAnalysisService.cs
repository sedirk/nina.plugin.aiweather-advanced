using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class GeminiAnalysisService : IOnlineWeatherAnalysisService
    {
        private static readonly HttpClient Http = new HttpClient();
        private const int MaxAttempts = 3;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

        private readonly string _apiKey;
        private readonly string _modelName;
        private bool _isInitialized;

        public GeminiAnalysisService(string apiKey, string modelName)
        {
            _apiKey = apiKey;
            // The alias tracks Google's latest stable Flash release; concrete version IDs
            // get retired out from under a hardcoded fallback (gemini-2.0-flash was).
            _modelName = string.IsNullOrWhiteSpace(modelName) ? "gemini-flash-latest" : modelName.Trim();
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                Logger.Warning("Gemini API key not configured");
                _isInitialized = false;
                return Task.FromResult(false);
            }

            _isInitialized = true;
            Logger.Info($"Gemini analysis service initialized with model: {_modelName}");
            return Task.FromResult(true);
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(Bitmap image, AstroContext? astroContext = null, CancellationToken cancellationToken = default)
        {
            var attempt = await TryAnalyzeOnlineOnlyAsync(image, astroContext, cancellationToken);
            if (attempt.Success && attempt.Result != null)
            {
                return attempt.Result;
            }

            // Compatibility for callers that only know IWeatherAnalysisService. This is an
            // explicit failed online result, never a disguised local fallback. The safety
            // monitor uses TryAnalyzeOnlineOnlyAsync and owns the fallback decision.
            return new WeatherAnalysisResult
            {
                Timestamp = DateTime.UtcNow,
                Condition = WeatherCondition.Unknown,
                CloudCoverage = 50,
                Confidence = 0,
                IsSafeForImaging = false,
                Description = $"Gemini analysis failed: {attempt.Provenance.FailureCategory}",
                Provenance = attempt.Provenance.Clone()
            };
        }

        public async Task<OnlineAnalysisAttempt> TryAnalyzeOnlineOnlyAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var attemptsUsed = 0;

            if (!_isInitialized)
            {
                Logger.Warning("Gemini service is not initialized; returning an online failure for explicit orchestration");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        _modelName,
                        AnalysisFailureCategory.Authentication,
                        0,
                        stopwatch.ElapsedMilliseconds),
                    "Gemini API key is missing or the service was not initialized");
            }

            try
            {
                Logger.Info($"Starting Gemini AI weather analysis with {_modelName}");

                var base64Image = ConvertImageToBase64(image);
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(_modelName)}:generateContent";

                var promptText = PromptText.FullPrompt;
                var promptPrefix = WeatherAnalysisPrompts.BuildPromptPrefix(astroContext);
                if (promptPrefix.Length > 0)
                    promptText = promptPrefix + "\n" + promptText;

                var payload = new
                {
                    contents = new object[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { text = promptText },
                                new
                                {
                                    inlineData = new
                                    {
                                        mimeType = "image/jpeg",
                                        data = base64Image
                                    }
                                }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.1,
                        // Newer Gemini Flash models may spend part of the output budget on
                        // internal reasoning. 512 tokens produced repeatedly truncated JSON
                        // in live NINA runs, even though the HTTP request itself succeeded.
                        maxOutputTokens = 2048,
                        responseMimeType = "application/json"
                    }
                };

                var serializedPayload = JsonSerializer.Serialize(payload);

                // Keep retries inside the original 60-second request budget. A transient
                // provider failure must not turn a one-minute monitor interval into several
                // minutes of blocked work.
                using var timeoutCts = new CancellationTokenSource(RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                for (var attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    attemptsUsed = attempt;
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, url);
                        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
                        request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                        request.Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");

                        Logger.Info($"Calling Gemini API (attempt {attempt}/{MaxAttempts})...");

                        using var response = await Http.SendAsync(request, linkedCts.Token);
                        var json = await response.Content.ReadAsStringAsync(linkedCts.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var isTransient = IsTransientStatus(response.StatusCode);
                            if (isTransient && attempt < MaxAttempts)
                            {
                                var delay = GetRetryDelay(response, json, attempt);
                                Logger.Warning(
                                    $"Gemini API temporarily unavailable: HTTP {(int)response.StatusCode}. " +
                                    $"Retrying in {delay.TotalSeconds:F1}s (attempt {attempt + 1}/{MaxAttempts}).");
                                await Task.Delay(delay, linkedCts.Token);
                                continue;
                            }

                            Logger.Error(
                                $"Gemini API error after {attempt} attempt(s): HTTP {(int)response.StatusCode}: " +
                                TruncateForLog(json));
                            throw new GeminiApiException(response.StatusCode, json, attempt, isTransient);
                        }

                        Logger.Info("Gemini API responded, parsing response...");

                        using var doc = JsonDocument.Parse(json);
                        var text = ExtractGeminiText(doc.RootElement);

                        var result = PromptText.ParseAIResponse(text);
                        if (!WeatherAnalysisValidator.IsValidTeacherResult(result, out var validationReason))
                        {
                            Logger.Warning($"Gemini returned a response rejected by the weather schema: {validationReason}");
                            return OnlineAnalysisAttempt.Failed(
                                AnalysisMetadata.FailedOnline(
                                    AnalysisOrigin.Gemini,
                                    "Gemini",
                                    _modelName,
                                    AnalysisFailureCategory.SchemaRejected,
                                    attempt,
                                    stopwatch.ElapsedMilliseconds,
                                    (int)response.StatusCode),
                                validationReason);
                        }

                        result.Provenance = AnalysisMetadata.Online(
                            AnalysisOrigin.Gemini,
                            "Gemini",
                            _modelName,
                            attempt,
                            stopwatch.ElapsedMilliseconds,
                            (int)response.StatusCode);
                        Logger.Info($"Gemini analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                        return OnlineAnalysisAttempt.Succeeded(result);
                    }
                    catch (HttpRequestException ex) when (attempt < MaxAttempts)
                    {
                        var delay = GetRetryDelay(null, null, attempt);
                        Logger.Warning(
                            $"Gemini network request failed: {ex.Message}. " +
                            $"Retrying in {delay.TotalSeconds:F1}s (attempt {attempt + 1}/{MaxAttempts}).");
                        await Task.Delay(delay, linkedCts.Token);
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new GeminiApiException(null, ex.Message, attempt, true, ex);
                    }
                }

                throw new GeminiApiException(null, "Retry loop ended without a response", MaxAttempts, true);
            }
            catch (OperationCanceledException ex)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                Logger.Warning($"Gemini API call timed out: {ex.Message}");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        _modelName,
                        AnalysisFailureCategory.Timeout,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds),
                    "Gemini request timed out");
            }
            catch (GeminiApiException ex)
            {
                var status = ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : "network error";
                var reason = ex.IsTransient
                    ? $"Gemini temporarily unavailable after {ex.Attempts} attempts ({status})"
                    : $"Gemini request rejected ({status})";

                Logger.Error($"{reason}: {ex.Message}");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        _modelName,
                        AnalysisMetadata.FromHttpStatus(ex.StatusCode),
                        ex.Attempts,
                        stopwatch.ElapsedMilliseconds,
                        ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null),
                    reason);
            }
            catch (JsonException ex)
            {
                Logger.Error($"Gemini returned malformed envelope JSON: {ex.Message}");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        _modelName,
                        AnalysisFailureCategory.MalformedResponse,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds,
                        200),
                    "Gemini response envelope was malformed");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Gemini online analysis: {ex.Message}", ex);
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        _modelName,
                        ex is HttpRequestException
                            ? AnalysisFailureCategory.Network
                            : AnalysisFailureCategory.Unknown,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds),
                    ex.GetType().Name);
            }
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        private static TimeSpan GetRetryDelay(HttpResponseMessage? response, string? responseBody, int attempt)
        {
            var maximumServerDelay = TimeSpan.FromSeconds(30);
            var retryAfter = response?.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return delta > maximumServerDelay ? maximumServerDelay : delta;
            }

            if (retryAfter?.Date is DateTimeOffset retryDate)
            {
                var requestedDelay = retryDate - DateTimeOffset.UtcNow;
                if (requestedDelay > TimeSpan.Zero)
                {
                    return requestedDelay > maximumServerDelay
                        ? maximumServerDelay
                        : requestedDelay;
                }
            }

            // Gemini commonly puts quota retry guidance in the JSON body as a
            // google.rpc.RetryInfo duration (or in the human-readable message) without a
            // Retry-After header. Respect it so retries do not make a 429 quota window worse.
            if (TryGetGeminiRetryDelay(responseBody, out var geminiDelay))
            {
                return geminiDelay > maximumServerDelay ? maximumServerDelay : geminiDelay;
            }

            // Exponential backoff with jitter, as recommended for transient Gemini errors.
            var exponentialSeconds = Math.Min(8.0, Math.Pow(2.0, attempt - 1));
            var jitterMilliseconds = Random.Shared.Next(150, 651);
            return TimeSpan.FromSeconds(exponentialSeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
        }

        private static bool TryGetGeminiRetryDelay(string? responseBody, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (document.RootElement.TryGetProperty("error", out var error))
                {
                    if (error.TryGetProperty("details", out var details)
                        && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in details.EnumerateArray())
                        {
                            if (detail.ValueKind == JsonValueKind.Object
                                && detail.TryGetProperty("retryDelay", out var retryDelay)
                                && retryDelay.ValueKind == JsonValueKind.String
                                && TryParseGoogleDuration(retryDelay.GetString(), out delay))
                            {
                                return true;
                            }
                        }
                    }

                    if (error.TryGetProperty("message", out var message)
                        && message.ValueKind == JsonValueKind.String
                        && TryParseRetryDelayFromMessage(message.GetString(), out delay))
                    {
                        return true;
                    }
                }
            }
            catch (JsonException)
            {
                // Some proxies return text or HTML instead of Google's normal JSON body.
            }

            return TryParseRetryDelayFromMessage(responseBody, out delay);
        }

        private static bool TryParseGoogleDuration(string? value, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value) || !value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
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

        private static bool TryParseRetryDelayFromMessage(string? value, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = Regex.Match(
                value,
                @"(?i)\bretry\s+in\s+(?<seconds>\d+(?:\.\d+)?)s\b",
                RegexOptions.CultureInvariant);
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

        private static string TruncateForLog(string value, int maxLength = 1000)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }

        private sealed class GeminiApiException : Exception
        {
            public GeminiApiException(
                HttpStatusCode? statusCode,
                string responseBody,
                int attempts,
                bool isTransient,
                Exception? innerException = null)
                : base(TruncateForLog(responseBody), innerException)
            {
                StatusCode = statusCode;
                Attempts = attempts;
                IsTransient = isTransient;
            }

            public HttpStatusCode? StatusCode { get; }
            public int Attempts { get; }
            public bool IsTransient { get; }
        }

        private static string ExtractGeminiText(JsonElement root)
        {
            try
            {
                if (root.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
                {
                    var content = candidates[0].GetProperty("content");
                    if (content.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    {
                        var sb = new StringBuilder();
                        foreach (var part in parts.EnumerateArray())
                        {
                            if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                            {
                                sb.AppendLine(textProp.GetString());
                            }
                        }
                        return sb.ToString().Trim();
                    }
                }
            }
            catch
            {
                // best-effort
            }

            return string.Empty;
        }

        private static string ConvertImageToBase64(Bitmap image)
        {
            using var memoryStream = new MemoryStream();
            image.Save(memoryStream, ImageFormat.Jpeg);
            return Convert.ToBase64String(memoryStream.ToArray());
        }

        private static class PromptText
        {
                        public static string FullPrompt => WeatherAnalysisPrompts.DetailedSystemPrompt;

            public static WeatherAnalysisResult ParseAIResponse(string jsonResponse)
            {
                try
                {
                    jsonResponse = jsonResponse.Trim();
                    if (jsonResponse.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonResponse = jsonResponse.Substring(7);
                    }
                    if (jsonResponse.StartsWith("```", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonResponse = jsonResponse.Substring(3);
                    }
                    if (jsonResponse.EndsWith("```", StringComparison.OrdinalIgnoreCase))
                    {
                        jsonResponse = jsonResponse.Substring(0, jsonResponse.Length - 3);
                    }
                    jsonResponse = jsonResponse.Trim();

                    using var json = JsonDocument.Parse(jsonResponse);
                    var root = json.RootElement;

                    var conditionStr = root.GetProperty("condition").GetString() ?? "Unknown";
                    var condition = Enum.TryParse<WeatherCondition>(conditionStr, true, out var parsedCondition)
                        ? parsedCondition
                        : WeatherCondition.Unknown;

                    var cloudCoverage = root.GetProperty("cloudCoverage").GetDouble();
                    var rainDetected = root.GetProperty("rainDetected").GetBoolean();
                    var fogDetected = root.GetProperty("fogDetected").GetBoolean();
                    var isSafe = root.GetProperty("isSafe").GetBoolean();
                    var description = root.GetProperty("description").GetString() ?? string.Empty;
                    var confidence = root.TryGetProperty("confidence", out var confProp) ? confProp.GetDouble() : 85.0;

                    return new WeatherAnalysisResult
                    {
                        Timestamp = DateTime.UtcNow,
                        Condition = condition,
                        CloudCoverage = cloudCoverage,
                        Confidence = confidence,
                        IsSafeForImaging = isSafe,
                        Description = description,
                        RainDetected = rainDetected,
                        FogDetected = fogDetected,
                        RawAnalysisData = jsonResponse
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error parsing AI response: {ex.Message}", ex);
                    Logger.Debug($"Raw response: {jsonResponse}");

                    return new WeatherAnalysisResult
                    {
                        Timestamp = DateTime.UtcNow,
                        Condition = WeatherCondition.Unknown,
                        CloudCoverage = 50,
                        Confidence = 0,
                        IsSafeForImaging = false,
                        Description = $"Failed to parse AI response: {ex.Message}",
                        RawAnalysisData = jsonResponse
                    };
                }
            }
        }
    }
}
