using System;
using System.ComponentModel.Composition;
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
using System.Windows.Controls.Primitives;
using MediaColor = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

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
        private CancellationTokenSource? _startCts;
        private Media? _currentMedia;
        private string? _activePlaybackUrl;
        private VideoHostLayout? _lastLoggedVideoLayout;
        private bool _videoSurfaceReady;

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

                    // LibVLC can be noisy with benign messages; don't surface these as warnings/errors.
                    if (message.Contains("unsupported control query", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("surface dimensions", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("SetThumbNailClip failed", StringComparison.OrdinalIgnoreCase))
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

        public void StartStream(string rtspUrl, string? username = null, string? password = null)
        {
            // Backward-compatible fire-and-forget entrypoint used by the ViewModel.
            _ = StartStreamAsync(rtspUrl, username, password);
        }

        public async Task StartStreamAsync(string rtspUrl, string? username = null, string? password = null, CancellationToken cancellationToken = default)
        {
            if (!Dispatcher.CheckAccess())
            {
                await Dispatcher.InvokeAsync(() => StartStreamAsync(rtspUrl, username, password, cancellationToken)).Task.Unwrap();
                return;
            }

            await _streamGate.WaitAsync(cancellationToken);
            try
            {
                if (_isStartingStream)
                {
                    Logger.Warning("StartStream ignored because a start is already in progress");
                    return;
                }

                _isStartingStream = true;

                Logger.Info($"StartStream called with URL: {RedactRtspCredentials(rtspUrl)}");
                Logger.Info($"Authentication: {(string.IsNullOrWhiteSpace(username) ? "not configured separately" : "configured")}");

                if (_libVLC == null)
                {
                    Logger.Error("LibVLC not initialized - cannot start stream");
                    return;
                }

                if (VideoPanel == null)
                {
                    Logger.Error("VideoPanel is null - XAML element not found!");
                    return;
                }

                Logger.Info($"VideoPanel found. Size: {VideoPanel.ActualWidth}x{VideoPanel.ActualHeight}");

                var playbackUrl = BuildAuthenticatedUrl(rtspUrl, username, password);
                if (_videoHost?.Player?.IsPlaying == true
                    && string.Equals(_activePlaybackUrl, playbackUrl, StringComparison.Ordinal))
                {
                    Logger.Debug("RTSP preview is already playing this source; skipping duplicate restart");
                    UpdateVideoHostLayoutToFit();
                    return;
                }

                // Cancel any previous start loop before tearing down the current player/host.
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                await StopStreamCoreAsync();

                // Create a fresh CTS for this start attempt. StopStreamCoreAsync clears _startCts.
                _startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var startToken = _startCts.Token;

                Logger.Info("Creating MediaPlayer and VideoHost...");
                Logger.Debug("Creating LibVLC MediaPlayer...");

                var player = new MediaPlayer(_libVLC)
                {
                    Volume = 0,
                    EnableHardwareDecoding = true
                };

                Logger.Debug("MediaPlayer created");

                VideoPanel.Visibility = Visibility.Visible;
                CameraImage.Visibility = Visibility.Collapsed;

                Logger.Debug("Creating VideoHwndHost...");
                // Give the native host a conventional video-shaped viewport from the outset.
                // HwndHost pixels cannot be transparently composed with WPF, so allowing the
                // native host to fill a tall panel would expose its white unused area before
                // dimensions arrive. The surrounding VideoPanel remains genuine N.I.N.A. WPF.
                var initialViewport = VideoFitCalculator.FitInside(
                    VideoPanel.ActualWidth,
                    VideoPanel.ActualHeight,
                    16,
                    9);
                _videoHost = new VideoHwndHost(player, ResolveVideoBackgroundColor())
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Width = initialViewport.Width,
                    Height = initialViewport.Height
                };
                _videoSurfaceReady = false;
                _lastLoggedVideoLayout = null;
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
                    hwnd = _videoHost.VideoHwnd;
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
                    return;
                }

                player.Hwnd = hwnd;
                Logger.Info($"Player dedicated video-target Hwnd set to: {player.Hwnd}, Volume: {player.Volume}, HW decode: {player.EnableHardwareDecoding}");

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
                Logger.Info("Media created with options: network-caching=1000, rtsp-tcp, no-audio");

                Logger.Info("Starting playback...");
                var playResult = player.Play(_currentMedia);
                Logger.Info($"Play() returned: {playResult}, Player state: {player.State}");

                if (!playResult)
                {
                    Logger.Error("Play() returned false - VLC refused to play media");
                    await StopStreamCoreAsync();
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
                        Logger.Info("RTSP stream playing successfully!");
                        try
                        {
                            await RefreshVideoHostLayoutAfterVoutAsync(player, startToken);
                        }
                        catch (OperationCanceledException) when (startToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            Logger.Debug($"Video fill layout update failed: {ex.Message}");
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
                }
                else
                {
                    _activePlaybackUrl = playbackUrl;
                }

                Logger.Info($"Started RTSP stream: {RedactRtspCredentials(playbackUrl)}");
            }
            catch (OperationCanceledException)
            {
                Logger.Info("StartStream canceled");
            }
            catch (UriFormatException ex)
            {
                Logger.Error($"Invalid RTSP URL format: {ex.Message}");
                Logger.Error("URL must be in format: rtsp://[username:password@]camera-ip[:port]/path");
                await StopStreamCoreAsync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to start RTSP stream: {ex.Message}", ex);
                Logger.Error("Common issues: Wrong URL, authentication failure, network error, or camera offline.");
                await StopStreamCoreAsync();
            }
            finally
            {
                _isStartingStream = false;
                _streamGate.Release();
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

            await _streamGate.WaitAsync();
            try
            {
                await StopStreamCoreAsync();
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
                _activePlaybackUrl = null;
                _startCts?.Cancel();
                _startCts?.Dispose();
                _startCts = null;

                if (_videoHost != null)
                {
                    var host = _videoHost;
                    var player = host.Player;

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
                    _lastLoggedVideoLayout = null;
                    _videoSurfaceReady = false;

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

        private MediaColor ResolveVideoBackgroundColor()
        {
            // HwndHost is a native airspace and cannot inherit a transparent WPF
            // background. Mirror N.I.N.A.'s active secondary background into the native
            // child window so letterbox/pillarbox regions follow light and dark themes.
            if (VideoPanel?.Background is SolidColorBrush renderedPanelBrush)
            {
                Logger.Info($"RTSP native host background resolved from rendered VideoPanel: {renderedPanelBrush.Color}");
                return renderedPanelBrush.Color;
            }

            if (TryFindResource("SecondaryBackgroundBrush") is SolidColorBrush localBrush)
            {
                Logger.Info($"RTSP native host background resolved from local theme resource: {localBrush.Color}");
                return localBrush.Color;
            }

            if (Application.Current?.TryFindResource("SecondaryBackgroundBrush") is SolidColorBrush appBrush)
            {
                Logger.Info($"RTSP native host background resolved from application theme resource: {appBrush.Color}");
                return appBrush.Color;
            }

            Logger.Warning("RTSP native host background resource was unavailable; using dark fallback #202B30");
            return MediaColor.FromRgb(32, 43, 48);
        }

        private void Player_Vout(object? sender, MediaPlayerVoutEventArgs e)
        {
            if (sender is not MediaPlayer player)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var token = _startCts?.Token ?? CancellationToken.None;
                    await RefreshVideoHostLayoutAfterVoutAsync(player, token);
                }
                catch (OperationCanceledException)
                {
                    // Stream teardown cancels any pending first-frame layout work.
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Deferred RTSP video layout failed: {ex.Message}");
                }
            }), DispatcherPriority.Background);
        }

        private async Task<bool> RefreshVideoHostLayoutAfterVoutAsync(
            MediaPlayer player,
            CancellationToken cancellationToken)
        {
            // Vout/Playing can be raised just before libvlc_video_get_size starts returning
            // dimensions. The dedicated target is full-sized under a native theme cover while
            // polling, so VLC can initialize without exposing its white startup surface.
            // This camera/LibVLC combination can create its visible output child several
            // seconds after the first Vout notification. Keep probing long enough to catch it;
            // the theme cover remains in place throughout, so the wait cannot expose white.
            for (var attempt = 0; attempt < 600; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_videoHost == null || !ReferenceEquals(_videoHost.Player, player))
                {
                    return false;
                }

                // Multiple Vout/Playing notifications may schedule overlapping probes. Once
                // one probe has revealed a ready surface, later probes must never reapply the
                // startup clip and blank an already playing preview.
                if (_videoSurfaceReady)
                {
                    if (TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
                    {
                        _videoHost.ShowVideoSurface();
                        return true;
                    }
                }

                // LibVLC can reorder its native child hierarchy while vout is being created.
                // Reassert the solid startup cover until a real output surface is measurable,
                // otherwise the future 16:9 viewport briefly becomes a white rectangle.
                _videoHost.ShowStartupCover();

                if (TryGetCurrentVideoSize(player, out _, out _))
                {
                    // Keep the native target under the theme cover while Direct3D prepares the
                    // first frame. Raising it only after this warm-up avoids a white flash.
                    await Task.Delay(250, cancellationToken);
                    if (_videoHost != null && ReferenceEquals(_videoHost.Player, player))
                    {
                        _videoSurfaceReady = true;
                        if (TryUpdateVideoHostLayoutToFit(allowBeforeReady: true))
                        {
                            _videoHost.ShowVideoSurface();
                            return true;
                        }
                    }
                }

                await Task.Delay(100, cancellationToken);
            }

            Logger.Warning("RTSP video dimensions were unavailable 60 seconds after Vout; preview surface remains behind the theme cover instead of showing a white native window");
            return false;
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
                TryUpdateVideoHostLayoutToFit();
            }
            catch (Exception ex)
            {
                Logger.Debug($"VideoPanel resize handler failed: {ex.Message}");
            }
        }

        // Resize the native video output so the complete frame remains visible.
        // Any aspect-ratio mismatch is shown as letterboxing/pillarboxing instead of cropping.
        private void UpdateVideoHostLayoutToFit()
        {
            TryUpdateVideoHostLayoutToFit();
        }

        private bool TryUpdateVideoHostLayoutToFit(bool allowBeforeReady = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                return Dispatcher.Invoke(() => TryUpdateVideoHostLayoutToFit(allowBeforeReady));
            }

            if (_videoHost == null || VideoPanel == null)
            {
                return false;
            }

            if (!_videoSurfaceReady && !allowBeforeReady)
            {
                return false;
            }

            var panelWidth = VideoPanel.ActualWidth;
            var panelHeight = VideoPanel.ActualHeight;
            if (panelWidth <= 1 || panelHeight <= 1)
            {
                return false;
            }

            if (!TryGetCurrentVideoSize(_videoHost.Player, out var videoWidth, out var videoHeight))
            {
                // The dedicated VLC target stays behind the native theme cover until dimensions
                // exist. Exposing the target here caused the startup white flash.
                return false;
            }

            // Resize the entire native airspace to the video aspect. The surrounding pixels are
            // then genuine WPF VideoPanel pixels, so neither the system host nor LibVLC can ever
            // repaint the letterbox area white.
            var fittedViewport = VideoFitCalculator.FitInside(
                panelWidth,
                panelHeight,
                videoWidth,
                videoHeight);
            _videoHost.HorizontalAlignment = HorizontalAlignment.Center;
            _videoHost.VerticalAlignment = VerticalAlignment.Center;
            _videoHost.Width = fittedViewport.Width;
            _videoHost.Height = fittedViewport.Height;
            VideoPanel.UpdateLayout();
            _videoHost.UpdateLayout();

            // VLC still receives a dedicated inner child rather than the HwndHost itself. That
            // keeps its generated native hierarchy isolated from the WPF layout lifecycle.
            if (!_videoHost.TrySetVideoContentSize(videoWidth, videoHeight, out var layout))
            {
                return false;
            }

            if (_lastLoggedVideoLayout != layout)
            {
                Logger.Info(
                    $"RTSP preview nested fit: themed host {layout.ContainerWidth}x{layout.ContainerHeight}, " +
                    $"video {videoWidth:0}x{videoHeight:0}, dedicated VLC target " +
                    $"{layout.VideoWidth}x{layout.VideoHeight} at ({layout.X},{layout.Y})");
                _lastLoggedVideoLayout = layout;
            }

            try
            {
                // Zero means VLC automatically fits the picture to the HWND. Because the HWND
                // now has the same aspect ratio, the picture fills it without native bars.
                _videoHost.Player.Scale = 0;
            }
            catch
            {
                // best-effort
            }

            // Force layout update
            _videoHost.UpdateLayout();
            VideoPanel.UpdateLayout();
            return true;
        }

        private bool TryGetCurrentVideoSize(
            MediaPlayer player,
            out double videoWidth,
            out double videoHeight)
        {
            videoWidth = 0;
            videoHeight = 0;
            try
            {
                uint width = 0;
                uint height = 0;
                player.Size(0, ref width, ref height);
                videoWidth = width;
                videoHeight = height;
                if (width > 0 && height > 0)
                {
                    return true;
                }
            }
            catch
            {
                // Fall through to the native VLC output-window probe below. Some LibVLC builds
                // render correctly but keep video_get_size at 0 for an HWND target.
            }

            return _videoHost != null
                && ReferenceEquals(_videoHost.Player, player)
                && _videoHost.TryGetRenderedVideoSize(out videoWidth, out videoHeight);
        }
    }
}
