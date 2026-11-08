using AIWeather.Equipment;
using AIWeather.Localization;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace SkySentinel.SequenceItems {

    [Export(typeof(ISequenceItem))]
    [ExportMetadata("Name", "Sky Sentinel - Start Monitoring")]
    [ExportMetadata("Description", "Starts Sky Sentinel all-sky camera monitoring and periodic weather checks")]
    [ExportMetadata("Category", "Sky Sentinel")]
    [ExportMetadata("Icon", "SkySentinel_StartIcon")]
    public class StartMonitoringInstruction : SequenceItem {

        [ImportingConstructor]
        public StartMonitoringInstruction() {
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var monitor = AIWeatherSafetyMonitor.Instance;

                if (monitor.Connected) {
                    Logger.Info("Sky Sentinel monitoring is already running");
                    progress?.Report(new ApplicationStatus() {
                        Status = UiLocalization.Text("Sequencer.Already")
                    });
                    return;
                }

                Logger.Info("Sky Sentinel: Starting monitoring from sequencer instruction");
                progress?.Report(new ApplicationStatus() {
                    Status = UiLocalization.Text("Sequencer.Starting")
                });

                var success = await monitor.Connect(token);

                if (success) {
                    Logger.Info("Sky Sentinel: Monitoring started successfully from sequencer");
                    progress?.Report(new ApplicationStatus() {
                        Status = UiLocalization.Text("Sequencer.Active")
                    });
                } else {
                    Logger.Warning("Sky Sentinel: Failed to start monitoring from sequencer");
                    progress?.Report(new ApplicationStatus() {
                        Status = UiLocalization.Text("Sequencer.StartFailed")
                    });
                }
            } catch (Exception ex) {
                Logger.Error($"Sky Sentinel: Error starting monitoring from sequencer: {ex.Message}", ex);
            }
        }

        public override object Clone() {
            return new StartMonitoringInstruction() { Icon = Icon, Name = Name };
        }

        public override string ToString() {
            return UiLocalization.Text("Sequencer.Start");
        }
    }
}
