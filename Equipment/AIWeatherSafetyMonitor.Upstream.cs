using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using AIWeather.Services;
using NINA.Core.Utility;
using NINA.Core.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Equipment.MyGuider.PHD2.PhdEvents;

namespace AIWeather.Equipment
{
    public partial class AIWeatherSafetyMonitor
    {
        private readonly ExternalSafetyDevice _environmentDevice;
        private IGuiderMediator? _guiderMediator;
        private IWeatherDataMediator? _weatherMediator;
        private bool _guiderSubscribed;
        internal IWeatherAnalysisService AnalysisService => _analysisService;
        public string? LastCaptureFailure { get; private set; }
        public string? LastFrameSource { get; private set; }
        public static string PluginDataFolder => Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "SkySentinel");
        public static string CaptureFolder => Path.Combine(PluginDataFolder, "captures");
        public static string DefaultDecisionFolder => Path.Combine(PluginDataFolder, "decisions");
        internal DecisionArchive Decisions = new DecisionArchive(DefaultDecisionFolder);
        private bool? _lastDecisionSafe;

        private bool IsEnvironmentMonitorSafe() =>
            !Properties.Settings.Default.UseEnvironmentSafetyMonitor ||
            _environmentDevice.IsSafe(Properties.Settings.Default.EnvironmentSafetyMonitorProgId, UtcNow());

        /// <summary>Injects NINA's guider mediator, the source of PHD2 guide steps for the guide-star veto (#24).</summary>
        public void SetGuiderMediator(IGuiderMediator guiderMediator)
        {
            _guiderMediator = guiderMediator;
        }

        /// <summary>Injects NINA's weather data mediator, the optional second vote of the guide-star veto (#24).</summary>
        public void SetWeatherDataMediator(IWeatherDataMediator weatherDataMediator)
        {
            _weatherMediator = weatherDataMediator;
        }

        /// <summary>The guide star's recent history, kept while connected (#24).</summary>
        internal GuideStarVeto GuideVeto { get; } = new GuideStarVeto();

        /// <summary>One guide step: PHD2's star mass and, when the second vote is on, the device reading taken with it.</summary>
        internal void RecordGuideStep(DateTime utc, double starMass, double? weather)
        {
            GuideVeto.Record(utc, starMass, weather);
        }

        private void OnGuideStep(object? sender, IGuideStep step)
        {
            // Only PHD2 reports star mass; other guiders simply never feed the veto.
            if (step is PhdEventGuideStep phd)
            {
                RecordGuideStep(UtcNow(), phd.StarMass, WeatherReading());
            }
        }

        /// <summary>The weather device's chosen property right now, or null when the vote is off, the device is away or the value is not provided.</summary>
        private double? WeatherReading()
        {
            if (!Properties.Settings.Default.WeatherVoteEnabled || _weatherMediator == null) return null;
            try
            {
                var info = _weatherMediator.GetInfo();
                if (info == null || !info.Connected) return null;
                var value = Properties.Settings.Default.WeatherVoteProperty switch
                {
                    "SkyTemperature" => info.SkyTemperature,
                    "CloudCover" => info.CloudCover,
                    _ => info.SkyQuality,
                };
                return double.IsNaN(value) || double.IsInfinity(value) ? null : value;
            }
            catch (Exception ex)
            {
                Logger.Debug($"Weather device reading for the guide-star veto failed: {ex.Message}");
                return null;
            }
        }

        private void SubscribeGuider()
        {
            if (_guiderMediator == null || _guiderSubscribed) return;
            _guiderMediator.GuideEvent += OnGuideStep;
            _guiderSubscribed = true;
        }

        private void UnsubscribeGuider()
        {
            if (_guiderMediator == null || !_guiderSubscribed) return;
            _guiderMediator.GuideEvent -= OnGuideStep;
            _guiderSubscribed = false;
        }

        private void KeepDecisionIfChanged(WeatherAnalysisResult result)
        {
            if (!Properties.Settings.Default.KeepDecisionFrames)
            {
                return;
            }

            var combined = IsSafe;
            // A vetoed cloud call changes nothing and is kept all the same: the frame the
            // model read as cloudy next to the guide-star numbers that said otherwise (#24).
            var vetoed = combined && !string.IsNullOrEmpty(result.Veto);
            if (_lastDecisionSafe == combined && !vetoed)
            {
                return;
            }
            var transition = vetoed && _lastDecisionSafe == combined
                ? "Safe held (cloud call vetoed)"
                : $"{(_lastDecisionSafe is bool prev ? (prev ? "Safe" : "Unsafe") : "Start")} -> {(combined ? "Safe" : "Unsafe")}";
            _lastDecisionSafe = combined;

            string Device(bool enabled, bool safe, bool connected) =>
                !enabled ? "not configured" : safe ? "Safe" : connected ? "Unsafe" : "cannot be connected or read";

            var record = new DecisionRecord
            {
                Transition = transition,
                Utc = UtcNow(),
                // Evidence names who actually looked at the frame: a verdict from the offline
                // analyzer after the provider failed is filed as the analyzer's, with the failure.
                Provider = result.Provenance.Provider + (result.Provenance.IsFallback
                    ? $" (local fallback, online failure: {result.Provenance.FailureCategory})" : string.Empty),
                Model = result.Provenance.Model,
                Condition = result.Condition,
                CloudCoverage = result.CloudCoverage,
                Confidence = result.Confidence,
                RainDetected = result.RainDetected,
                FogDetected = result.FogDetected,
                Description = result.Description,
                HighThreshold = EffectiveCloudUnsafeThreshold(),
                LowThreshold = EffectiveCloudSafeThreshold(),
                SkySafe = _isCurrentlySafe,
                ExternalMonitor = Properties.Settings.Default.UseAscomSafetyMonitor
                    ? $"{Device(true, IsExternalMonitorSafe(), _externalMonitor.Connected)} ({ExternalMode})"
                    : "not configured",
                EnvironmentMonitor = Device(Properties.Settings.Default.UseEnvironmentSafetyMonitor, IsEnvironmentMonitorSafe(), _environmentDevice.Connected),
                CombinedSafe = combined,
                Reason = SafetyStateReason,
                Veto = result.Veto,
            };

            var path = Decisions.Save(_lastImage, record, UtcNow().ToLocalTime());
            Logger.Info($"Decision kept ({transition}): {path ?? "no frame"}, {Decisions.Folder}");
            Decisions.Prune(TimeSpan.FromDays(Math.Max(1, Properties.Settings.Default.DecisionRetentionDays)), UtcNow().ToLocalTime());
        }

        internal static bool NextSkyState(bool currentlySafe, WeatherAnalysisResult result,
            double unsafeThreshold, double safeThreshold)
        {
            // ADVANCED owns the thresholds; provider IsSafeForImaging is not ground truth.
            if (!AnalysisThrottle.CanApplyResult(result) || result.IsSkyUnreadable ||
                result.Condition == WeatherCondition.Unknown || result.RainDetected || result.FogDetected)
                return false;
            if (currentlySafe && !string.IsNullOrEmpty(result.Veto)) return true;
            return result.CloudCoverage < (currentlySafe ? unsafeThreshold : safeThreshold);
        }

        /// <summary>The pause before the second frame of a transition; the tests pass one that does not wait.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;
        private static readonly TimeSpan ConfirmPause = TimeSpan.FromSeconds(20);

        /// <summary>
        /// A change of the combined state wants two frames. Three Safe -> Unsafe -> Safe cycles
        /// in one night, each from a single reading on frames that looked the same, parked a
        /// rig and cancelled an autofocus (#21). When an analysis would change the state, a
        /// second frame is taken and analysed, and the second frame decides: the change goes
        /// through only if it agrees. No change proposed, no second frame. Without a second
        /// frame the first reading stands - a camera that cannot deliver does not veto.
        /// </summary>
        internal async Task<WeatherAnalysisResult> ResolveTransitionAsync(WeatherAnalysisResult first, Func<Task<WeatherAnalysisResult?>> secondOpinion)
        {
            if (_lastResult == null || !IsAnalysisFresh() || IsReplicaFailoverActive ||
                first.IsSkyUnreadable || first.Condition == WeatherCondition.Unknown ||
                first.RainDetected || first.FogDetected || !AnalysisThrottle.CanApplyResult(first) ||
                !IsEnvironmentMonitorSafe() ||
                (Properties.Settings.Default.UseAscomSafetyMonitor && !IsExternalMonitorSafe())) return first;   // the first verdict after connecting: nothing to protect yet

            var high = EffectiveCloudUnsafeThreshold();
            var low = EffectiveCloudSafeThreshold();
            var prospectiveSky = NextSkyState(_isCurrentlySafe, first, high, low);
            var ascom = Properties.Settings.Default.UseAscomSafetyMonitor;
            var environment = Properties.Settings.Default.UseEnvironmentSafetyMonitor;
            var prospective = SafetyVerdict.Combine(ExternalMode, prospectiveSky, true,
                first.RainDetected || first.FogDetected, ascom, IsExternalMonitorSafe(), environment, IsEnvironmentMonitorSafe());
            if (prospective == IsSafe) return first;

            // The guide-star veto (#24): a Safe -> Unsafe change that rests on clouds alone is
            // put to PHD2's guide star first. Rain and fog never come here; neither does a
            // change the external devices would make anyway. Held, the call needs no second
            // frame either: the star already looked.
            if (IsSafe && !prospectiveSky && !first.RainDetected && !first.FogDetected
                && Properties.Settings.Default.GuideStarVetoEnabled
                && SafetyVerdict.Combine(ExternalMode, true, true, false, ascom, IsExternalMonitorSafe(), environment, IsEnvironmentMonitorSafe()))
            {
                var verdict = GuideVeto.Evaluate(UtcNow(), Properties.Settings.Default.GuideStarVetoPercent,
                    Properties.Settings.Default.WeatherVoteEnabled, Properties.Settings.Default.WeatherVoteLimit,
                    Properties.Settings.Default.WeatherVoteProperty);
                if (verdict == null)
                {
                    Logger.Info($"Cloud call ({first.Condition}, {first.CloudCoverage:F0}%): no guide-star history to check it against, no veto");
                }
                else if (verdict.Holds)
                {
                    Logger.Info($"Cloud call vetoed ({first.Condition}, {first.CloudCoverage:F0}%): {verdict.Describe()}");
                    first.Veto = verdict.Describe();
                    return first;
                }
                else
                {
                    Logger.Info($"Cloud call stands ({first.Condition}, {first.CloudCoverage:F0}%): {verdict.Describe()}");
                }
            }

            if (!Properties.Settings.Default.ConfirmTransitions) return first;

            Logger.Info($"Analysis proposes {(IsSafe ? "Safe" : "Unsafe")} -> {(prospective ? "Safe" : "Unsafe")} ({first.Condition}, {first.CloudCoverage:F0}%); asking a second frame before acting on it");
            var second = await secondOpinion();
            if (second == null)
            {
                Logger.Warning("No second frame; the first reading stands");
                return first;
            }

            var confirmedSky = NextSkyState(_isCurrentlySafe, second, high, low);
            var confirmed = SafetyVerdict.Combine(ExternalMode, confirmedSky, true,
                second.RainDetected || second.FogDetected,
                Properties.Settings.Default.UseAscomSafetyMonitor, IsExternalMonitorSafe(),
                Properties.Settings.Default.UseEnvironmentSafetyMonitor, IsEnvironmentMonitorSafe());
            Logger.Info(confirmed == prospective
                ? $"Second frame agrees ({second.Condition}, {second.CloudCoverage:F0}%): the change goes through"
                : $"Second frame disagrees ({second.Condition}, {second.CloudCoverage:F0}%): the state stays {(IsSafe ? "Safe" : "Unsafe")}");
            return second;
        }

    }
}
