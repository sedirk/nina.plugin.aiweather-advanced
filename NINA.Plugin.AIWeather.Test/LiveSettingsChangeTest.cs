using System;
using System.Threading;
using AIWeather.Equipment;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test
{
    /// <summary>
    /// A model changed in the options while the monitor was connected never reached the night:
    /// the service was rebuilt but not initialised, so every check logged "Gemini service not
    /// initialized" and ran on the offline analyzer until a reconnect (#21, 25-26/10).
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class LiveSettingsChangeTest
    {
        private sealed class Idle : IExternalSafetyMonitor
        {
            public string ProgId => string.Empty;
            public bool Connected => false;
            public bool TryConnect(string progId) => false;
            public bool TryGetIsSafe(out bool isSafe) { isSafe = true; return false; }
            public void Disconnect() { }
        }

        private (string provider, string key, string model) _before;
        private AIWeatherSafetyMonitor _monitor = null!;

        [SetUp]
        public void SetUp() {
            var s = global::AIWeather.Properties.Settings.Default;
            _before = (s.AnalysisProvider, s.GeminiPaidKey, s.SelectedModel);
            s.AnalysisProvider = "Gemini";
            s.GeminiPaidKey = "not-a-real-key";
            s.SelectedModel = "gemini-flash-latest";
            _monitor = new AIWeatherSafetyMonitor(new Idle(), new Idle(), observeSettings: false);
        }

        [TearDown]
        public async System.Threading.Tasks.Task TearDown() {
            await _monitor.ShutdownAsync();
            var s = global::AIWeather.Properties.Settings.Default;
            (s.AnalysisProvider, s.GeminiPaidKey, s.SelectedModel) = _before;
        }

        private async System.Threading.Tasks.Task<IWeatherAnalysisService> Settled()
        {
            // ADVANCED initializes the replacement service under the check gate, not in an
            // async-void settings handler. Exercise those same two methods without a timer/API.
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(AIWeatherSafetyMonitor).GetMethod("UpdateAnalysisService", flags)!.Invoke(_monitor, null);
            return await (System.Threading.Tasks.Task<IWeatherAnalysisService>)
                typeof(AIWeatherSafetyMonitor).GetMethod("EnsureAnalysisServiceInitializedAsync", flags)!
                    .Invoke(_monitor, new object[] { CancellationToken.None })!;
        }

        [Test]
        public async System.Threading.Tasks.Task AModelChangedWhileConnectedIsReadyForTheNextCheck() {
            global::AIWeather.Properties.Settings.Default.SelectedModel = "gemini-flash-lite-latest";

            (await Settled()).Should().BeOfType<GeminiAnalysisService>().Which.IsInitialized.Should().BeTrue();
        }

        [Test]
        public async System.Threading.Tasks.Task ARemovedKeyRetainsAnExplicitFailedTeacherForTheOnnxOrchestrator() {
            // Keep failure provenance; the orchestrator chooses ONNX explicitly.
            global::AIWeather.Properties.Settings.Default.GeminiPaidKey = string.Empty;

            (await Settled()).Should().BeOfType<GeminiAnalysisService>().Which.IsInitialized.Should().BeFalse();
        }
    }
}
