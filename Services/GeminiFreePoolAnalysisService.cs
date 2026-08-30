using AIWeather.Models;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Best-effort Gemini free-tier pool. It walks the operator-ordered model list for the
    /// configured number of complete cycles. Each model owns an independent quota circuit;
    /// a paused model is skipped without issuing a request. No ordering or downgrade policy
    /// from this class is ever applied to the billed Gemini provider.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public sealed class GeminiFreePoolAnalysisService : IOnlineWeatherAnalysisService
    {
        private static readonly TimeSpan PerModelRequestTimeout = TimeSpan.FromSeconds(20);

        private readonly string _apiKey;
        private readonly IReadOnlyList<string> _models;
        private readonly int _cycles;
        private readonly int _requestEveryChecks;
        private readonly IHttpClientProvider _httpProvider;
        private readonly Func<string, GeminiQuotaCircuitBreaker> _quotaCircuitForModel;
        private readonly Func<DateTimeOffset> _utcNow;
        private readonly Dictionary<string, GeminiAnalysisService> _services =
            new Dictionary<string, GeminiAnalysisService>(StringComparer.OrdinalIgnoreCase);
        private long _requestSequence;
        private bool _isInitialized;

        public GeminiFreePoolAnalysisService(
            string apiKey,
            IEnumerable<string> orderedModels,
            int cycles = 2,
            int requestEveryChecks = 1)
            : this(
                apiKey,
                orderedModels,
                cycles,
                requestEveryChecks,
                new SystemProxyAwareHttpClientProvider(),
                model => GeminiQuotaCircuitRegistry.Get(apiKey, model),
                () => DateTimeOffset.UtcNow)
        {
        }

        internal GeminiFreePoolAnalysisService(
            string apiKey,
            IEnumerable<string> orderedModels,
            int cycles,
            int requestEveryChecks,
            IHttpClientProvider httpProvider,
            Func<string, GeminiQuotaCircuitBreaker> quotaCircuitForModel,
            Func<DateTimeOffset> utcNow)
        {
            _apiKey = apiKey?.Trim() ?? string.Empty;
            _models = GeminiProviderProfile.ParseFreeModelOrder(
                string.Join("\n", orderedModels ?? Array.Empty<string>()));
            _cycles = Math.Clamp(cycles, 1, 10);
            _requestEveryChecks = Math.Clamp(requestEveryChecks, 1, 10000);
            _httpProvider = httpProvider ?? throw new ArgumentNullException(nameof(httpProvider));
            _quotaCircuitForModel = quotaCircuitForModel
                ?? throw new ArgumentNullException(nameof(quotaCircuitForModel));
            _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || _models.Count == 0)
            {
                Logger.Warning("Gemini Free pool is missing an API key or model order");
                _isInitialized = false;
                return false;
            }

            foreach (var model in _models)
            {
                var service = new GeminiAnalysisService(
                    _apiKey,
                    model,
                    _httpProvider,
                    _quotaCircuitForModel,
                    _utcNow,
                    requestEveryChecks: 1,
                    serviceTier: GeminiServiceTier.Free,
                    requestTimeout: PerModelRequestTimeout);
                if (!await service.InitializeAsync(cancellationToken))
                {
                    _isInitialized = false;
                    return false;
                }
                _services[model] = service;
            }

            _isInitialized = true;
            Logger.Info(
                $"Gemini Free ordered pool initialized: {_models.Count} models, {_cycles} cycles, " +
                $"{PerModelRequestTimeout.TotalSeconds:F0}s per-model request bound");
            return true;
        }

        public async Task<WeatherAnalysisResult> AnalyzeImageAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            var attempt = await TryAnalyzeOnlineOnlyAsync(image, astroContext, cancellationToken);
            if (attempt.Success && attempt.Result != null)
            {
                return attempt.Result;
            }

            return new WeatherAnalysisResult
            {
                Timestamp = DateTime.UtcNow,
                Condition = WeatherCondition.Unknown,
                CloudCoverage = 50,
                Confidence = 0,
                IsSafeForImaging = false,
                Description = $"Gemini Free pool failed: {attempt.Provenance.FailureCategory}",
                Provenance = attempt.Provenance.Clone()
            };
        }

        public async Task<OnlineAnalysisAttempt> TryAnalyzeOnlineOnlyAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var requestSequence = Interlocked.Increment(ref _requestSequence);
            if (!_isInitialized)
            {
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini Free",
                        "ordered-pool",
                        AnalysisFailureCategory.Authentication,
                        0,
                        stopwatch.ElapsedMilliseconds),
                    "Gemini Free API key is missing or the ordered pool was not initialized");
            }

            if (_requestEveryChecks > 1
                && (requestSequence - 1) % _requestEveryChecks != 0)
            {
                return OnlineAnalysisAttempt.Failed(
                    AnalysisMetadata.FailedOnline(
                        AnalysisOrigin.Gemini,
                        "Gemini Free",
                        "ordered-pool",
                        AnalysisFailureCategory.ScheduledLocal,
                        0,
                        stopwatch.ElapsedMilliseconds,
                        providerFailureCode: "scheduled_local",
                        requestSuppressed: true,
                        requestEveryChecks: _requestEveryChecks,
                        requestSequence: requestSequence),
                    $"Gemini Free pool runs every {_requestEveryChecks} weather checks");
            }

            var diagnostics = new List<AnalysisAttemptDiagnostic>();
            var failures = new List<AnalysisProvenance>();
            var actualRequests = 0;
            var poolPosition = 0;

            for (var cycle = 1; cycle <= _cycles; cycle++)
            {
                Logger.Info($"Gemini Free pool cycle {cycle}/{_cycles} started");
                foreach (var model in _models)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    poolPosition++;
                    var circuit = _quotaCircuitForModel(model);
                    if (circuit.TryGetActive(_utcNow(), out var activeQuota))
                    {
                        failures.Add(AnalysisMetadata.FailedOnline(
                            AnalysisOrigin.Gemini,
                            "Gemini Free",
                            model,
                            AnalysisFailureCategory.QuotaExhausted,
                            0,
                            stopwatch.ElapsedMilliseconds,
                            providerFailureCode: activeQuota.ProviderFailureCode,
                            retryAfterUtc: activeQuota.RetryAfterUtc.UtcDateTime,
                            quotaMetric: activeQuota.QuotaMetric,
                            quotaId: activeQuota.QuotaId,
                            consecutiveQuotaFailures: activeQuota.ConsecutiveFailures,
                            requestSuppressed: true));
                        diagnostics.Add(new AnalysisAttemptDiagnostic
                        {
                            Attempt = poolPosition,
                            Model = model,
                            FailureCategory = AnalysisFailureCategory.QuotaExhausted,
                            DurationMilliseconds = 0,
                            Outcome = "quota_circuit_skipped"
                        });
                        Logger.Info(
                            $"Gemini Free model {model} skipped by its quota circuit until " +
                            $"{activeQuota.RetryAfterUtc:O}");
                        continue;
                    }

                    Logger.Info(
                        $"Gemini Free pool trying {model} (cycle {cycle}/{_cycles}, " +
                        $"position {poolPosition}/{_models.Count * _cycles})");
                    var service = _services[model];
                    var attempt = await service.TryAnalyzeOnlineOnlyAsync(
                        image,
                        astroContext,
                        cancellationToken);
                    actualRequests += Math.Max(0, attempt.Provenance.Attempts);
                    AppendDiagnostics(diagnostics, attempt.Provenance, poolPosition);

                    if (attempt.Success && attempt.Result != null)
                    {
                        attempt.Result.Provenance.Provider = "Gemini Free";
                        attempt.Result.Provenance.Attempts = actualRequests;
                        attempt.Result.Provenance.LatencyMilliseconds = stopwatch.ElapsedMilliseconds;
                        attempt.Result.Provenance.AttemptDiagnostics =
                            diagnostics.Select(item => item.Clone()).ToArray();
                        Logger.Info(
                            $"Gemini Free pool succeeded with {model} during cycle {cycle}/{_cycles}");
                        return OnlineAnalysisAttempt.Succeeded(attempt.Result);
                    }

                    failures.Add(attempt.Provenance.Clone());
                }
            }

            var strongest = SelectStrongestFailure(failures);
            var failureCategory = strongest?.FailureCategory ?? AnalysisFailureCategory.Unknown;
            var failureModel = strongest?.Model ?? "ordered-pool";
            var retryAfterUtc = failures
                .Where(item => item.RetryAfterUtc.HasValue)
                .Select(item => item.RetryAfterUtc)
                .OrderBy(value => value)
                .FirstOrDefault();
            var allSuppressed = actualRequests == 0;

            Logger.Warning(
                $"Gemini Free pool exhausted {_cycles} complete cycle(s) across {_models.Count} " +
                $"models; actual API requests {actualRequests}, quota skips " +
                $"{diagnostics.Count(item => item.Outcome == "quota_circuit_skipped")}");

            return OnlineAnalysisAttempt.Failed(
                AnalysisMetadata.FailedOnline(
                    AnalysisOrigin.Gemini,
                    "Gemini Free",
                    failureModel,
                    failureCategory,
                    actualRequests,
                    stopwatch.ElapsedMilliseconds,
                    strongest?.HttpStatus,
                    providerFailureCode: strongest?.ProviderFailureCode ?? "free_pool_exhausted",
                    retryAfterUtc: retryAfterUtc,
                    quotaMetric: strongest?.QuotaMetric,
                    quotaId: strongest?.QuotaId,
                    consecutiveQuotaFailures: strongest?.ConsecutiveQuotaFailures ?? 0,
                    requestSuppressed: allSuppressed,
                    requestEveryChecks: _requestEveryChecks,
                    requestSequence: requestSequence,
                    attemptDiagnostics: diagnostics),
                $"Gemini Free ordered pool exhausted {_cycles} cycle(s) without an available model");
        }

        private static void AppendDiagnostics(
            ICollection<AnalysisAttemptDiagnostic> destination,
            AnalysisProvenance provenance,
            int poolPosition)
        {
            if (provenance.AttemptDiagnostics.Count == 0)
            {
                destination.Add(new AnalysisAttemptDiagnostic
                {
                    Attempt = poolPosition,
                    Model = provenance.Model,
                    HttpStatus = provenance.HttpStatus,
                    FailureCategory = provenance.FailureCategory,
                    DurationMilliseconds = provenance.LatencyMilliseconds,
                    Outcome = provenance.RequestSuppressed ? "suppressed" : "failed"
                });
                return;
            }

            foreach (var item in provenance.AttemptDiagnostics)
            {
                var clone = item.Clone();
                clone.Attempt = poolPosition;
                destination.Add(clone);
            }
        }

        private static AnalysisProvenance? SelectStrongestFailure(
            IEnumerable<AnalysisProvenance> failures)
        {
            return failures
                .OrderByDescending(item => FailurePriority(item.FailureCategory))
                .ThenByDescending(item => item.HttpStatus == 503)
                .FirstOrDefault();
        }

        private static int FailurePriority(AnalysisFailureCategory category) => category switch
        {
            AnalysisFailureCategory.Authentication => 100,
            AnalysisFailureCategory.ServiceUnavailable => 90,
            AnalysisFailureCategory.Timeout => 80,
            AnalysisFailureCategory.Network => 70,
            AnalysisFailureCategory.QuotaExhausted => 60,
            AnalysisFailureCategory.RateLimited => 60,
            AnalysisFailureCategory.ModelUnavailable => 50,
            AnalysisFailureCategory.SchemaRejected => 40,
            AnalysisFailureCategory.MalformedResponse => 40,
            _ => 10
        };
    }
}
