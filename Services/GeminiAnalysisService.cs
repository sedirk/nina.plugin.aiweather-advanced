using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace AIWeather.Services
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class GeminiAnalysisService : IOnlineWeatherAnalysisService
    {
        private const int MaxAttempts = 3;
        private const int PrimaryProbeAfterAlternateChecks = 2;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MinimumAttemptBudget = TimeSpan.FromSeconds(5);

        private readonly string _apiKey;
        private readonly string _primaryModelName;
        private readonly IHttpClientProvider _httpProvider;
        private readonly Func<string, GeminiQuotaCircuitBreaker> _quotaCircuitForModel;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly int _requestEveryChecks;
        private readonly IReadOnlyList<string>? _injectedFailoverCandidates;
        private readonly object _failoverGate = new object();
        private string? _alternateModelName;
        private int _alternateSuccesses;
        private int _alternateChecks;
        private long _requestSequence;
        private bool _isInitialized;

        public GeminiAnalysisService(
            string apiKey,
            string modelName,
            int requestEveryChecks = 1)
            : this(
                apiKey,
                NormalizeModelName(modelName),
                new SystemProxyAwareHttpClientProvider(),
                candidate => GeminiQuotaCircuitRegistry.Get(apiKey, candidate),
                () => DateTimeOffset.UtcNow,
                requestEveryChecks)
        {
        }

        internal GeminiAnalysisService(
            string apiKey,
            string modelName,
            HttpClient http,
            GeminiQuotaCircuitBreaker quotaCircuit,
            Func<DateTimeOffset> utcNow,
            int requestEveryChecks = 1,
            IReadOnlyList<string>? failoverCandidates = null)
            : this(
                apiKey,
                modelName,
                new FixedHttpClientProvider(http),
                _ => quotaCircuit,
                utcNow,
                requestEveryChecks,
                failoverCandidates)
        {
        }

        internal GeminiAnalysisService(
            string apiKey,
            string modelName,
            HttpClient http,
            Func<string, GeminiQuotaCircuitBreaker> quotaCircuitForModel,
            Func<DateTimeOffset> utcNow,
            int requestEveryChecks = 1,
            IReadOnlyList<string>? failoverCandidates = null)
            : this(
                apiKey,
                modelName,
                new FixedHttpClientProvider(http),
                quotaCircuitForModel,
                utcNow,
                requestEveryChecks,
                failoverCandidates)
        {
        }

        private GeminiAnalysisService(
            string apiKey,
            string modelName,
            IHttpClientProvider httpProvider,
            Func<string, GeminiQuotaCircuitBreaker> quotaCircuitForModel,
            Func<DateTimeOffset> utcNow,
            int requestEveryChecks = 1,
            IReadOnlyList<string>? failoverCandidates = null)
        {
            _apiKey = apiKey;
            // The alias tracks Google's latest stable Flash release; concrete version IDs
            // get retired out from under a hardcoded fallback (gemini-2.0-flash was).
            _primaryModelName = NormalizeModelName(modelName);
            _httpProvider = httpProvider ?? throw new ArgumentNullException(nameof(httpProvider));
            _quotaCircuitForModel = quotaCircuitForModel ?? throw new ArgumentNullException(nameof(quotaCircuitForModel));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
            _requestEveryChecks = Math.Clamp(requestEveryChecks, 1, 10000);
            _injectedFailoverCandidates = failoverCandidates?
                .Select(NormalizeModelName)
                .Where(candidate => !string.Equals(candidate, _primaryModelName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeModelName(string modelName)
        {
            return string.IsNullOrWhiteSpace(modelName) ? "gemini-flash-latest" : modelName.Trim();
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
            Logger.Info($"Gemini analysis service initialized with primary model: {_primaryModelName}; " +
                        "provider/quota failover is temporary, quota-aware per model, and never changes the saved model selection");
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
            var diagnostics = new List<AnalysisAttemptDiagnostic>();
            var currentModel = GetStartingModel(out var probingPrimary);

            if (!_isInitialized)
            {
                Logger.Warning("Gemini service is not initialized; returning an online failure for explicit orchestration");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        _primaryModelName,
                        AnalysisFailureCategory.Authentication,
                        0,
                        stopwatch.ElapsedMilliseconds),
                    "Gemini API key is missing or the service was not initialized");
            }

            var startingCircuit = _quotaCircuitForModel(currentModel);
            if (startingCircuit.TryGetActive(_utcNow(), out var activeQuota))
            {
                RecordModelFailure(currentModel, "quota circuit is still open");
                return BuildQuotaFailure(
                    currentModel,
                    activeQuota,
                    attempts: 0,
                    stopwatch.ElapsedMilliseconds,
                    httpStatus: null,
                    requestSuppressed: true,
                    diagnostics: diagnostics);
            }

            // Once a quota window expires, probe immediately even if this ordinary check
            // would otherwise fall between paced online calls. Otherwise N=12 with a
            // two-minute weather interval could silently postpone Google's first allowed
            // probe by another 22 minutes after the displayed retry time.
            var quotaProbeDue = activeQuota.ConsecutiveFailures > 0;

            var requestSequence = Interlocked.Increment(ref _requestSequence);
            if (!quotaProbeDue
                && _requestEveryChecks > 1
                && (requestSequence - 1) % _requestEveryChecks != 0)
            {
                Logger.Debug(
                    $"Gemini online request intentionally skipped by pacing policy " +
                    $"(check {requestSequence}, every {_requestEveryChecks} checks); " +
                    "local analysis remains active");
                return BuildScheduledLocal(requestSequence, stopwatch.ElapsedMilliseconds);
            }

            if (quotaProbeDue)
            {
                Logger.Info(
                    $"Gemini quota pause expired; forcing one online probe now " +
                    $"(scheduled check {requestSequence}, every {_requestEveryChecks} checks)");
            }

            try
            {
                Logger.Info($"Starting Gemini AI weather analysis with {currentModel}" +
                            (probingPrimary ? " (probing configured primary after temporary failover)" : string.Empty));

                var base64Image = ConvertImageToBase64(image);

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
                    var attemptStopwatch = Stopwatch.StartNew();
                    try
                    {
                        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(currentModel)}:generateContent";
                        using var request = new HttpRequestMessage(HttpMethod.Post, url);
                        request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
                        request.Headers.UserAgent.ParseAdd("NINA-AIWeather/1.0");
                        request.Content = new StringContent(serializedPayload, Encoding.UTF8, "application/json");

                        Logger.Info($"Calling Gemini API model {currentModel} (attempt {attempt}/{MaxAttempts})...");

                        // Resolve the transport immediately before each real API request.
                        // The provider preserves the normal connection pool, but swaps it
                        // when v2rayN/Windows proxy settings changed while N.I.N.A. stayed
                        // open.  Re-enabling the proxy therefore takes effect on this check
                        // without restarting N.I.N.A.
                        var http = _httpProvider.GetClient();
                        using var response = await http.SendAsync(request, linkedCts.Token);
                        var json = await response.Content.ReadAsStringAsync(linkedCts.Token);

                        if (!response.IsSuccessStatusCode)
                        {
                            var serverRetryDelay = GetServerRetryDelay(response, json, _utcNow());
                            var quota = GeminiQuotaParser.Parse(
                                response.StatusCode,
                                json,
                                serverRetryDelay);
                            if (quota.IsQuotaFailure)
                            {
                                diagnostics.Add(CreateDiagnostic(
                                    attempt,
                                    currentModel,
                                    response.StatusCode,
                                    AnalysisFailureCategory.QuotaExhausted,
                                    attemptStopwatch.ElapsedMilliseconds,
                                    "quota_rejected"));
                                var circuit = _quotaCircuitForModel(currentModel).RecordFailure(_utcNow(), quota);
                                RecordModelFailure(currentModel, "quota rejected");
                                Logger.Warning(
                                    $"Gemini API quota unavailable: HTTP {(int)response.StatusCode}; " +
                                    $"immediate retries suppressed, next online attempt no earlier than " +
                                    $"{circuit.RetryAfterUtc:O}; consecutive quota failures " +
                                    $"{circuit.ConsecutiveFailures}; metric " +
                                    $"{circuit.QuotaMetric ?? "unknown"}; quota id " +
                                    $"{circuit.QuotaId ?? "unknown"}; daily quota " +
                                    $"{circuit.IsDailyQuota}.");
                                return BuildQuotaFailure(
                                    currentModel,
                                    circuit,
                                    attempt,
                                    stopwatch.ElapsedMilliseconds,
                                    (int)response.StatusCode,
                                    requestSuppressed: false,
                                    diagnostics: diagnostics);
                            }

                            if (_quotaCircuitForModel(currentModel).Reset())
                            {
                                Logger.Info($"Gemini quota circuit reset for {currentModel} after a non-quota API response");
                            }

                            var isTransient = IsTransientStatus(response.StatusCode);
                            diagnostics.Add(CreateDiagnostic(
                                attempt,
                                currentModel,
                                response.StatusCode,
                                AnalysisMetadata.FromHttpStatus(response.StatusCode),
                                attemptStopwatch.ElapsedMilliseconds,
                                "http_error"));
                            if (isTransient && attempt < MaxAttempts)
                            {
                                if (response.StatusCode == HttpStatusCode.ServiceUnavailable
                                    && string.Equals(currentModel, _primaryModelName, StringComparison.OrdinalIgnoreCase)
                                    && TrySelectAlternateModel(out var alternateModel)
                                    && HasAttemptBudget(stopwatch, TimeSpan.Zero))
                                {
                                    ActivateAlternate(alternateModel);
                                    Logger.Warning(
                                        $"Gemini primary model {_primaryModelName} returned HTTP 503 after " +
                                        $"{attemptStopwatch.Elapsed.TotalSeconds:F1}s; temporarily switching to " +
                                        $"{alternateModel} within the same {RequestTimeout.TotalSeconds:F0}s budget");
                                    currentModel = alternateModel;
                                    probingPrimary = false;
                                    continue;
                                }

                                var delay = GetTransientRetryDelay(response, json, attempt);
                                if (!HasAttemptBudget(stopwatch, delay))
                                {
                                    Logger.Warning(
                                        $"Gemini retry skipped because only " +
                                        $"{Math.Max(0, (RequestTimeout - stopwatch.Elapsed).TotalSeconds):F1}s remain " +
                                        $"after HTTP {(int)response.StatusCode}; preserving the provider failure");
                                    RecordModelFailure(currentModel, "provider retry budget exhausted");
                                    return BuildDiagnosticFailure(
                                        diagnostics,
                                        currentModel,
                                        attemptsUsed,
                                        stopwatch.ElapsedMilliseconds,
                                        "Gemini retry budget exhausted after provider failure");
                                }
                                Logger.Warning(
                                    $"Gemini API model {currentModel} temporarily unavailable: HTTP {(int)response.StatusCode}. " +
                                    $"Retrying in {delay.TotalSeconds:F1}s (attempt {attempt + 1}/{MaxAttempts}).");
                                await Task.Delay(delay, linkedCts.Token);
                                continue;
                            }

                            Logger.Error(
                                $"Gemini API error after {attempt} attempt(s): HTTP {(int)response.StatusCode}: " +
                                TruncateForLog(json));
                            throw new GeminiApiException(response.StatusCode, json, attempt, isTransient);
                        }

                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            response.StatusCode,
                            AnalysisFailureCategory.None,
                            attemptStopwatch.ElapsedMilliseconds,
                            "success"));

                        if (_quotaCircuitForModel(currentModel).Reset())
                        {
                            Logger.Info($"Gemini quota circuit closed for {currentModel} after a successful API response");
                        }

                        Logger.Info("Gemini API responded, parsing response...");

                        using var doc = JsonDocument.Parse(json);
                        var text = ExtractGeminiText(doc.RootElement);

                        var result = PromptText.ParseAIResponse(text);
                        if (!WeatherAnalysisValidator.IsValidTeacherResult(result, out var validationReason))
                        {
                            RecordModelFailure(currentModel, "response schema rejected");
                            Logger.Warning($"Gemini returned a response rejected by the weather schema: {validationReason}");
                            return OnlineAnalysisAttempt.Failed(
                                AnalysisMetadata.FailedOnline(
                                    AnalysisOrigin.Gemini,
                                    "Gemini",
                                    currentModel,
                                    AnalysisFailureCategory.SchemaRejected,
                                    attempt,
                                    stopwatch.ElapsedMilliseconds,
                                    (int)response.StatusCode,
                                    attemptDiagnostics: diagnostics),
                                validationReason);
                        }

                        RecordModelSuccess(currentModel);
                        result.Provenance = AnalysisMetadata.Online(
                            AnalysisOrigin.Gemini,
                            "Gemini",
                            currentModel,
                            attempt,
                            stopwatch.ElapsedMilliseconds,
                            (int)response.StatusCode,
                            diagnostics);
                        Logger.Info($"Gemini analysis complete: {result.Condition}, Cloud Coverage: {result.CloudCoverage:F1}%");
                        return OnlineAnalysisAttempt.Succeeded(result);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            null,
                            AnalysisFailureCategory.Timeout,
                            attemptStopwatch.ElapsedMilliseconds,
                            "timeout"));
                        Logger.Warning(
                            $"Gemini model {currentModel} exhausted the bounded online-analysis budget; " +
                            "retaining earlier HTTP evidence in provenance");
                        RecordModelFailure(currentModel, "request timed out");
                        return BuildDiagnosticFailure(
                            diagnostics,
                            currentModel,
                            attemptsUsed,
                            stopwatch.ElapsedMilliseconds,
                            "Gemini request budget exhausted");
                    }
                    catch (HttpRequestException ex) when (attempt < MaxAttempts)
                    {
                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            null,
                            AnalysisFailureCategory.Network,
                            attemptStopwatch.ElapsedMilliseconds,
                            "network_error"));
                        var delay = GetTransientRetryDelay(null, null, attempt);
                        if (!HasAttemptBudget(stopwatch, delay))
                        {
                            RecordModelFailure(currentModel, "network retry budget exhausted");
                            return BuildDiagnosticFailure(
                                diagnostics,
                                currentModel,
                                attemptsUsed,
                                stopwatch.ElapsedMilliseconds,
                                "Gemini retry budget exhausted after network failure");
                        }
                        Logger.Warning(
                            $"Gemini model {currentModel} network request failed: {ex.Message}. " +
                            $"Retrying in {delay.TotalSeconds:F1}s (attempt {attempt + 1}/{MaxAttempts}).");
                        await Task.Delay(delay, linkedCts.Token);
                    }
                    catch (HttpRequestException ex)
                    {
                        diagnostics.Add(CreateDiagnostic(
                            attempt,
                            currentModel,
                            null,
                            AnalysisFailureCategory.Network,
                            attemptStopwatch.ElapsedMilliseconds,
                            "network_error"));
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
                if (diagnostics.Count == 0)
                {
                    diagnostics.Add(CreateDiagnostic(
                        Math.Max(1, attemptsUsed),
                        currentModel,
                        null,
                        AnalysisFailureCategory.Timeout,
                        stopwatch.ElapsedMilliseconds,
                        "timeout"));
                }
                RecordModelFailure(currentModel, "request timed out");
                return BuildDiagnosticFailure(
                    diagnostics,
                    currentModel,
                    attemptsUsed,
                    stopwatch.ElapsedMilliseconds,
                    "Gemini request timed out");
            }
            catch (GeminiApiException ex)
            {
                var status = ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode.Value}" : "network error";
                var reason = ex.IsTransient
                    ? $"Gemini temporarily unavailable after {ex.Attempts} attempts ({status})"
                    : $"Gemini request rejected ({status})";

                Logger.Error($"{reason}: {ex.Message}");
                RecordModelFailure(currentModel, reason);
                return BuildDiagnosticFailure(
                    diagnostics,
                    currentModel,
                    ex.Attempts,
                    stopwatch.ElapsedMilliseconds,
                    reason,
                    AnalysisMetadata.FromHttpStatus(ex.StatusCode),
                    ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null);
            }
            catch (JsonException ex)
            {
                Logger.Error($"Gemini returned malformed envelope JSON: {ex.Message}");
                RecordModelFailure(currentModel, "malformed response envelope");
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        currentModel,
                        AnalysisFailureCategory.MalformedResponse,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds,
                        200,
                        attemptDiagnostics: diagnostics),
                    "Gemini response envelope was malformed");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in Gemini online analysis: {ex.Message}", ex);
                RecordModelFailure(currentModel, ex.GetType().Name);
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini",
                        currentModel,
                        ex is HttpRequestException
                            ? AnalysisFailureCategory.Network
                            : AnalysisFailureCategory.Unknown,
                        attemptsUsed,
                        stopwatch.ElapsedMilliseconds,
                        attemptDiagnostics: diagnostics),
                    ex.GetType().Name);
            }
        }

        private string GetStartingModel(out bool probingPrimary)
        {
            string preferredModel;
            lock (_failoverGate)
            {
                if (string.IsNullOrWhiteSpace(_alternateModelName))
                {
                    preferredModel = _primaryModelName;
                }
                else if (_alternateChecks >= PrimaryProbeAfterAlternateChecks)
                {
                    preferredModel = _primaryModelName;
                }
                else
                {
                    preferredModel = _alternateModelName;
                }
            }

            probingPrimary = string.Equals(preferredModel, _primaryModelName, StringComparison.OrdinalIgnoreCase)
                             && !string.IsNullOrWhiteSpace(GetActiveAlternateModel());

            var now = _utcNow();
            if (!_quotaCircuitForModel(preferredModel).TryGetActive(now, out _))
            {
                return preferredModel;
            }

            // Quotas are per model. A temporary alternate that has exhausted its own RPD
            // allowance must never pin the service there until the next reset while the
            // configured primary is healthy. Likewise, a quota-limited primary may use a
            // different same-family Flash model whose independent circuit is still open.
            if (!string.Equals(preferredModel, _primaryModelName, StringComparison.OrdinalIgnoreCase)
                && !_quotaCircuitForModel(_primaryModelName).TryGetActive(now, out _))
            {
                probingPrimary = true;
                Logger.Info(
                    $"Gemini temporary alternate {preferredModel} is quota-paused; " +
                    $"probing configured primary {_primaryModelName} instead");
                return _primaryModelName;
            }

            if (TrySelectAlternateModel(out var availableAlternate, preferredModel))
            {
                ActivateAlternate(availableAlternate);
                probingPrimary = false;
                Logger.Info(
                    $"Gemini model {preferredModel} is quota-paused; using available " +
                    $"same-family alternate {availableAlternate}");
                return availableAlternate;
            }

            return preferredModel;
        }

        private string? GetActiveAlternateModel()
        {
            lock (_failoverGate)
            {
                return _alternateModelName;
            }
        }

        private bool TrySelectAlternateModel(
            out string alternateModel,
            string? excludedModel = null)
        {
            var now = _utcNow();
            var activeAlternate = GetActiveAlternateModel();
            if (!string.IsNullOrWhiteSpace(activeAlternate)
                && !string.Equals(activeAlternate, excludedModel, StringComparison.OrdinalIgnoreCase)
                && !_quotaCircuitForModel(activeAlternate).TryGetActive(now, out _))
            {
                alternateModel = activeAlternate;
                return true;
            }

            var candidates = _injectedFailoverCandidates
                ?? GeminiModelFailoverCatalog.GetFailoverCandidates(_primaryModelName);
            alternateModel = candidates.FirstOrDefault(candidate =>
                !string.Equals(candidate, excludedModel, StringComparison.OrdinalIgnoreCase)
                && !_quotaCircuitForModel(candidate).TryGetActive(now, out _)) ?? string.Empty;
            return alternateModel.Length > 0;
        }

        private void ActivateAlternate(string alternateModel)
        {
            lock (_failoverGate)
            {
                if (!string.Equals(_alternateModelName, alternateModel, StringComparison.OrdinalIgnoreCase))
                {
                    _alternateModelName = alternateModel;
                    _alternateSuccesses = 0;
                    _alternateChecks = 0;
                }
                else
                {
                    // A primary recovery probe failed again. Start a fresh, short hold on
                    // the already selected alternate before probing the primary once more.
                    _alternateSuccesses = 0;
                    _alternateChecks = 0;
                }
            }
        }

        private void RecordModelSuccess(string modelName)
        {
            lock (_failoverGate)
            {
                if (string.Equals(modelName, _primaryModelName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(_alternateModelName))
                    {
                        Logger.Info(
                            $"Gemini configured primary {_primaryModelName} recovered; " +
                            $"leaving temporary alternate {_alternateModelName}");
                    }
                    _alternateModelName = null;
                    _alternateSuccesses = 0;
                    _alternateChecks = 0;
                    return;
                }

                if (!string.Equals(modelName, _alternateModelName, StringComparison.OrdinalIgnoreCase))
                {
                    _alternateModelName = modelName;
                    _alternateSuccesses = 0;
                    _alternateChecks = 0;
                }

                _alternateSuccesses = Math.Min(
                    PrimaryProbeAfterAlternateChecks,
                    _alternateSuccesses + 1);
                _alternateChecks = Math.Min(
                    PrimaryProbeAfterAlternateChecks,
                    _alternateChecks + 1);
                Logger.Info(
                    $"Gemini temporary alternate {modelName} succeeded " +
                    $"({_alternateChecks}/{PrimaryProbeAfterAlternateChecks} checks; " +
                    $"{_alternateSuccesses} successes); " +
                    (_alternateChecks >= PrimaryProbeAfterAlternateChecks
                        ? $"the next online check will probe configured primary {_primaryModelName}"
                        : "the alternate will handle one more online check before probing the primary"));
            }
        }

        private void RecordModelFailure(string modelName, string reason)
        {
            lock (_failoverGate)
            {
                if (string.Equals(modelName, _primaryModelName, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(modelName, _alternateModelName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _alternateChecks = Math.Min(
                    PrimaryProbeAfterAlternateChecks,
                    _alternateChecks + 1);
                Logger.Info(
                    $"Gemini temporary alternate {modelName} completed with {reason} " +
                    $"({_alternateChecks}/{PrimaryProbeAfterAlternateChecks} checks); " +
                    (_alternateChecks >= PrimaryProbeAfterAlternateChecks
                        ? $"the next online check will probe configured primary {_primaryModelName}"
                        : "the alternate may handle one more online check before probing the primary"));
            }
        }

        private static bool HasAttemptBudget(Stopwatch stopwatch, TimeSpan delay)
        {
            return RequestTimeout - stopwatch.Elapsed - delay >= MinimumAttemptBudget;
        }

        private static AnalysisAttemptDiagnostic CreateDiagnostic(
            int attempt,
            string model,
            HttpStatusCode? statusCode,
            AnalysisFailureCategory category,
            long durationMilliseconds,
            string outcome)
        {
            return new AnalysisAttemptDiagnostic
            {
                Attempt = Math.Max(1, attempt),
                Model = model,
                HttpStatus = statusCode.HasValue ? (int)statusCode.Value : null,
                FailureCategory = category,
                DurationMilliseconds = Math.Max(0, durationMilliseconds),
                Outcome = outcome
            };
        }

        private static OnlineAnalysisAttempt BuildDiagnosticFailure(
            IReadOnlyList<AnalysisAttemptDiagnostic> diagnostics,
            string currentModel,
            int attempts,
            long latencyMilliseconds,
            string reason,
            AnalysisFailureCategory fallbackCategory = AnalysisFailureCategory.Unknown,
            int? fallbackHttpStatus = null)
        {
            // A concrete provider response is more informative than a later budget timeout.
            // In particular, retain the HTTP 503 that caused failover instead of reporting
            // only a generic timeout from the final attempt.
            var strongest = diagnostics.LastOrDefault(item => item.HttpStatus == 503)
                ?? diagnostics.LastOrDefault();
            var category = strongest?.FailureCategory ?? fallbackCategory;
            if (category == AnalysisFailureCategory.None)
            {
                category = fallbackCategory;
            }
            var httpStatus = strongest?.HttpStatus ?? fallbackHttpStatus;
            var model = strongest?.Model ?? currentModel;
            var providerFailureCode = category switch
            {
                AnalysisFailureCategory.ServiceUnavailable => "service_unavailable",
                AnalysisFailureCategory.Timeout => "timeout",
                AnalysisFailureCategory.Network => "network_error",
                AnalysisFailureCategory.ModelUnavailable => "model_unavailable",
                _ => null
            };

            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    "Gemini",
                    model,
                    category,
                    Math.Max(attempts, diagnostics.Count),
                    latencyMilliseconds,
                    httpStatus,
                    providerFailureCode: providerFailureCode,
                    attemptDiagnostics: diagnostics),
                reason);
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 408 || code == 429 || code == 500 || code == 502 || code == 503 || code == 504;
        }

        private OnlineAnalysisAttempt BuildQuotaFailure(
            string modelName,
            GeminiQuotaCircuitState circuit,
            int attempts,
            long latencyMilliseconds,
            int? httpStatus,
            bool requestSuppressed,
            IReadOnlyList<AnalysisAttemptDiagnostic>? diagnostics = null)
        {
            var retryAfterUtc = circuit.RetryAfterUtc.UtcDateTime;
            var action = requestSuppressed ? "API request skipped" : "API request rejected";
            var reason =
                $"Gemini quota unavailable; {action}; next online attempt after " +
                $"{retryAfterUtc:O}";

            if (requestSuppressed)
            {
                Logger.Info(
                    $"Gemini quota circuit active; API request skipped until " +
                    $"{circuit.RetryAfterUtc:O} (consecutive quota failures " +
                    $"{circuit.ConsecutiveFailures})");
            }

            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    "Gemini",
                    modelName,
                    AnalysisFailureCategory.QuotaExhausted,
                    attempts,
                    latencyMilliseconds,
                    httpStatus,
                    providerFailureCode: circuit.ProviderFailureCode,
                    retryAfterUtc: retryAfterUtc,
                    quotaMetric: circuit.QuotaMetric,
                    quotaId: circuit.QuotaId,
                    consecutiveQuotaFailures: circuit.ConsecutiveFailures,
                    requestSuppressed: requestSuppressed,
                    attemptDiagnostics: diagnostics),
                reason);
        }

        private OnlineAnalysisAttempt BuildScheduledLocal(
            long requestSequence,
            long latencyMilliseconds)
        {
            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    "Gemini",
                    _primaryModelName,
                    AnalysisFailureCategory.ScheduledLocal,
                    attempts: 0,
                    latencyMilliseconds,
                    providerFailureCode: "scheduled_local",
                    requestSuppressed: true,
                    requestEveryChecks: _requestEveryChecks,
                    requestSequence: requestSequence),
                $"Gemini pacing policy uses local analysis on this check; " +
                $"online request runs every {_requestEveryChecks} checks");
        }

        private static TimeSpan GetTransientRetryDelay(
            HttpResponseMessage? response,
            string? responseBody,
            int attempt)
        {
            var maximumMonitorDelay = TimeSpan.FromSeconds(30);
            var serverDelay = GetServerRetryDelay(response, responseBody, DateTimeOffset.UtcNow);
            if (serverDelay.HasValue)
            {
                return serverDelay.Value > maximumMonitorDelay
                    ? maximumMonitorDelay
                    : serverDelay.Value;
            }

            // Exponential backoff with jitter for network errors and provider 5xx responses.
            // Explicit QuotaFailure responses never enter this loop.
            var exponentialSeconds = Math.Min(8.0, Math.Pow(2.0, attempt - 1));
            var jitterMilliseconds = Random.Shared.Next(150, 651);
            return TimeSpan.FromSeconds(exponentialSeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
        }

        private static TimeSpan? GetServerRetryDelay(
            HttpResponseMessage? response,
            string? responseBody,
            DateTimeOffset nowUtc)
        {
            var retryAfter = response?.Headers.RetryAfter;
            if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (retryAfter?.Date is DateTimeOffset retryDate)
            {
                var requestedDelay = retryDate - nowUtc;
                if (requestedDelay > TimeSpan.Zero)
                {
                    return requestedDelay;
                }
            }

            var parsed = GeminiQuotaParser.Parse(
                response?.StatusCode ?? (HttpStatusCode)0,
                responseBody);
            return parsed.RetryDelay;
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
