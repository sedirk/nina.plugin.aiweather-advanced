using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.DatasetSmoke;

internal static class Program
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> Main()
    {
        var runRoot = Path.Combine(
            Path.GetTempPath(),
            "AIWeatherDatasetSmoke",
            DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
        Directory.CreateDirectory(runRoot);

        try
        {
            VerifyLocalizationSelection();
            VerifySolarAltitudeGuard();
            VerifyDatasetDefaults();
            VerifyGeminiQuotaPolicy();
            await VerifyGeminiRequestPacingAsync();
            await VerifyGeminiServiceSuppressesQuotaRetriesAsync();
            await VerifyQuotaFallbackMetadataAsync();
            await VerifyTrainableDedupPrivacyAndManualReviewAsync(
                Path.Combine(runRoot, "main"));
            await VerifyInvalidTeacherGoesToQuarantineAsync(
                Path.Combine(runRoot, "quarantine"));
            await VerifyQuotaStopsCollectionAsync(
                Path.Combine(runRoot, "quota"));

            Console.WriteLine($"PASS dataset smoke suite: {runRoot}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL dataset smoke suite: {ex}");
            Console.Error.WriteLine($"Artifacts retained at: {runRoot}");
            return 1;
        }
    }

    private static void VerifySolarAltitudeGuard()
    {
        var daytime = new AstroContext { SunAltitude = 12.5 };
        var twilight = new AstroContext { SunAltitude = -6.0 };
        var night = new AstroContext { SunAltitude = -6.1 };

        Assert(!SolarAltitudeGuard.Evaluate(false, -6, daytime).ShouldSuspend,
            "Disabled Sun-altitude guard blocked analysis");
        Assert(SolarAltitudeGuard.Evaluate(true, -6, daytime).ShouldSuspend,
            "Daytime Sun altitude did not block analysis");
        Assert(SolarAltitudeGuard.Evaluate(true, -6, twilight).ShouldSuspend,
            "The configured Sun-altitude boundary must remain suspended");
        Assert(!SolarAltitudeGuard.Evaluate(true, -6, night).ShouldSuspend,
            "Sun below the configured altitude did not release analysis");

        var missing = SolarAltitudeGuard.Evaluate(true, -6, context: null);
        Assert(missing.ShouldSuspend && !missing.HasAstronomicalContext,
            "Missing astronomical context must fail closed when the guard is enabled");
        Assert(SolarAltitudeGuard.NormalizeLimit(double.NaN) == SolarAltitudeGuard.DefaultLimitDegrees,
            "Non-finite Sun-altitude limit did not fall back to the default");
        Assert(SolarAltitudeGuard.NormalizeLimit(120) == 90,
            "Sun-altitude limit was not clamped to the physical range");
    }

    private static void VerifyDatasetDefaults()
    {
        var options = DatasetRecorderOptions.FromSettings();
        Assert(options.PeriodicEveryChecks == 1,
            "Default periodic dataset ratio must sample every successful teacher check");
        Assert(Math.Abs(options.ImageScalePercent - 50) < 0.001,
            "Default training-image downscale must be 50 percent");
        Assert(Properties.Settings.Default.GeminiRequestEveryChecks == 1,
            "Default Gemini request pacing must preserve one online call per weather check");
    }

    private static void VerifyLocalizationSelection()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
            Assert(UiLocalization.Text("Preview.ActivityLog") == "Activity Log",
                "English localization was not selected for en-GB");
            Assert(UiLocalization.ReviewStatus(DatasetReviewStatuses.Accepted) == "Accepted",
                "English review status localization failed");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert(UiLocalization.Text("Preview.ActivityLog") == "活动日志",
                "Chinese localization was not selected for zh-CN");
            var fallback = new WeatherAnalysisResult
            {
                Condition = WeatherCondition.Overcast,
                CloudCoverage = 100,
                Provenance = new AnalysisProvenance
                {
                    Provider = "Gemini",
                    Origin = AnalysisOrigin.LocalHeuristic,
                    IsFallback = true,
                    FailureCategory = AnalysisFailureCategory.Timeout
                }
            };
            fallback.Provenance.Provider = "Local";
            Assert(UiLocalization.AnalysisDescription(fallback, "Gemini") == "[回退：本地] Gemini 失败（超时）。阴天——云量 100.0%",
                "Fallback analysis description was not localized for zh-CN");
            Assert(UiLocalization.Condition(WeatherCondition.MostlyCloudy) == "大部多云",
                "Chinese weather condition localization failed");
            Assert(UiLocalization.ReviewStatus(DatasetReviewStatuses.Accepted) == "已接受",
                "Chinese review status localization failed");

            fallback.Provenance.FailureCategory = AnalysisFailureCategory.QuotaExhausted;
            fallback.Provenance.RetryAfterUtc = DateTime.UtcNow.AddMinutes(10);
            var quotaDescription = UiLocalization.AnalysisDescription(fallback, "Gemini");
            Assert(quotaDescription.Contains("Gemini API 配额暂不可用", StringComparison.Ordinal)
                   && quotaDescription.Contains("下次在线尝试时间", StringComparison.Ordinal),
                "Gemini quota fallback description was not localized for zh-CN");
            Assert(UiLocalization.FallbackStatus(fallback.Provenance).Contains("API 配额暂停至", StringComparison.Ordinal),
                "Gemini quota source summary was not localized for zh-CN");

            fallback.Provenance.FailureCategory = AnalysisFailureCategory.ScheduledLocal;
            fallback.Provenance.RequestEveryChecks = 12;
            var scheduledDescription = UiLocalization.AnalysisDescription(fallback, "Gemini");
            Assert(scheduledDescription.Contains("每 12 次天气检查在线调用一次", StringComparison.Ordinal)
                   && scheduledDescription.Contains("本次使用本地分析", StringComparison.Ordinal),
                "Gemini scheduled-local description was not localized for zh-CN");
            Assert(UiLocalization.FallbackStatus(fallback.Provenance).Contains("Gemini 每 12 次调用一次", StringComparison.Ordinal),
                "Gemini scheduled-local source summary was not localized for zh-CN");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert(UiLocalization.Text("Preview.ActivityLog") == "Activity Log",
                "Unsupported cultures must fall back to English");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static void VerifyGeminiQuotaPolicy()
    {
        const string quotaJson = """
        {
          "error": {
            "code": 429,
            "message": "You exceeded your current quota. Please retry in 57.57808206s.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "generativelanguage.googleapis.com/generate_content_free_tier_requests",
                    "quotaId": "GenerateRequestsPerDayPerProjectPerModel-FreeTier"
                  }
                ]
              },
              {
                "@type": "type.googleapis.com/google.rpc.RetryInfo",
                "retryDelay": "57.578082060s"
              }
            ]
          }
        }
        """;

        var quota = GeminiQuotaParser.Parse((HttpStatusCode)429, quotaJson);
        Assert(quota.IsQuotaFailure, "Gemini QuotaFailure envelope was not recognized");
        Assert(quota.ProviderFailureCode == "quota_exhausted",
            "Gemini quota provider failure code is unstable");
        Assert(quota.QuotaMetric?.EndsWith("generate_content_free_tier_requests", StringComparison.Ordinal) == true,
            "Gemini quota metric was not preserved");
        Assert(quota.QuotaId?.Contains("PerDay", StringComparison.Ordinal) == true,
            "Gemini quota id was not preserved");
        Assert(quota.IsDailyQuota, "Gemini per-day quota identity was not recognized");
        Assert(quota.RetryDelay.HasValue
               && Math.Abs(quota.RetryDelay.Value.TotalSeconds - 57.578082060) < 0.001,
            "Gemini RetryInfo duration was not parsed without truncation");

        var generic429 = GeminiQuotaParser.Parse(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":429,\"message\":\"Too many requests\"}}");
        Assert(!generic429.IsQuotaFailure,
            "A generic HTTP 429 was incorrectly promoted to an explicit quota failure");

        var currentDailyCode = GeminiQuotaParser.Parse(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":\"quota_exceeded\",\"message\":\"Daily quota exhausted\"}}");
        Assert(currentDailyCode.IsQuotaFailure && currentDailyCode.IsDailyQuota,
            "The current Gemini quota_exceeded machine code was not treated as daily quota");

        var currentRateCode = GeminiQuotaParser.Parse(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Too many requests\"}}");
        Assert(!currentRateCode.IsQuotaFailure,
            "The current Gemini rate_limit_exceeded code was incorrectly treated as daily quota");

        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var dailyBreaker = new GeminiQuotaCircuitBreaker();
        var daily = dailyBreaker.RecordFailure(now, quota);
        Assert(daily.IsDailyQuota, "Daily Gemini quota state was not retained");
        Assert(daily.RetryAfterUtc == new DateTimeOffset(2026, 8, 23, 7, 2, 0, TimeSpan.Zero),
            "Daily Gemini quota did not pause until Pacific midnight plus the safety margin");

        var shortQuota = new GeminiQuotaInfo
        {
            IsQuotaFailure = true,
            ProviderFailureCode = "quota_exhausted",
            QuotaMetric = quota.QuotaMetric,
            QuotaId = "GenerateRequestsPerMinutePerProjectPerModel-FreeTier",
            RetryDelay = quota.RetryDelay,
            IsDailyQuota = false
        };
        var breaker = new GeminiQuotaCircuitBreaker();
        var first = breaker.RecordFailure(now, shortQuota);
        Assert(Math.Abs((first.RetryAfterUtc - now).TotalSeconds - 57.578082060) < 0.001,
            "First Gemini quota failure did not honor the full provider RetryInfo");
        Assert(breaker.TryGetActive(now.AddSeconds(30), out _),
            "Gemini quota circuit did not suppress a premature API request");

        var secondAt = first.RetryAfterUtc.AddSeconds(1);
        var second = breaker.RecordFailure(secondAt, shortQuota);
        Assert(second.RetryAfterUtc - secondAt >= TimeSpan.FromMinutes(10),
            "Second consecutive quota failure did not escalate to 10 minutes");

        var thirdAt = second.RetryAfterUtc.AddSeconds(1);
        var third = breaker.RecordFailure(thirdAt, shortQuota);
        Assert(third.RetryAfterUtc - thirdAt >= TimeSpan.FromMinutes(30),
            "Third consecutive quota failure did not escalate to 30 minutes");

        var fourthAt = third.RetryAfterUtc.AddSeconds(1);
        var fourth = breaker.RecordFailure(fourthAt, shortQuota);
        Assert(fourth.RetryAfterUtc - fourthAt >= TimeSpan.FromMinutes(60),
            "Fourth consecutive quota failure did not escalate to 60 minutes");
        Assert(breaker.Reset(), "Gemini quota circuit did not report a state reset");
        Assert(!breaker.TryGetActive(fourthAt, out _),
            "Gemini quota circuit remained open after a successful-response reset");
    }

    private static async Task VerifyQuotaFallbackMetadataAsync()
    {
        var retryAfterUtc = DateTime.UtcNow.AddMinutes(10);
        var orchestrator = new WeatherAnalysisOrchestrator();
        using var frame = CreateFrame();
        var bundle = await orchestrator.AnalyzeAsync(
            new QuotaTeacher(retryAfterUtc),
            frame,
            astroContext: null,
            CancellationToken.None);

        Assert(bundle.UsedFallback, "Quota-suppressed Gemini teacher did not select local fallback");
        Assert(bundle.EffectiveResult.Provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted,
            "Quota failure category was not copied to the effective local result");
        Assert(bundle.EffectiveResult.Provenance.RetryAfterUtc == retryAfterUtc,
            "Quota retry timestamp was not copied to the effective local result");
        Assert(bundle.EffectiveResult.Provenance.RequestSuppressed,
            "Quota request-suppression state was not copied to the effective local result");
        Assert(bundle.EffectiveResult.Provenance.QuotaId == "test-daily-quota",
            "Quota identity was not copied to the effective local result");
    }

    private static async Task VerifyGeminiServiceSuppressesQuotaRetriesAsync()
    {
        const string quotaJson = """
        {
          "error": {
            "code": 429,
            "message": "Quota exceeded. Please retry in 57.5s.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "test.requests",
                    "quotaId": "GenerateRequestsPerMinutePerProjectPerModel-FreeTier"
                  }
                ]
              },
              {
                "@type": "type.googleapis.com/google.rpc.RetryInfo",
                "retryDelay": "57.5s"
              }
            ]
          }
        }
        """;

        var handler = new QuotaResponseHandler(quotaJson);
        using var http = new HttpClient(handler);
        var breaker = new GeminiQuotaCircuitBreaker();
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test",
            http,
            breaker,
            () => now);
        Assert(await service.InitializeAsync(), "Injected Gemini service failed to initialize");

        using var frame = CreateFrame();
        var first = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(!first.Success
               && first.Provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted,
            "Gemini service did not expose an explicit quota failure");
        Assert(first.Provenance.Attempts == 1 && handler.RequestCount == 1,
            "Gemini service retried an explicit quota response within the same weather check");
        Assert(!first.Provenance.RequestSuppressed && first.Provenance.HttpStatus == 429,
            "The quota-triggering HTTP request was incorrectly marked as suppressed");

        var suppressed = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(!suppressed.Success
               && suppressed.Provenance.RequestSuppressed
               && suppressed.Provenance.Attempts == 0,
            "Gemini service did not mark an open-circuit check as request-suppressed");
        Assert(handler.RequestCount == 1,
            "Gemini service sent traffic while its quota circuit was open");

        now = first.Provenance.RetryAfterUtc!.Value.AddSeconds(1);
        var secondFailure = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(handler.RequestCount == 2 && secondFailure.Provenance.Attempts == 1,
            "Gemini service did not perform exactly one probe after the provider delay expired");
        Assert(secondFailure.Provenance.RetryAfterUtc - now.UtcDateTime >= TimeSpan.FromMinutes(10),
            "Repeated Gemini quota rejection did not escalate the service circuit to 10 minutes");
    }

    private static async Task VerifyGeminiRequestPacingAsync()
    {
        const string successfulEnvelope = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "{\"condition\":\"Clear\",\"cloudCoverage\":5,\"rainDetected\":false,\"fogDetected\":false,\"isSafe\":true,\"description\":\"Clear sky\",\"confidence\":95}"
                  }
                ]
              }
            }
          ]
        }
        """;

        var handler = new StaticResponseHandler(HttpStatusCode.OK, successfulEnvelope);
        using var http = new HttpClient(handler);
        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test",
            http,
            new GeminiQuotaCircuitBreaker(),
            () => new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            requestEveryChecks: 3);
        Assert(await service.InitializeAsync(), "Paced Gemini service failed to initialize");

        using var frame = CreateFrame();
        var first = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var second = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var third = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var fourth = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var fifth = await service.TryAnalyzeOnlineOnlyAsync(frame);

        Assert(first.Success && fourth.Success,
            "Gemini pacing did not run online on checks 1 and 4 for N=3");
        Assert(!second.Success && !third.Success && !fifth.Success,
            "Gemini pacing unexpectedly called online between scheduled checks");
        Assert(second.Provenance.FailureCategory == AnalysisFailureCategory.ScheduledLocal
               && second.Provenance.RequestSuppressed
               && second.Provenance.RequestEveryChecks == 3
               && second.Provenance.RequestSequence == 2,
            "Gemini pacing metadata did not explain the scheduled local check");
        Assert(handler.RequestCount == 2,
            "Gemini pacing sent an unexpected number of HTTP requests");

        var probeHandler = new StaticResponseHandler(HttpStatusCode.OK, successfulEnvelope);
        using var probeHttp = new HttpClient(probeHandler);
        var probeBreaker = new GeminiQuotaCircuitBreaker();
        var probeNow = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var probeService = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test",
            probeHttp,
            probeBreaker,
            () => probeNow,
            requestEveryChecks: 3);
        Assert(await probeService.InitializeAsync(), "Quota-probe Gemini service failed to initialize");
        Assert((await probeService.TryAnalyzeOnlineOnlyAsync(frame)).Success,
            "Quota-probe setup call failed");

        probeBreaker.RecordFailure(probeNow, new GeminiQuotaInfo
        {
            IsQuotaFailure = true,
            ProviderFailureCode = "quota_exhausted",
            QuotaId = "GenerateRequestsPerMinutePerProjectPerModel-FreeTier",
            RetryDelay = TimeSpan.FromMinutes(1),
            IsDailyQuota = false
        });
        probeNow = probeNow.AddMinutes(2);
        var forcedProbe = await probeService.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(forcedProbe.Success && probeHandler.RequestCount == 2,
            "An expired Gemini quota pause did not force an immediate probe between paced calls");
    }

    private static async Task VerifyTrainableDedupPrivacyAndManualReviewAsync(string root)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "orphan.tmp"), "interrupted write");

        var options = CreateOptions(root);
        var recorder = new TeacherStudentDatasetRecorder(() => options);
        var captured = DateTime.UtcNow;
        using var frame = CreateFrame();
        var bundle = CreateSuccessfulBundle();
        var astro = new AstroContext
        {
            UtcTime = captured,
            Latitude = 39.9042,
            Longitude = 116.4074,
            Elevation = 50,
            SunAltitude = 15,
            SunState = "Day",
            MoonAltitude = -20,
            MoonIllumination = 30,
            MoonPhase = "Waxing Crescent"
        };

        Assert(recorder.TryEnqueue(
            frame, captured, astro, bundle,
            effectiveSafe: true, visualSafe: true, externalSafetyMonitorSafe: null,
            highThreshold: 50, lowThreshold: 40), "initial sample was not accepted");
        await WaitForRecorderAsync(recorder, status => status.TotalSamples == 1, "first sample");

        var persistentDisagreement = CreateSuccessfulBundle();
        persistentDisagreement.Student.Condition = WeatherCondition.Overcast;
        persistentDisagreement.Student.CloudCoverage = 100;
        persistentDisagreement.Student.IsSafeForImaging = false;
        Assert(recorder.TryEnqueue(
            frame, captured.AddSeconds(30), astro, persistentDisagreement,
            effectiveSafe: true, visualSafe: true, externalSafetyMonitorSafe: null,
            highThreshold: 50, lowThreshold: 40),
            "persistent disagreement event was not accepted by queue");
        await WaitForRecorderAsync(recorder, status => status.DroppedSamples >= 1,
            "near-duplicate disagreement drop");
        Assert(recorder.Status.TotalSamples == 1,
            "persistent disagreement bypassed near-duplicate protection");

        Assert(recorder.TryEnqueue(
            frame, captured.AddMinutes(2), astro, bundle,
            effectiveSafe: true, visualSafe: true, externalSafetyMonitorSafe: null,
            highThreshold: 50, lowThreshold: 40), "periodic duplicate was not accepted by queue");
        await WaitForRecorderAsync(recorder, status => status.DroppedSamples >= 2, "near-duplicate drop");
        Assert(recorder.Status.TotalSamples == 1, "periodic near-duplicate created a label");

        Assert(recorder.TryEnqueue(
            frame, captured.AddMinutes(4), astro, bundle,
            effectiveSafe: true, visualSafe: true, externalSafetyMonitorSafe: null,
            highThreshold: 50, lowThreshold: 40, manualReview: true),
            "manual review sample was not accepted");
        await WaitForRecorderAsync(recorder, status => status.TotalSamples == 2, "manual review sample");
        await recorder.StopAsync(TimeSpan.FromSeconds(10));

        var images = Directory.GetFiles(Path.Combine(root, "images"), "*.jpg", SearchOption.AllDirectories);
        var labels = Directory.GetFiles(Path.Combine(root, "labels"), "*.json", SearchOption.AllDirectories);
        var index = Directory.GetFiles(Path.Combine(root, "index"), "*.jsonl", SearchOption.TopDirectoryOnly);
        Assert(images.Length == 1, $"content-addressed dedup expected 1 image, found {images.Length}");
        Assert(labels.Length == 2, $"expected 2 labels, found {labels.Length}");
        Assert(index.Length == 1, "monthly JSONL index missing");

        var allLabelText = string.Join("\n", labels.Select(File.ReadAllText));
        Assert(!allLabelText.Contains("39.9042", StringComparison.Ordinal), "latitude leaked");
        Assert(!allLabelText.Contains("116.4074", StringComparison.Ordinal), "longitude leaked");
        Assert(!allLabelText.Contains("super-secret", StringComparison.Ordinal), "raw secret leaked");
        Assert(!allLabelText.Contains("camera-password", StringComparison.Ordinal), "RTSP password leaked");
        Assert(allLabelText.Contains("[REDACTED]", StringComparison.Ordinal)
               || allLabelText.Contains("***", StringComparison.Ordinal),
               "sanitized raw response did not contain a redaction marker");

        using (var labelDocument = JsonDocument.Parse(File.ReadAllText(labels[0])))
        {
            var image = labelDocument.RootElement.GetProperty("image");
            Assert(image.GetProperty("sourceWidth").GetInt32() == 800
                   && image.GetProperty("sourceHeight").GetInt32() == 450,
                "source image dimensions were not preserved in metadata");
            Assert(image.GetProperty("width").GetInt32() == 640
                   && image.GetProperty("height").GetInt32() == 360,
                "80% image downsampling produced the wrong dimensions");
            Assert(Math.Abs(image.GetProperty("scalePercent").GetDouble() - 80) < 0.01,
                "actual image scale percentage was not recorded");
        }

        foreach (var line in File.ReadLines(index[0]))
        {
            using var _ = JsonDocument.Parse(line);
        }

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "dataset.json")));
        Assert(manifest.RootElement.GetProperty("schemaVersion").GetInt32() == 1,
            "manifest schema mismatch");
        Assert(Directory.GetFiles(
            Path.Combine(root, "quarantine", "incomplete"),
            "*.tmp",
            SearchOption.AllDirectories).Length == 1,
            "startup recovery did not quarantine orphan .tmp");
        Assert(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories)
                   .All(path => path.Contains(
                       Path.Combine("quarantine", "incomplete"),
                       StringComparison.OrdinalIgnoreCase)),
            "temporary file remained outside incomplete quarantine");

        await VerifyReviewSidecarsPreserveTeacherLabelsAsync(root, labels);
    }

    private static async Task VerifyReviewSidecarsPreserveTeacherLabelsAsync(
        string root,
        string[] labelPaths)
    {
        var originalHashes = labelPaths.ToDictionary(
            path => path,
            path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
            StringComparer.OrdinalIgnoreCase);
        var service = new DatasetReviewService(root);
        var entries = await service.LoadAsync();
        Assert(entries.Count == 2, $"flat reviewer expected 2 entries, found {entries.Count}");
        Assert(entries.All(entry => entry.Record != null && entry.LoadError == null),
            "reviewer could not index a valid dataset label");

        var selected = entries[0];
        var correction = await service.SaveReviewAsync(
            selected,
            DatasetReviewStatuses.Corrected,
            new DatasetHumanLabel
            {
                Condition = WeatherCondition.MostlyCloudy,
                CloudCoverage = 47,
                RainDetected = false,
                FogDetected = false
            },
            "manual correction token=super-secret");

        Assert(correction.Revision == 1, "first review revision was not 1");
        Assert(correction.Status == DatasetReviewStatuses.Corrected,
            "corrected review status was not saved");
        Assert(correction.Notes != null
               && !correction.Notes.Contains("super-secret", StringComparison.Ordinal),
            "review note secret was not redacted");
        Assert(correction.HumanLabel?.Condition == WeatherCondition.MostlyCloudy
               && Math.Abs(correction.HumanLabel.CloudCoverage - 47) < 0.01,
            "human correction did not round-trip");

        foreach (var pair in originalHashes)
        {
            var currentHash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pair.Key)));
            Assert(string.Equals(currentHash, pair.Value, StringComparison.Ordinal),
                "review operation rewrote an immutable teacher label");
        }

        var sidecars = Directory.GetFiles(
            Path.Combine(root, "review", "labels"),
            "*.review.json",
            SearchOption.TopDirectoryOnly);
        Assert(sidecars.Length == 1, "review did not produce exactly one flat sidecar");
        Assert(Path.GetFileName(sidecars[0]) == selected.SampleId + ".review.json",
            "review sidecar is not addressable by sample id");

        var reloaded = await service.LoadAsync();
        var reloadedSelection = reloaded.Single(entry => entry.SampleId == selected.SampleId);
        Assert(reloadedSelection.EffectiveReviewStatus == DatasetReviewStatuses.Corrected,
            "flat reviewer did not reload the review status");
        Assert(reloadedSelection.Review?.OriginalLabelSha256 == selected.OriginalLabelSha256,
            "review does not identify the immutable source label hash");

        var accepted = await service.SaveReviewAsync(
            reloadedSelection,
            DatasetReviewStatuses.Accepted,
            humanLabel: null,
            notes: "teacher verified");
        Assert(accepted.Revision == 2, "second review did not increment the revision");
        Assert(accepted.HumanLabel == null, "accepted review retained a stale human correction");

        var auditFiles = Directory.GetFiles(
            Path.Combine(root, "review"),
            "reviews-*.jsonl",
            SearchOption.TopDirectoryOnly);
        Assert(auditFiles.Length == 1, "monthly review audit file missing");
        var auditLines = File.ReadAllLines(auditFiles[0]);
        Assert(auditLines.Length == 2, $"expected 2 review audit events, found {auditLines.Length}");
        foreach (var line in auditLines)
        {
            using var _ = JsonDocument.Parse(line);
        }

        var restartedRecorder = new TeacherStudentDatasetRecorder(() => CreateOptions(root));
        await WaitForRecorderAsync(
            restartedRecorder,
            status => status.TotalSamples == 2 && status.TodaySamples == 2,
            "startup dataset indexing");
        await restartedRecorder.StopAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task VerifyInvalidTeacherGoesToQuarantineAsync(string root)
    {
        var options = CreateOptions(root);
        var recorder = new TeacherStudentDatasetRecorder(() => options);
        using var frame = CreateFrame();
        var student = CreateStudent();
        var failure = OnlineAnalysisAttempt.Failed(
            new AnalysisProvenance
            {
                Origin = AnalysisOrigin.Gemini,
                Provider = "Gemini",
                Model = "gemini-test",
                PromptVersion = WeatherAnalysisPrompts.PromptVersion,
                FailureCategory = AnalysisFailureCategory.SchemaRejected,
                OnlineSucceeded = false,
                Attempts = 1,
                HttpStatus = 200
            },
            "schema rejected");
        var bundle = new WeatherAnalysisBundle
        {
            EffectiveResult = student.Clone(),
            Student = student,
            Teacher = failure,
            UsedFallback = true
        };

        Assert(recorder.TryEnqueue(
            frame, DateTime.UtcNow, null, bundle,
            effectiveSafe: false, visualSafe: false, externalSafetyMonitorSafe: null,
            highThreshold: 50, lowThreshold: 40),
            "invalid teacher sample was not accepted for quarantine");
        await WaitForRecorderAsync(recorder, status => status.QuarantineSamples == 1, "quarantine sample");
        await recorder.StopAsync(TimeSpan.FromSeconds(10));

        Assert(Directory.GetFiles(
            Path.Combine(root, "quarantine", "labels"),
            "*.json",
            SearchOption.AllDirectories).Length == 1,
            "invalid teacher did not produce one quarantine label");
        Assert(Directory.GetFiles(
            Path.Combine(root, "labels"),
            "*.json",
            SearchOption.AllDirectories).Length == 0,
            "invalid teacher entered the trainable label directory");
    }

    private static async Task VerifyQuotaStopsCollectionAsync(string root)
    {
        var options = new DatasetRecorderOptions
        {
            Enabled = true,
            RootDirectory = root,
            PeriodicEveryChecks = 2,
            MaximumBytes = 1,
            MinimumFreeBytes = 1,
            ImageScalePercent = 80,
            JpegQuality = 80,
            DisagreementThreshold = 20,
            NearDuplicateHammingDistance = 4,
            RecordQuarantine = true
        };
        var recorder = new TeacherStudentDatasetRecorder(() => options);
        using var frame = CreateFrame();
        var bundle = CreateSuccessfulBundle();
        Assert(recorder.TryEnqueue(
            frame, DateTime.UtcNow, null, bundle,
            effectiveSafe: true, visualSafe: true, externalSafetyMonitorSafe: null,
            highThreshold: 50, lowThreshold: 40), "quota test sample was not queued");
        await WaitForRecorderAsync(recorder, status => status.DroppedSamples >= 1, "quota guard");
        await recorder.StopAsync(TimeSpan.FromSeconds(10));
        Assert(recorder.Status.TotalSamples == 0, "quota guard allowed a label write");
    }

    private static DatasetRecorderOptions CreateOptions(string root)
    {
        return new DatasetRecorderOptions
        {
            Enabled = true,
            RootDirectory = root,
            PeriodicEveryChecks = 2,
            MaximumBytes = 1024L * 1024 * 1024,
            MinimumFreeBytes = 1,
            ImageScalePercent = 80,
            JpegQuality = 80,
            DisagreementThreshold = 20,
            NearDuplicateHammingDistance = 4,
            SaveTeacherRaw = true,
            RecordQuarantine = true
        };
    }

    private static Bitmap CreateFrame()
    {
        var bitmap = new Bitmap(800, 450);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.MidnightBlue);
        using var cloud = new SolidBrush(Color.LightGray);
        graphics.FillEllipse(cloud, 180, 90, 300, 140);
        graphics.FillEllipse(Brushes.White, 50, 40, 4, 4);
        return bitmap;
    }

    private static WeatherAnalysisBundle CreateSuccessfulBundle()
    {
        var teacher = new WeatherAnalysisResult
        {
            Timestamp = DateTime.UtcNow,
            Condition = WeatherCondition.PartlyCloudy,
            CloudCoverage = 28,
            Confidence = 91,
            IsSafeForImaging = true,
            Description = "Thin scattered cloud",
            RawAnalysisData =
                "{\"password\":\"super-secret\",\"url\":\"rtsp://camera:camera-password@192.0.2.1/live\",\"token\":\"super-secret\"}",
            Provenance = new AnalysisProvenance
            {
                Origin = AnalysisOrigin.Gemini,
                Provider = "Gemini",
                Model = "gemini-test",
                PromptVersion = WeatherAnalysisPrompts.PromptVersion,
                OnlineSucceeded = true,
                Attempts = 1,
                HttpStatus = 200,
                LatencyMilliseconds = 123
            }
        };
        var student = CreateStudent();
        return new WeatherAnalysisBundle
        {
            EffectiveResult = teacher,
            Teacher = OnlineAnalysisAttempt.Succeeded(teacher),
            Student = student
        };
    }

    private static WeatherAnalysisResult CreateStudent()
    {
        return new WeatherAnalysisResult
        {
            Timestamp = DateTime.UtcNow,
            Condition = WeatherCondition.PartlyCloudy,
            CloudCoverage = 31,
            Confidence = 70,
            IsSafeForImaging = true,
            Description = "Local shadow result",
            Provenance = new AnalysisProvenance
            {
                Origin = AnalysisOrigin.LocalHeuristic,
                Provider = "Local",
                Model = "local-heuristic-v1",
                Attempts = 1
            }
        };
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}");
            }
            await Task.Delay(50);
        }
    }

    private static async Task WaitForRecorderAsync(
        TeacherStudentDatasetRecorder recorder,
        Func<DatasetStatusSnapshot, bool> condition,
        string description)
    {
        await WaitUntilAsync(() =>
        {
            var status = recorder.Status;
            if (status.ErrorCount > 0)
            {
                throw new InvalidOperationException(
                    $"Recorder error while waiting for {description}: {status.LastError}");
            }
            return condition(status);
        }, description);
    }

    private sealed class QuotaTeacher : IOnlineWeatherAnalysisService
    {
        private readonly DateTime _retryAfterUtc;

        public QuotaTeacher(DateTime retryAfterUtc)
        {
            _retryAfterUtc = retryAfterUtc;
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<WeatherAnalysisResult> AnalyzeImageAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WeatherAnalysisResult
            {
                Condition = WeatherCondition.Unknown,
                Confidence = 0,
                IsSafeForImaging = false
            });
        }

        public Task<OnlineAnalysisAttempt> TryAnalyzeOnlineOnlyAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(OnlineAnalysisAttempt.Failed(
                new AnalysisProvenance
                {
                    Origin = AnalysisOrigin.Gemini,
                    Provider = "Gemini",
                    Model = "gemini-test",
                    PromptVersion = WeatherAnalysisPrompts.PromptVersion,
                    FailureCategory = AnalysisFailureCategory.QuotaExhausted,
                    OnlineSucceeded = false,
                    Attempts = 0,
                    ProviderFailureCode = "quota_exhausted",
                    RetryAfterUtc = _retryAfterUtc,
                    QuotaMetric = "test-quota-metric",
                    QuotaId = "test-daily-quota",
                    ConsecutiveQuotaFailures = 2,
                    RequestSuppressed = true
                },
                "Gemini quota circuit active"));
        }
    }

    private sealed class QuotaResponseHandler : StaticResponseHandler
    {
        public QuotaResponseHandler(string responseBody)
            : base((HttpStatusCode)429, responseBody)
        {
        }
    }

    private class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public StaticResponseHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
