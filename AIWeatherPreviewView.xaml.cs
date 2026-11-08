using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using NINA.Core.Utility;
using AIWeather.Views;
using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using System.Windows.Controls.Primitives;
using Bitmap = System.Drawing.Bitmap;
using DrawingImage = System.Drawing.Image;

namespace AIWeather
{
    /// <summary>
    /// Interaction logic for AIWeatherPreviewView.xaml
    /// </summary>
    [Export(typeof(AIWeatherPreviewView))]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public partial class AIWeatherPreviewView : UserControl
    {
        private LibVLC? _libVLC;
        private VideoHwndHost? _videoHost;
        private bool _isStartingStream;
        private readonly SemaphoreSlim _streamGate = new SemaphoreSlim(1, 1);
        private readonly PreviewStartupState _startupState = new PreviewStartupState();
        private string? _startingPlaybackUrl;
        private int _previewCanSnapshot;
        private CancellationTokenSource? _startCts;
        private Media? _currentMedia;
        private string? _activePlaybackUrl;
        private readonly RtspPreviewHealthMonitor _previewHealthMonitor = new RtspPreviewHealthMonitor();
        private int _previewRecoveryScheduled;
        private int _previewUnhealthy;
        private IDisposable? _sharedPreviewFrameRegistration;

        internal bool IsPreviewStreamHealthy =>
            _videoHost?.Player?.IsPlaying == true
            && Volatile.Read(ref _previewUnhealthy) == 0;

        public AIWeatherPreviewView()
        {
            InitializeComponent();
            InitializeVLC();
            
            // Subscribe to Unloaded event for cleanup
            this.Unloaded += OnViewUnloaded;
            
            // Set view reference in ViewModel when loaded
            this.Loaded += (s, e) =>
            {
                Logger.Info($"🔄 AI Weather view Loaded event fired");
                ResumePendingPreview();
                
                if (DataContext is AIWeatherPreviewViewModel vm)
                {
                    vm.SetView(this);
                    vm.SyncCaptureMode();
                    Logger.Debug($"View reference set in ViewModel");
                    
                    // If we're navigating back and there's a running RTSP stream, restart it
                    // Use longer delay and background priority to ensure UI is stable
                    Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            Logger.Debug("Waiting 1000ms for view stabilization...");
                            // Wait for view to fully stabilize before attempting restart
                            await Task.Delay(1000);
                            
                            // Recheck DataContext in case view was unloaded during delay
                            if (DataContext is not AIWeatherPreviewViewModel viewModel)
                            {
                                Logger.Debug("View unloaded before RTSP restart could complete");
                                return;
                            }
                            
                            Logger.Debug($"Checking for running RTSP stream. Mode: {viewModel.CurrentCaptureMode}");

                            if (viewModel.IsClusterReplica)
                            {
                                await viewModel.SynchronizeReplicaPreviewAsync();
                                return;
                            }

                            // Check if any source is marked as running (RTSP mode only)
                            var runningSource = viewModel.Sources?.FirstOrDefault(src => src.IsRunning);
                            if (runningSource != null && viewModel.CurrentCaptureMode == CaptureMode.RTSPStream)
                            {
                                Logger.Info($"🔄 View reloaded with running RTSP stream - checking playback for {RedactRtspCredentials(runningSource.FullUrl)}");
                                // Restart the stream only if we're still on the UI thread and view is loaded
                                if (this.IsLoaded)
                                {
                                    Logger.Info($"Attempting StartStreamAsync with URL: {RedactRtspCredentials(runningSource.FullUrl)}");
                                    await StartStreamAsync(runningSource.FullUrl, runningSource.Username, runningSource.Password);
                                    Logger.Info("✅ RTSP stream successfully restarted after navigation");
                                }
                                else
                                {
                                    Logger.Warning("View no longer loaded, skipping stream restart");
                                }
                            }
                            else
                            {
                                Logger.Debug($"No running RTSP stream found. RunningSource: {runningSource != null}, Mode: {viewModel.CurrentCaptureMode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error restarting RTSP stream on view reload: {ex.Message}");
                        }
                    }), DispatcherPriority.Background);
                }
                else
                {
                    Logger.Warning("DataContext is not AIWeatherPreviewViewModel");
                }
                
                // Refresh video layout when view becomes visible
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        UpdateVideoHostLayoutToFit();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Error refreshing video layout on load: {ex.Message}");
                    }
                }), DispatcherPriority.Loaded);
            };
            
            // Also refresh when visibility changes
            this.IsVisibleChanged += (s, e) =>
            {
                if (this.IsVisible)
                {
                    ResumePendingPreview();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            UpdateVideoHostLayoutToFit();
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Error refreshing video layout on visibility change: {ex.Message}");
                        }
                    }), DispatcherPriority.Loaded);
                }
            };
        }

        private void OnViewUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // NOTE: WPF can raise Unloaded during normal docking/layout changes (tab switching).
                // DO NOT stop the stream here - it should continue running in the background.
                // Only clean up when explicitly stopped by user or plugin shutdown.
                
                // Disposing LibVLC here can crash NINA on the next start (native AV).
                // DO NOT dispose resources here - let them persist across navigation.
                
                Logger.Debug("AI Weather view unloaded (navigated away) - keeping stream running");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in view unloaded handler: {ex.Message}");
            }
        }

        private void InitializeVLC()
        {
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                
                // Subscribe to VLC log events to see errors
                _libVLC.Log += (sender, e) =>
                {
                    var message = RedactRtspCredentials(e.Message);

                    // Some IP-camera streams slowly drift away from LibVLC's input clock. VLC
                    // then reports every decoded picture as late and drops every new frame,
                    // leaving the preview frozen on its last good frame even though the RTSP
                    // source and the independent OpenCV analysis path are still healthy. Do not
                    // write thousands of identical lines per minute; aggregate the burst and
                    // rebuild only the preview player if it remains unhealthy.
                    if (RtspPreviewHealthMonitor.IsLateFrameMessage(message))
                    {
                        var observation = _previewHealthMonitor.Observe(message, DateTime.UtcNow);
                        if (observation.ShouldLogSummary)
                        {
                            Logger.Warning(
                                $"VLC preview timing is late: {observation.LateMessageCount} messages over " +
                                $"{observation.LateDuration.TotalSeconds:0.0}s; latest: {message}");
                        }

                        if (observation.ShouldRecover)
                        {
                            SchedulePreviewRecovery(observation);
                        }

                        return;
                    }

                    // LibVLC can be noisy with benign messages; don't surface these as warnings/errors.
                    // The decoder's timing chatter goes with them: "picture is too late", late
                    // frames dropped, PCR/clock complaints, buffer deadlocks. One night of a
                    // Tapo stream produced 1,700 of those lines and nothing anyone could act on,
                    // burying the eight session drops that mattered. Network events stay visible.
                    if (message.Contains("unsupported control query", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("surface dimensions", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("SetThumbNailClip failed", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("too late", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("late frames", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("late video", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("PCR", StringComparison.Ordinal)
                        || message.Contains("pts_delay", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("reference clock", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("convert timestamp", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("Timestamp conversion failed", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("early picture", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("buffer deadlock", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("MatchingDeviceId", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("Password in a URI", StringComparison.OrdinalIgnoreCase))
                    {
                        Logger.Debug($"VLC: {message}");
                        return;
                    }

                    // VLC is extremely chatty; only surface real warnings/errors.
                    if (e.Level == LogLevel.Error)
                    {
                        Logger.Error($"VLC: {message}");
                        return;
                    }

                    if (e.Level == LogLevel.Warning)
                    {
                        Logger.Warning($"VLC: {message}");
                        return;
                    }

                    Logger.Debug($"VLC: {message}");
                };
                
                Logger.Info("LibVLC initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize LibVLC: {ex.Message}");
            }
        }

        private void SchedulePreviewRecovery(RtspPreviewHealthObservation observation)
        {
            Volatile.Write(ref _previewUnhealthy, 1);
            if (Interlocked.CompareExchange(ref _previewRecoveryScheduled, 1, 0) != 0)
            {
                return;
            }

            Logger.Warning(
                $"RTSP preview watchdog detected sustained late-frame dropping " +
                $"({observation.LateMessageCount} messages over {observation.LateDuration.TotalSeconds:0.0}s); " +
                "scheduling a preview-only restart. Weather analysis and safety state are not restarted.");

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                Interlocked.Exchange(ref _previewRecoveryScheduled, 0);
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var playbackUrl = _activePlaybackUrl;
                    if (string.IsNullOrWhiteSpace(playbackUrl) || _videoHost == null)
                    {
                        Logger.Debug("RTSP preview watchdog recovery skipped because no preview player is active");
                        return;
                    }

                    if (!IsLoaded || !IsVisible)
                    {
                        // Keep _previewUnhealthy set. The normal Loaded handler will see it and
                        // rebuild the player instead of accepting IsPlaying from a frozen vout.
                        Logger.Info("RTSP preview watchdog deferred recovery until the preview view is visible");
                        return;
                    }

                    Logger.Warning($"RTSP preview watchdog is rebuilding {RedactRtspCredentials(playbackUrl)}");
                    await StartStreamAsync(
                        playbackUrl,
                        cancellationToken: CancellationToken.None,
                        forceRestart: true);
                }
                catch (Exception ex)
                {
                    Logger.Error($"RTSP preview watchdog restart failed: {ex.Message}", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _previewRecoveryScheduled, 0);
                }
            }), DispatcherPriority.Background);
        }

        public void StartStream(string rtspUrl, string? username = null, string? password = null)
        {
            // Backward-compatible fire-and-forget entrypoint used by the ViewModel.
            StartStreamObservedAsync(rtspUrl, username, password);
        }

        private async void StartStreamObservedAsync(string url, string? user, string? password)
        {
            try { await StartStreamAsync(url, user, password); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warning($"Preview start failed: {ex.GetType().Name}"); }
        }

        public async Task StartStreamAsync(
            string rtspUrl,
            string? username = null,
            string? password = null,
            CancellationToken cancellationToken = default,
            bool forceRestart = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => StartStreamAsync(rtspUrl, username, password, cancellationToken, forceRestart)).Task.Unwrap();
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var playbackUrl = BuildAuthenticatedUrl(rtspUrl, username, password);
            if (_isStartingStream && !forceRestart &&
                string.Equals(_startingPlaybackUrl, playbackUrl, StringComparison.Ordinal))
                return; // Loaded/SetView/Vout must not queue a second restart of the same player.
            var request = _startupState.Start();
            if (_isStartingStream) _startCts?.Cancel();

            if (!IsOnScreen())
            {
                _pendingPreview = (rtspUrl, username, password);
                ReportPreviewStatus(UiLocalization.Text("Preview.VideoConnecting"));
                Logger.Debug("RTSP preview start deferred until its view is attached to a visible window");
                return;
            }
            _pendingPreview = null;
            _requestedPreview = (rtspUrl, username, password);

            await _streamGate.WaitAsync(cancellationToken);
            try
            {
                if (!_startupState.IsCurrent(request))
                    throw new OperationCanceledException("Preview start superseded by stop or another start");

                _isStartingStream = true;
                _startingPlaybackUrl = playbackUrl;
                ReportPreviewStatus(UiLocalization.Text("Preview.VideoConnecting"));

                Logger.Info($"StartStream called with URL: {RedactRtspCredentials(rtspUrl)}");
                Logger.Info($"Authentication: {(string.IsNullOrWhiteSpace(username) ? "not configured separately" : "configured")}");

                if (_libVLC == null)
                {
                    Logger.Error("LibVLC not initialized - cannot start stream");
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoLibVlcUnavailable"));
                    return;
                }

                if (VideoPanel == null)
                {
                    Logger.Error("VideoPanel is null - XAML element not found!");
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoViewUnavailable"));
                    return;
                }

                Logger.Info($"VideoPanel found. Size: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight}");

                if (!forceRestart
                    && Volatile.Read(ref _previewUnhealthy) == 0
                    && _videoHost?.Player?.IsPlaying == true
                    && string.Equals(_activePlaybackUrl, playbackUrl, StringComparison.Ordinal))
                {
                    Logger.Debug("RTSP preview is already playing this source; skipping duplicate restart");
                    UpdateVideoHostLayoutToFit();
                    ReportPreviewStatus(null);
                    return;
                }

                // Cancel any previous start loop before tearing down the current player/host.
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                await StopStreamCoreAsync();
                if (!_startupState.IsCurrent(request)) throw new OperationCanceledException();
                cancellationToken.ThrowIfCancellationRequested();

                // Create a fresh CTS for this start attempt. StopStreamCoreAsync clears _startCts.
                _startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var startToken = _startCts.Token;

                // Reserve this endpoint before LibVLC opens it. The safety monitor uses
                // the reservation to release an idle background OpenCV decoder and prefer
                // frames from the decoder that is already proven healthy.
                _sharedPreviewFrameRegistration =
                    await SharedRtspPreviewFrameProvider.Instance.RegisterAsync(
                        playbackUrl,
                        CaptureCurrentPreviewFrameAsync,
                        startToken,
                        () => Volatile.Read(ref _previewCanSnapshot) != 0);
                var sharedSourceIdentity =
                    SharedRtspPreviewFrameProvider.CreateSourceIdentity(playbackUrl);
                Logger.Info(
                    $"Reserved RTSP source " +
                    $"{SharedRtspPreviewFrameProvider.Fingerprint(sharedSourceIdentity)} " +
                    "for shared preview and AI capture");

                Logger.Info("Creating MediaPlayer and VideoHost...");
                Logger.Debug("Creating LibVLC MediaPlayer...");

                var player = new MediaPlayer(_libVLC)
                {
                    Volume = 0,
                    EnableHardwareDecoding = true
                };

                player.EncounteredError += OnPreviewPlaybackLost;
                player.EndReached += OnPreviewPlaybackLost;
                player.Stopped += OnPreviewPlaybackLost;
                Logger.Debug("MediaPlayer created");

                VideoPanel.Visibility = Visibility.Visible;
                CameraImage.Visibility = Visibility.Collapsed;

                Logger.Debug("Creating VideoHwndHost...");
                // Upstream's single layered HWND: no nested target, cover, clipping or reveal gate.
                _videoHost = new VideoHwndHost(player)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top
                };
                _previewHealthMonitor.ResetBurst();
                Volatile.Write(ref _previewUnhealthy, 0);
                player.Vout += Player_Vout;
                Logger.Info($"VideoHost created (Panel: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight})");

                Logger.Info("Adding VideoHost to VideoPanel...");
                VideoPanel.Children.Add(_videoHost);
                _videoHost.Margin = new Thickness(0);

                await Dispatcher.Yield(DispatcherPriority.Loaded);

                IntPtr hwnd = IntPtr.Zero;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    startToken.ThrowIfCancellationRequested();
                    hwnd = _videoHost.Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        break;
                    }
                    await Task.Delay(20, startToken);
                }

                Logger.Info($"VideoHost handle resolved: {hwnd}");
                if (hwnd == IntPtr.Zero)
                {
                    Logger.Error("VideoHost handle never became available; aborting playback setup");
                    await StopStreamCoreAsync();
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoSurfaceUnavailable"));
                    return;
                }

                player.Hwnd = hwnd;
                Logger.Info($"Player Hwnd set to: {player.Hwnd}, Volume: {player.Volume}, HW decode: {player.EnableHardwareDecoding}");

                try
                {
                    UpdateVideoHostLayoutToFit();
                }
                catch (Exception ex)
                {
                    Logger.Debug($"VideoHost initial resize failed: {ex.Message}");
                }

                Logger.Info($"Creating media for URL: {RedactRtspCredentials(playbackUrl)}");

                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVLC, playbackUrl, FromType.FromLocation);
                _currentMedia.AddOption(":network-caching=1000");
                _currentMedia.AddOption(":rtsp-tcp");
                _currentMedia.AddOption(":no-audio");
                // This preview is a silent real-time safety-camera view, not synchronized A/V.
                // Disabling the input-clock correction prevents camera timestamp drift from
                // growing into a permanent late-frame drop loop after several hours.
                _currentMedia.AddOption(":clock-synchro=0");
                _currentMedia.AddOption(":clock-jitter=0");
                _currentMedia.AddOption(":drop-late-frames");
                _currentMedia.AddOption(":skip-frames");
                Logger.Info("Media created with options: network-caching=1000, rtsp-tcp, no-audio, clock-synchro=0, clock-jitter=0, drop-late-frames, skip-frames");

                Logger.Info("Starting playback...");
                var playResult = player.Play(_currentMedia);
                Logger.Info($"Play() returned: {playResult}, Player state: {player.State}");

                if (!playResult)
                {
                    Logger.Error("Play() returned false - VLC refused to play media");
                    await StopStreamCoreAsync();
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoOpenFailed"));
                    return;
                }

                for (int i = 0; i < 50; i++)
                {
                    startToken.ThrowIfCancellationRequested();
                    var state = player.State;
                    Logger.Debug($"Waiting for playback... State: {state}, IsPlaying: {player.IsPlaying}");

                    if (state == VLCState.Error || state == VLCState.Ended)
                    {
                        Logger.Error($"Player entered error/ended state: {state}");
                        break;
                    }

                    if (player.IsPlaying)
                    {
                        Volatile.Write(ref _previewCanSnapshot, 1);
                        Logger.Info("RTSP stream playing successfully!");
                        try
                        {
                            UpdateVideoHostLayoutToFit();
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Video fit layout update failed: {ex.Message}");
                        }
                        break;
                    }

                    await Task.Delay(100, startToken);
                }

                var finalState = player.State;
                var finalIsPlaying = player.IsPlaying;
                Logger.Info($"Stream startup complete. Final state: {finalState}, IsPlaying: {finalIsPlaying}");
                
                if (!finalIsPlaying)
                {
                    var errorMsg = "RTSP stream failed to start. ";
                    if (finalState == VLCState.Error)
                    {
                        errorMsg += "Possible causes: incorrect URL, wrong credentials, network unreachable, or unsupported codec.";
                    }
                    else
                    {
                        errorMsg += $"Player state: {finalState}. Check URL format and network connectivity.";
                    }
                    Logger.Warning(errorMsg);
                    Logger.Warning($"Troubleshooting tips:\n" +
                        $"  1. Verify RTSP URL is correct (e.g., rtsp://camera-ip:554/stream)\n" +
                        $"  2. Check username/password if authentication is required\n" +
                        $"  3. Ensure camera is reachable (ping the IP address)\n" +
                        $"  4. Try the URL in VLC media player to verify it works\n" +
                        $"  5. Some cameras require specific paths like /h264, /live, or /stream");

                    // The shared source reservation is deliberately installed before
                    // LibVLC opens the stream. If playback never becomes healthy, release
                    // that reservation as well as the failed player so headless monitoring
                    // can use its independent OpenCV path on the next analysis cycle.
                    await StopStreamCoreAsync();
                    ReportPreviewFailure(UiLocalization.Text("Preview.VideoOpenFailed"));
                    return;
                }
                else
                {
                    _activePlaybackUrl = playbackUrl;
                    _previewReconnectAttempt = 0;
                    _previewHealthMonitor.ResetBurst();
                    Volatile.Write(ref _previewUnhealthy, 0);
                    ReportPreviewStatus(null);
                }

                Logger.Info($"Started RTSP stream: {RedactRtspCredentials(playbackUrl)}");
            }
            catch (OperationCanceledException)
            {
                Logger.Info("StartStream canceled");
                await StopStreamCoreAsync();
                throw; // the caller must not mark a cancelled request running or start analysis
            }
            catch (UriFormatException ex)
            {
                Logger.Error($"Invalid RTSP URL format: {ex.Message}");
                Logger.Error("URL must be in format: rtsp://[username:password@]camera-ip[:port]/path");
                await StopStreamCoreAsync();
                ReportPreviewFailure(UiLocalization.Text("Preview.VideoUrlInvalid"));
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start RTSP stream: {ex.Message}", ex);
                Logger.Error("Common issues: Wrong URL, authentication failure, network error, or camera offline.");
                await StopStreamCoreAsync();
                ReportPreviewFailure(UiLocalization.Text("Preview.VideoOpenFailed"));
            }
            finally
            {
                _isStartingStream = false;
                _startingPlaybackUrl = null;
                if (_requestedPreview.HasValue && _videoHost?.Player?.IsPlaying != true)
                    SchedulePreviewReconnect();
                _streamGate.Release();
            }
        }

        private async Task<Bitmap?> CaptureCurrentPreviewFrameAsync(
            CancellationToken cancellationToken)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                return null;
            }

            var tempPath = Path.Combine(
                Path.GetTempPath(),
                $"aiweather_shared_preview_{Guid.NewGuid():N}.png");
            try
            {
                // TakeSnapshot queues a write from LibVLC. A few bounded attempts cover the
                // short interval between Play() succeeding and the first video output frame.
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryDeleteSnapshot(tempPath);

                    var requested = await Dispatcher.InvokeAsync(
                        () =>
                        {
                            var player = _videoHost?.Player;
                            if (player?.IsPlaying != true
                                || string.IsNullOrWhiteSpace(_activePlaybackUrl)
                                || Volatile.Read(ref _previewUnhealthy) != 0)
                            {
                                return false;
                            }

                            return player.TakeSnapshot(0, tempPath, 0, 0);
                        },
                        DispatcherPriority.Background,
                        cancellationToken);

                    if (!requested)
                    {
                        await Task.Delay(150, cancellationToken);
                        continue;
                    }

                    for (var wait = 0; wait < 30; wait++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var frame = await Task.Run(
                            () => TryLoadSnapshot(tempPath),
                            cancellationToken);
                        if (frame != null)
                        {
                            Logger.Debug(
                                $"Captured current AI frame from existing LibVLC preview: " +
                                $"{frame.Width}x{frame.Height}");
                            return frame;
                        }

                        await Task.Delay(50, cancellationToken);
                    }
                }

                Logger.Warning(
                    "Existing LibVLC preview did not produce a current snapshot within the bounded wait");
                return null;
            }
            finally
            {
                TryDeleteSnapshot(tempPath);
            }
        }

        private void ReportPreviewStatus(string? text, bool retryAvailable = false)
        {
            if (DataContext is AIWeatherPreviewViewModel viewModel)
            {
                viewModel.SetPreviewStreamStatus(text, retryAvailable);
            }
        }

        private void ReportPreviewFailure(string text)
        {
            ReportPreviewStatus(text, retryAvailable: true);
        }

        private static Bitmap? TryLoadSnapshot(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length <= 0)
                {
                    return null;
                }

                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var image = DrawingImage.FromStream(stream);
                return new Bitmap(image);
            }
            catch (IOException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                // The file exists but LibVLC has not finished writing a complete image.
                return null;
            }
        }

        private static void TryDeleteSnapshot(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best effort only. Every request uses a unique path.
            }
        }

        public void StopStream()
        {
            _ = StopStreamAsync();
        }

        public async Task StopStreamAsync()
        {
            // Log the call stack to understand who's calling this
            Logger.Info($"🛑 StopStreamAsync called - Stack trace: {Environment.StackTrace.Split('\n').Take(5).Aggregate((a, b) => a + "\n" + b)}");
            
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => StopStreamAsync()).Task.Unwrap();
                return;
            }

            _startupState.Stop();
            _requestedPreview = null;
            _pendingPreview = null;
            var reconnect = _previewReconnectCts;
            _previewReconnectCts = null;
            reconnect?.Cancel();
            _startCts?.Cancel();

            await _streamGate.WaitAsync();
            try
            {
                await StopStreamCoreAsync();
                _previewHealthMonitor.ResetAll();
                Volatile.Write(ref _previewUnhealthy, 0);
                ReportPreviewStatus(null);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to stop stream: {ex.Message}");
            }
            finally
            {
                _streamGate.Release();
            }
        }

        // Core stop logic. Caller must already be on UI thread.
        // If called from StartStreamAsync, it runs while holding _streamGate to avoid races.
        private async Task StopStreamCoreAsync()
        {
            try
            {
                _sharedPreviewFrameRegistration?.Dispose();
                _sharedPreviewFrameRegistration = null;
                Volatile.Write(ref _previewCanSnapshot, 0);
                _activePlaybackUrl = null;
                _previewHealthMonitor.ResetBurst();
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                if (_videoHost != null)
                {
                    var host = _videoHost;
                    _videoHost = null; // late Vout callbacks no longer own a surface
                    host.Visibility = Visibility.Collapsed;
                    var player = host.Player;
                    if (player != null)
                    {
                        player.EncounteredError -= OnPreviewPlaybackLost;
                        player.EndReached -= OnPreviewPlaybackLost;
                        player.Stopped -= OnPreviewPlaybackLost;
                    }

                    try
                    {
                        // Detach the native render target first to reduce odds of a blocking stop.
                        if (player != null)
                        {
                            player.Vout -= Player_Vout;
                            player.Hwnd = IntPtr.Zero;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error detaching MediaPlayer HWND: {ex.Message}");
                    }

                    try
                    {
                        VideoPanel?.Children.Remove(host);
                    }
                    catch
                    {
                        // best-effort
                    }

                    try
                    {
                        host.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing VideoHost: {ex.Message}");
                    }

                    _videoHost = null;

                    // Stop/Dispose can sometimes block. Do it off-UI with a timeout.
                    await StopAndDisposePlayerBestEffortAsync(player, TimeSpan.FromSeconds(2));
                }

                _currentMedia?.Dispose();
                _currentMedia = null;

                if (VideoPanel != null)
                {
                    VideoPanel.Visibility = Visibility.Collapsed;
                }

                if (CameraImage != null)
                {
                    CameraImage.Visibility = Visibility.Visible;
                }

                Logger.Info("Stopped RTSP stream");
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to stop stream: {ex.Message}");
            }
        }

        private static async Task StopAndDisposePlayerBestEffortAsync(MediaPlayer? player, TimeSpan timeout)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                var stopTask = Task.Run(() =>
                {
                    try
                    {
                        player.Stop();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error stopping MediaPlayer: {ex.Message}");
                    }

                    try
                    {
                        player.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing MediaPlayer: {ex.Message}");
                    }
                });

                var completed = await Task.WhenAny(stopTask, Task.Delay(timeout)) == stopTask;
                if (!completed)
                {
                    Logger.Warning("MediaPlayer stop/dispose timed out; continuing cleanup to avoid UI hang");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Unexpected error stopping/disposing MediaPlayer: {ex.Message}");
            }
        }

        private static string BuildAuthenticatedUrl(string rtspUrl, string? username, string? password)
        {
            try
            {
                var uri = new Uri(rtspUrl);
                if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
                {
                    var builder = new UriBuilder(uri)
                    {
                        UserName = username,
                        Password = password
                    };
                    return builder.Uri.ToString();
                }

                if (string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/")
                {
                    Logger.Warning($"RTSP URL has no path component. Most cameras need a path like /stream or /live. Current URL: {RedactRtspCredentials(rtspUrl)}");
                }

                return rtspUrl;
            }
            catch
            {
                return rtspUrl;
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox && passwordBox.DataContext is CameraSource source)
            {
                source.Password = passwordBox.Password;
                Logger.Debug($"Password updated for camera source: {RedactRtspCredentials(source.FullUrl)}");
                try
                {
                    Properties.Settings.Default.RtspPassword = source.Password ?? string.Empty;
                    CoreUtil.SaveSettings(Properties.Settings.Default);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to persist RTSP password from preview: {ex.Message}");
                }
            }
        }

        private static string RedactRtspCredentials(string? input)
        {
            return LogRedactor.RedactRtspUrl(input);
        }

        private void Player_Vout(object? sender, MediaPlayerVoutEventArgs e)
        {
            if (sender is not MediaPlayer player || e.Count == 0 ||
                Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(_videoHost?.Player, player)) return;
                try { UpdateVideoHostLayoutToFit(); }
                catch (Exception ex) { Logger.Debug($"RTSP layout failed: {ex.GetType().Name}"); }
            }), DispatcherPriority.Render);
        }

        private void PasswordBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is PasswordBox passwordBox && passwordBox.DataContext is CameraSource source)
            {
                passwordBox.Password = source.Password;
            }
        }

        private void VideoPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                UpdateVideoHostLayoutToFit();
            }
            catch (Exception ex)
            {
                Logger.Debug($"VideoPanel resize handler failed: {ex.Message}");
            }
        }

        // Keep the useful complete-frame fit; rendering itself is upstream's single HWND.
        // No startup mask, snapshot gate, child-window probes or repeated reveal tasks.
        private void UpdateVideoHostLayoutToFit()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(UpdateVideoHostLayoutToFit);
                return;
            }
            if (_videoHost == null || VideoPanel == null) return;
            var width = VideoPanel.ActualWidth;
            var height = VideoPanel.ActualHeight;
            if (width <= 1 || height <= 1) return;
            try
            {
                uint videoWidth = 0, videoHeight = 0;
                _videoHost.Player.Size(0, ref videoWidth, ref videoHeight);
                if (videoWidth > 0 && videoHeight > 0)
                {
                    var fit = VideoFitCalculator.FitInside(width, height, videoWidth, videoHeight);
                    width = fit.Width;
                    height = fit.Height;
                }
                _videoHost.HorizontalAlignment = HorizontalAlignment.Center;
                _videoHost.VerticalAlignment = VerticalAlignment.Center;
                _videoHost.Width = width;
                _videoHost.Height = height;
                _videoHost.Margin = new Thickness(0);
                _videoHost.ResizeTo(width, height);
                _videoHost.Player.Scale = 0; // VLC fits; it must never crop the analysis field of view.
            }
            catch (Exception ex) { Logger.Debug($"RTSP fit layout unavailable: {ex.GetType().Name}"); }
        }
    }
}
