using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NINA.Core.Utility;

namespace AIWeather
{
    public partial class AIWeatherPreviewView
    {
        private (string url, string? user, string? password)? _pendingPreview;
        private (string url, string? user, string? password)? _requestedPreview;
        private CancellationTokenSource? _previewReconnectCts;
        private int _previewReconnectAttempt;

        private bool IsOnScreen() =>
            IsLoaded && IsVisible && PresentationSource.FromVisual(this) != null;

        private void ResumePendingPreview()
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (!IsOnScreen()) return;
                if (_pendingPreview is { } pending)
                {
                    _pendingPreview = null;
                    try { await StartStreamAsync(pending.url, pending.user, pending.password); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { Logger.Warning($"Deferred preview start failed: {ex.GetType().Name}"); }
                }
                else if (_requestedPreview.HasValue && _videoHost?.Player?.IsPlaying != true)
                {
                    SchedulePreviewReconnect();
                }
            }), DispatcherPriority.Loaded);
        }

        private void OnPreviewPlaybackLost(object? sender, EventArgs e)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(sender, _videoHost?.Player) || !_requestedPreview.HasValue) return;
                Volatile.Write(ref _previewUnhealthy, 1);
                SchedulePreviewReconnect();
            }), DispatcherPriority.Background);
        }

        private async void SchedulePreviewReconnect()
        {
            if (_previewReconnectCts != null || !_requestedPreview.HasValue || Dispatcher.HasShutdownStarted) return;
            using var cts = new CancellationTokenSource();
            _previewReconnectCts = cts;
            try
            {
                while (_requestedPreview.HasValue)
                {
                    var seconds = Math.Min(30, 2 << Math.Min(_previewReconnectAttempt++, 4));
                    Logger.Info($"RTSP preview playback lost; reconnect attempt {_previewReconnectAttempt} in {seconds}s");
                    await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token);
                    if (!IsOnScreen()) continue;
                    if (_requestedPreview is not { } requested) break;
                    await StartStreamAsync(requested.url, requested.user, requested.password, cts.Token);
                    if (_videoHost?.Player?.IsPlaying == true) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warning($"Preview reconnect failed: {ex.GetType().Name}"); }
            finally
            {
                if (ReferenceEquals(_previewReconnectCts, cts)) _previewReconnectCts = null;
            }
        }
    }
}
