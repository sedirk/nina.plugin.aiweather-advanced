using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
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

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert(UiLocalization.Text("Preview.ActivityLog") == "Activity Log",
                "Unsupported cultures must fall back to English");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
