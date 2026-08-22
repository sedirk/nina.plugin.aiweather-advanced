using AIWeather.Services;
using AIWeather.Localization;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace AIWeather.Models
{
    public sealed class DatasetRecorderOptions
    {
        public bool Enabled { get; init; }
        public bool Paused { get; init; }
        public string RootDirectory { get; init; } = string.Empty;
        public int PeriodicEveryChecks { get; init; } = 1;
        public long MaximumBytes { get; init; } = 20L * 1024 * 1024 * 1024;
        public long MinimumFreeBytes { get; init; } = 5L * 1024 * 1024 * 1024;
        public double ImageScalePercent { get; init; } = 50;
        public int JpegQuality { get; init; } = 85;
        public double DisagreementThreshold { get; init; } = 20;
        public int NearDuplicateHammingDistance { get; init; } = 4;
        public bool SaveTeacherRaw { get; init; }
        public bool RecordQuarantine { get; init; } = true;

        public static DatasetRecorderOptions FromSettings()
        {
            var configuredRoot = Properties.Settings.Default.DatasetDirectory;

            return new DatasetRecorderOptions
            {
                Enabled = Properties.Settings.Default.DatasetEnabled,
                Paused = Properties.Settings.Default.DatasetPaused,
                RootDirectory = ResolveRootDirectory(configuredRoot),
                PeriodicEveryChecks = Math.Clamp(
                    Properties.Settings.Default.DatasetSampleEveryChecks, 1, 10000),
                MaximumBytes = GigabytesToBytes(
                    ClampFinite(Properties.Settings.Default.DatasetMaxSizeGb, 0.1, 10240, 20)),
                MinimumFreeBytes = GigabytesToBytes(
                    ClampFinite(Properties.Settings.Default.DatasetMinFreeSpaceGb, 0.1, 10240, 5)),
                ImageScalePercent = ClampFinite(
                    Properties.Settings.Default.DatasetImageScalePercent, 5, 100, 50),
                JpegQuality = Math.Clamp(Properties.Settings.Default.DatasetJpegQuality, 40, 100),
                DisagreementThreshold = ClampFinite(
                    Properties.Settings.Default.DatasetDisagreementThreshold, 0, 100, 20),
                NearDuplicateHammingDistance = Math.Clamp(
                    Properties.Settings.Default.DatasetNearDuplicateHammingDistance, 0, 64),
                SaveTeacherRaw = Properties.Settings.Default.DatasetSaveTeacherRaw,
                RecordQuarantine = Properties.Settings.Default.DatasetRecordQuarantine
            };
        }

        public static string DefaultRootDirectory()
        {
            return System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "AIWeather",
                "dataset",
                "v1");
        }

        private static long GigabytesToBytes(double value)
        {
            return checked((long)(value * 1024d * 1024d * 1024d));
        }

        private static string ResolveRootDirectory(string? configuredRoot)
        {
            try
            {
                var root = string.IsNullOrWhiteSpace(configuredRoot)
                    ? DefaultRootDirectory()
                    : Environment.ExpandEnvironmentVariables(configuredRoot.Trim());
                return System.IO.Path.GetFullPath(root);
            }
            catch (Exception)
            {
                // A manually edited or damaged user.config must not prevent the safety
                // monitor from starting. The default remains private to this user profile.
                return DefaultRootDirectory();
            }
        }

        private static double ClampFinite(double value, double minimum, double maximum, double fallback)
        {
            return double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
        }
    }

    /// <summary>
    /// Owns Image and must be disposed by either the queue rejection path or the single
    /// background writer.
    /// </summary>
    public sealed class DatasetSampleEnvelope : IDisposable
    {
        public Bitmap Image { get; init; } = null!;
        public DateTime CapturedUtc { get; init; }
        public AstroContext? AstroContext { get; init; }
        public WeatherAnalysisBundle Analysis { get; init; } = new WeatherAnalysisBundle();
        public DatasetRecorderOptions Options { get; init; } = new DatasetRecorderOptions();
        public IReadOnlyList<string> SelectionReasons { get; init; } = Array.Empty<string>();
        public bool IsPriorityEvent { get; init; }
        public bool Quarantined { get; init; }
        public string? QuarantineReason { get; init; }
        public bool EffectiveSafe { get; init; }
        public bool VisualSafe { get; init; }
        public bool? ExternalSafetyMonitorSafe { get; init; }
        public double HighThreshold { get; init; }
        public double LowThreshold { get; init; }
        public string RoiVersion { get; init; } = "full-frame-v1";

        public void Dispose()
        {
            Image.Dispose();
        }
    }

    public sealed class DatasetStatusSnapshot
    {
        public bool Enabled { get; init; }
        public bool Paused { get; init; }
        public string State { get; init; } = "Disabled";
        public string RootDirectory { get; init; } = string.Empty;
        public long TotalSamples { get; init; }
        public long TrainableSamples { get; init; }
        public long QuarantineSamples { get; init; }
        public long ReviewSamples { get; init; }
        public long TodaySamples { get; init; }
        public long DroppedSamples { get; init; }
        public long ErrorCount { get; init; }
        public long CurrentBytes { get; init; }
        public long FreeBytes { get; init; }
        public DateTime? LastWriteUtc { get; init; }
        public double? LastTeacherStudentDifference { get; init; }
        public string LastTeacher { get; init; } = "none";
        public string LastStudent { get; init; } = AnalysisMetadata.LocalHeuristicModelVersion;
        public string? LastError { get; init; }

        public string ToDisplayString()
        {
            if (!Enabled)
            {
                return UiLocalization.Text("Runtime.DatasetOff");
            }

            var sizeGb = CurrentBytes / 1024d / 1024d / 1024d;
            var difference = LastTeacherStudentDifference.HasValue
                ? UiLocalization.Text("Runtime.CloudDifference", LastTeacherStudentDifference.Value)
                : string.Empty;
            return UiLocalization.Text(
                "Runtime.DatasetStatus",
                LocalizeState(State),
                TodaySamples,
                TotalSamples,
                QuarantineSamples,
                DroppedSamples,
                sizeGb,
                difference);
        }

        private static string LocalizeState(string state)
        {
            if (!UiLocalization.IsChineseCulture())
            {
                return state;
            }

            return state switch
            {
                "Disabled" => "已关闭",
                "Paused" => "已暂停",
                "Ready" => "就绪",
                "Ready (quarantine written)" => "就绪（已写入隔离区）",
                "Quota reached" => "已达到容量上限",
                "Low disk space" => "磁盘空间不足",
                "Error" => "错误",
                _ => state
            };
        }
    }

    public sealed class DatasetManifest
    {
        public int SchemaVersion { get; init; } = 1;
        public string DatasetId { get; init; } = Guid.NewGuid().ToString("N");
        public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
        public string PluginVersion { get; init; } = "unknown";
        public string RoiVersion { get; init; } = "full-frame-v1";
        public string Privacy { get; init; } = "local-only; coordinates and credentials excluded";
    }

    public sealed class DatasetSampleRecord
    {
        public int SchemaVersion { get; init; } = 1;
        public string SampleId { get; init; } = string.Empty;
        public DateTime CapturedUtc { get; init; }
        public DatasetImageRecord Image { get; init; } = new DatasetImageRecord();
        public DatasetAstroRecord? Astro { get; init; }
        public DatasetAnalysisRecord Teacher { get; init; } = new DatasetAnalysisRecord();
        public DatasetAnalysisRecord Student { get; init; } = new DatasetAnalysisRecord();
        public DatasetDecisionRecord Decision { get; init; } = new DatasetDecisionRecord();
        public DatasetSelectionRecord Selection { get; init; } = new DatasetSelectionRecord();
        public DatasetReviewRecord Review { get; init; } = new DatasetReviewRecord();
    }

    public sealed class DatasetImageRecord
    {
        public string RelativePath { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string PerceptualHash { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public int SourceWidth { get; init; }
        public int SourceHeight { get; init; }
        public double ScalePercent { get; init; }
        public int JpegQuality { get; init; }
        public string RoiVersion { get; init; } = "full-frame-v1";
        public bool PossibleFrozenFrame { get; init; }
    }

    public sealed class DatasetAstroRecord
    {
        public double SunAltitude { get; init; }
        public string SunState { get; init; } = string.Empty;
        public double MoonAltitude { get; init; }
        public double MoonIllumination { get; init; }
        public string MoonPhase { get; init; } = string.Empty;
        // Latitude, longitude, elevation and local timezone are intentionally absent.
    }

    public sealed class DatasetAnalysisRecord
    {
        public bool Valid { get; init; }
        public AnalysisProvenance Provenance { get; init; } = new AnalysisProvenance();
        public DatasetWeatherResult? Result { get; init; }
        public string? RawResponse { get; init; }
        public string? FailureMessage { get; init; }
    }

    public sealed class DatasetWeatherResult
    {
        public DateTime Timestamp { get; init; }
        public WeatherCondition Condition { get; init; }
        public double CloudCoverage { get; init; }
        public double Confidence { get; init; }
        public bool IsSafeForImaging { get; init; }
        public string Description { get; init; } = string.Empty;
        public double? Brightness { get; init; }
        public bool RainDetected { get; init; }
        public bool FogDetected { get; init; }

        public static DatasetWeatherResult From(WeatherAnalysisResult result)
        {
            return new DatasetWeatherResult
            {
                Timestamp = result.Timestamp,
                Condition = result.Condition,
                CloudCoverage = result.CloudCoverage,
                Confidence = result.Confidence,
                IsSafeForImaging = result.IsSafeForImaging,
                Description = result.Description,
                Brightness = result.Brightness,
                RainDetected = result.RainDetected,
                FogDetected = result.FogDetected
            };
        }
    }

    public sealed class DatasetDecisionRecord
    {
        public AnalysisOrigin EffectiveSource { get; init; }
        public bool EffectiveSafe { get; init; }
        public bool VisualSafe { get; init; }
        public double HighThreshold { get; init; }
        public double LowThreshold { get; init; }
        public bool? ExternalSafetyMonitorSafe { get; init; }
        public bool UsedFallback { get; init; }
    }

    public sealed class DatasetSelectionRecord
    {
        public IReadOnlyList<string> Reason { get; init; } = Array.Empty<string>();
        public bool NearDuplicate { get; init; }
        public bool Quarantined { get; init; }
        public string? QuarantineReason { get; init; }
    }

    public sealed class DatasetReviewRecord
    {
        public string Status { get; init; } = "unreviewed";
        public DateTime? ReviewedUtc { get; init; }
        public object? HumanLabel { get; init; }
    }
}
