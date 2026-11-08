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
    [ExportMetadata("Name", "Sky Sentinel - Stop Monitoring")]
    [ExportMetadata("Description", "Stops Sky Sentinel all-sky camera monitoring")]
    [ExportMetadata("Category", "Sky Sentinel")]
    [ExportMetadata("Icon", "SkySentinel_StopIcon")]
    public class StopMonitoringInstruction : SequenceItem {

        [ImportingConstructor]
        public StopMonitoringInstruction() {
        }

        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            try {
                var monitor = AIWeatherSafetyMonitor.Instance;

                if (!monitor.Connected) {
                    Logger.Info("Sky Sentinel monitoring is not running");
                    progress?.Report(new ApplicationStatus() {
                        Status = UiLocalization.Text("Sequencer.NotRunning")
                    });
                    return Task.CompletedTask;
                }

                Logger.Info("Sky Sentinel: Stopping monitoring from sequencer instruction");
                monitor.Disconnect();

                Logger.Info("Sky Sentinel: Monitoring stopped from sequencer");
                progress?.Report(new ApplicationStatus() {
                    Status = UiLocalization.Text("Sequencer.Stopped")
                });
            } catch (Exception ex) {
                Logger.Error($"Sky Sentinel: Error stopping monitoring from sequencer: {ex.Message}", ex);
            }

            return Task.CompletedTask;
        }

        public override object Clone() {
            return new StopMonitoringInstruction() { Icon = Icon, Name = Name };
        }

        public override string ToString() {
            return UiLocalization.Text("Sequencer.Stop");
        }
    }
}
