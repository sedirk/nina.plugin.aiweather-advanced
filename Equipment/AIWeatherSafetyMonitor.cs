using NINA.Equipment.Interfaces;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Image.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;

namespace AIWeather.Equipment
{
    /// <summary>
    /// All Sky Camera Weather Monitor
    /// Monitors weather conditions and writes status to file
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public class AIWeatherSafetyMonitor : BaseINPC, ISafetyMonitor
    {
        private static AIWeatherSafetyMonitor? _instance;
        public static AIWeatherSafetyMonitor Instance => _instance ??= new AIWeatherSafetyMonitor();

        private readonly UnifiedCaptureService _captureService;
        private readonly WeatherAnalysisOrchestrator _analysisOrchestrator;
        private readonly TeacherStudentDatasetRecorder _datasetRecorder;
        private IWeatherAnalysisService _analysisService;
        private IWeatherAnalysisService? _initializedAnalysisService;
        private Timer? _monitoringTimer;
        private WeatherAnalysisResult? _lastResult;
        private Bitmap? _lastImage;
        private bool _isMonitoring = false;
        private bool _isCurrentlySafe = false;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _checkGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _replicaPollGate = new SemaphoreSlim(1, 1);
        private IProfileService? _profileService;
        private WeatherAnalysisBundle? _lastAnalysisBundle;
        private AstroContext? _lastAstroContext;
        private bool _isSolarAltitudeSuspended;
        private bool _solarContextUnavailable;
        private double? _currentSunAltitude;
        private double _activeSunAltitudeLimit = SolarAltitudeGuard.DefaultLimitDegrees;
        private ClusterNodeMode _connectedNodeMode = ClusterNodeMode.Standalone;
        private AIWeatherClusterServer? _clusterServer;
        private AIWeatherClusterClient? _clusterClient;
        private AIWeatherClusterSnapshot? _replicaSnapshot;
        private DateTime _replicaLastReceivedUtc = DateTime.MinValue;
        private AIWeatherReplicaFailure _replicaFailure = AIWeatherReplicaFailure.Waiting;
        private string _replicaLastError = string.Empty;
        private long _clusterSequence;

        // When the last analysis actually succeeded. The sky verdict expires: a monitor that
        // keeps answering with the last known state after its camera died reports SAFE all
        // night on data from before the failure, which is the one thing a safety monitor
        // must never do.
        private DateTime _lastAnalysisUtc = DateTime.MinValue;
        private bool _staleLogged;

        // Optional external ASCOM safety monitor, ANDed with the sky verdict so the two
        // protections are independent: this plugin watches the sky, the external device
        // watches whatever it was built to watch (humidity, dew point, rain sensor).
        private readonly AscomSafetyMonitorClient _externalMonitor = new AscomSafetyMonitorClient();
        private readonly object _externalGate = new object();
        private bool _externalSafeCached;
        private DateTime _externalReadUtc = DateTime.MinValue;
        private DateTime _externalConnectAttemptUtc = DateTime.MinValue;
        private bool _externalFailureLogged;

        /// <summary>IsSafe is polled often; a COM read per poll would hammer the driver.</summary>
        private static readonly TimeSpan ExternalReadCacheDuration = TimeSpan.FromSeconds(5);

        /// <summary>How long to wait before retrying a driver that failed to connect or read.</summary>
        private static readonly TimeSpan ExternalReconnectInterval = TimeSpan.FromSeconds(30);

        /// <summary>Floor for the automatic data-age limit, whatever the check interval.</summary>
        private static readonly TimeSpan MinimumAutomaticDataAge = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Fired after Connect succeeds and periodic monitoring has started.
        /// </summary>
        public event EventHandler? MonitoringStarted;

        public AIWeatherSafetyMonitor()
        {
            _captureService = new UnifiedCaptureService(cameraMediator: null);
            _analysisOrchestrator = new WeatherAnalysisOrchestrator();
            _datasetRecorder = new TeacherStudentDatasetRecorder(
                logger: new NinaDatasetRecorderLogger());
            _analysisService = new LocalWeatherAnalysisService();
            _datasetRecorder.StatusChanged += (_, _) =>
            {
                RaisePropertyChanged(nameof(DatasetStatus));
                RaisePropertyChanged(nameof(DatasetStatusText));
            };
            
            // Subscribe to settings changes
            Properties.Settings.Default.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(Properties.Settings.Default.UseGitHubModels)
                    || e.PropertyName == nameof(Properties.Settings.Default.AnalysisProvider)
                    || e.PropertyName == nameof(Properties.Settings.Default.SelectedModel)
                    || e.PropertyName == nameof(Properties.Settings.Default.GitHubToken)
                    || e.PropertyName == nameof(Properties.Settings.Default.OpenAIKey)
                    || e.PropertyName == nameof(Properties.Settings.Default.GeminiKey)
                    || e.PropertyName == nameof(Properties.Settings.Default.GeminiRequestEveryChecks)
                    || e.PropertyName == nameof(Properties.Settings.Default.AnthropicKey))
                {
                    UpdateAnalysisService();
                }

                if (!string.IsNullOrWhiteSpace(e.PropertyName)
                    && e.PropertyName.StartsWith("Dataset", StringComparison.Ordinal))
                {
                    _datasetRecorder.NotifyConfigurationChanged();
                    RaisePropertyChanged(nameof(DatasetStatus));
                    RaisePropertyChanged(nameof(DatasetStatusText));
                }

                if (e.PropertyName == nameof(Properties.Settings.Default.UseSunAltitudeLimit)
                    || e.PropertyName == nameof(Properties.Settings.Default.SunAltitudeLimitDegrees))
                {
                    RaisePropertyChanged(nameof(IsSolarAltitudeSuspended));
                    RaisePropertyChanged(nameof(CurrentSunAltitude));
                    RaisePropertyChanged(nameof(SunAltitudeLimitDegrees));
                    RequestImmediateWeatherCheck();
                }
            };
        }

        /// <summary>
        /// Injects NINA's image data factory for proper FITS/TIFF loading with debayering and stretching.
        /// Called from the MEF-constructed provider.
        /// </summary>
        public void SetImageDataFactory(IImageDataFactory imageDataFactory)
        {
            _captureService.SetImageDataFactory(imageDataFactory);
        }

        /// <summary>
        /// Injects NINA's profile service for accessing observer location (lat/lon/elevation).
        /// Called from the MEF-constructed provider.
        /// </summary>
        public void SetProfileService(IProfileService profileService)
        {
            _profileService = profileService;
        }

        private void UpdateAnalysisService()
        {
            var provider = Properties.Settings.Default.AnalysisProvider;
            if (string.IsNullOrWhiteSpace(provider))
            {
                provider = Properties.Settings.Default.UseGitHubModels ? "GitHubModels" : "Local";
            }

            provider = provider.Trim();
            var model = Properties.Settings.Default.SelectedModel;

            if (string.Equals(provider, "GitHubModels", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new GitHubModelsAnalysisService(
                    Properties.Settings.Default.GitHubToken,
                    model);
                return;
            }

            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new OpenAIAnalysisService(
                    Properties.Settings.Default.OpenAIKey,
                    model);
                return;
            }

            if (string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new GeminiAnalysisService(
                    Properties.Settings.Default.GeminiKey,
                    model,
                    Properties.Settings.Default.GeminiRequestEveryChecks);
                return;
            }

            if (string.Equals(provider, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new AnthropicAnalysisService(
                    Properties.Settings.Default.AnthropicKey,
                    model);
                return;
            }

            if (string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                _analysisService = new OllamaAnalysisService(
                    Properties.Settings.Default.OllamaBaseUrl,
                    model,
                    Properties.Settings.Default.OllamaDisableThinking);
                return;
            }

            _analysisService = new LocalWeatherAnalysisService();
        }

        private async Task<IWeatherAnalysisService> EnsureAnalysisServiceInitializedAsync(
            CancellationToken cancellationToken)
        {
            // Option bindings can change SelectedModel/provider credentials while the
            // monitor is connected. UpdateAnalysisService then replaces the service with a
            // fresh instance; without initializing that instance, every later check silently
            // falls back to the local analyzer until the monitor is manually reconnected.
            var service = _analysisService;
            if (ReferenceEquals(_initializedAnalysisService, service))
            {
                return service;
            }

            var configuredService = service;
            var analysisReady = await service.InitializeAsync(cancellationToken);
            if (!analysisReady)
            {
                if (service is IOnlineWeatherAnalysisService)
                {
                    // Preserve the configured teacher instance. Its online-only attempt will
                    // carry the initialization failure into the explicit orchestrator, which
                    // runs the student and records unambiguous provenance.
                    Logger.Warning("Selected online teacher failed to initialize; explicit local fallback will be used");
                }
                else
                {
                    Logger.Warning("Selected analysis provider failed to initialize; falling back to local analysis");
                    service = new LocalWeatherAnalysisService();
                    await service.InitializeAsync(cancellationToken);

                    // Do not overwrite a newer provider instance if a setting changed while the
                    // previous instance was being initialized. The newer instance will be picked
                    // up and initialized on the next serialized weather check.
                    if (ReferenceEquals(_analysisService, configuredService))
                    {
                        _analysisService = service;
                    }
                }
            }

            _initializedAnalysisService = service;
            return service;
        }

        #region ISafetyMonitor Implementation

        public string Category => UiLocalization.Text("Equipment.Category");
        public bool HasSetupDialog => true;
        public string Id => "AIWeatherSafetyMonitor";
        public string Name => UiLocalization.Text("Equipment.Name");
        public string Description => UiLocalization.Text("Equipment.Description");
        public string DriverInfo => UiLocalization.Text("Equipment.DriverInfo") + " v1.0";
        public string DriverVersion => "1.0.0";

        private bool _connected = false;
        public bool Connected
        {
            get => _connected;
            private set
            {
                if (_connected == value)
                {
                    return;
                }

                _connected = value;
                RaisePropertyChanged();
            }
        }

        public async Task<bool> Connect(CancellationToken token)
        {
            try
            {
                Logger.Info("Connecting to All Sky Camera Safety Monitor");

                _connectedNodeMode = ClusterNodeModeParser.Parse(Properties.Settings.Default.ClusterNodeMode);
                ResetConnectionVerdict();
                Logger.Info($"AI Weather node mode for this connection: {_connectedNodeMode}");

                if (_connectedNodeMode == ClusterNodeMode.Replica)
                {
                    return await ConnectReplicaAsync(token);
                }

                if (_connectedNodeMode == ClusterNodeMode.Primary)
                {
                    ValidatePrimaryClusterConfiguration();
                }

                // Get capture mode from settings
                var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                _captureService.CurrentMode = captureMode;
                Logger.Info($"Safety Monitor - Capture Mode: {captureMode}");

                bool success = false;

                if (captureMode == CaptureMode.RTSPStream)
                {
                    // RTSP mode
                    var rtspUrl = Properties.Settings.Default.RtspUrl;
                    var username = Properties.Settings.Default.RtspUsername;
                    var password = Properties.Settings.Default.RtspPassword;

                    Logger.Info($"Safety Monitor - RTSP URL: '{LogRedactor.RedactRtspUrl(rtspUrl)}'");
                    _captureService.ConfigureRTSP(rtspUrl ?? "", username, password);
                    success = !string.IsNullOrWhiteSpace(rtspUrl);
                }
                else if (captureMode == CaptureMode.INDICamera)
                {
                    // HTTP Image Download mode
                    var imageUrl = Properties.Settings.Default.INDIDeviceName;
                    var username = Properties.Settings.Default.RtspUsername;
                    var password = Properties.Settings.Default.RtspPassword;
                    
                    Logger.Info($"Safety Monitor - HTTP Image URL: '{imageUrl}'");
                    _captureService.ConfigureINDI(imageUrl ?? "", username, password);
                    success = !string.IsNullOrWhiteSpace(imageUrl);
                }
                else if (captureMode == CaptureMode.FolderWatch)
                {
                    // Folder Watch mode
                    var folderPath = Properties.Settings.Default.FolderPath;
                    Logger.Info($"Safety Monitor - Folder Path: '{folderPath}'");
                    _captureService.ConfigureFolderWatch(folderPath ?? "");
                    success = !string.IsNullOrWhiteSpace(folderPath) && Directory.Exists(folderPath);
                }

                if (!success)
                {
                    Logger.Error($"Safety Monitor - Failed to configure {captureMode} mode");
                    return false;
                }

                // Initialize analysis service
                UpdateAnalysisService();
                await EnsureAnalysisServiceInitializedAsync(token);

                // Best-effort: the lazy path in IsExternalMonitorSafe retries on its own
                // schedule, but connecting here surfaces a wrong ProgID in the log at once.
                if (Properties.Settings.Default.UseAscomSafetyMonitor)
                {
                    var progId = Properties.Settings.Default.AscomSafetyMonitorProgId;
                    lock (_externalGate)
                    {
                        _externalConnectAttemptUtc = DateTime.UtcNow;
                        _externalSafeCached = _externalMonitor.TryConnect(progId ?? string.Empty)
                                              && _externalMonitor.TryGetIsSafe(out var s) && s;
                        _externalReadUtc = DateTime.UtcNow;
                    }
                    Logger.Info($"External ASCOM safety monitor enabled ('{progId}'): " +
                                $"{(_externalMonitor.Connected ? "connected" : "NOT connected - the monitor will report UNSAFE until it is")}");
                }

                // Mark as connected BEFORE starting periodic monitoring
                // so that UI handlers can see Connected=true when the first check completes
                Connected = true;
                Logger.Info($"All Sky Camera Safety Monitor connected using {captureMode} mode");

                if (_connectedNodeMode == ClusterNodeMode.Primary)
                {
                    StartPrimaryClusterServer();
                }

                // Start periodic monitoring (first check runs immediately)
                StartPeriodicMonitoring();

                MonitoringStarted?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (Exception ex)
            {
                StopClusterTransport();
                Connected = false;
                Logger.Error($"Error connecting to safety monitor: {ex.Message}", ex);
                return false;
            }
        }

        private void ResetConnectionVerdict()
        {
            _lastAnalysisUtc = DateTime.MinValue;
            _isCurrentlySafe = false;
            _staleLogged = false;
            _isSolarAltitudeSuspended = false;
            _solarContextUnavailable = false;
            _currentSunAltitude = null;
            _activeSunAltitudeLimit = SolarAltitudeGuard.NormalizeLimit(
                Properties.Settings.Default.SunAltitudeLimitDegrees);
            _replicaSnapshot = null;
            _replicaLastReceivedUtc = DateTime.MinValue;
            _replicaFailure = AIWeatherReplicaFailure.Waiting;
            _replicaLastError = string.Empty;
            _lastResult = null;
            _lastAnalysisBundle = null;
            _lastImage?.Dispose();
            _lastImage = null;
        }

        private async Task<bool> ConnectReplicaAsync(CancellationToken token)
        {
            var primaryUrl = Properties.Settings.Default.ClusterPrimaryUrl?.Trim() ?? string.Empty;
            var sharedToken = Properties.Settings.Default.ClusterSharedToken ?? string.Empty;
            if (!AIWeatherClusterProtocol.IsTokenUsable(sharedToken))
            {
                throw new InvalidOperationException(
                    $"AI Weather replica mode requires a shared token with at least {AIWeatherClusterProtocol.MinimumTokenLength} characters.");
            }

            _captureService.Reset();
            _clusterClient = new AIWeatherClusterClient(primaryUrl, sharedToken, TimeSpan.FromSeconds(5));

            // A replica may add a local environmental monitor, but it can only tighten the
            // primary verdict. It never supplies a local sky verdict or substitutes for the
            // primary when the network is unavailable.
            if (Properties.Settings.Default.UseAscomSafetyMonitor)
            {
                var progId = Properties.Settings.Default.AscomSafetyMonitorProgId;
                lock (_externalGate)
                {
                    _externalConnectAttemptUtc = DateTime.UtcNow;
                    _externalSafeCached = _externalMonitor.TryConnect(progId ?? string.Empty)
                                          && _externalMonitor.TryGetIsSafe(out var externalSafe)
                                          && externalSafe;
                    _externalReadUtc = DateTime.UtcNow;
                }
            }

            Connected = true;
            Logger.Info($"AI Weather replica connected in fail-closed waiting state; primary {primaryUrl}");

            // Do not report a successful N.I.N.A. connection without at least attempting the
            // first synchronization. A temporarily unreachable primary still leaves the
            // equipment connected but Unsafe so it can recover without restarting N.I.N.A.
            await PollPrimaryAsync(token);
            StartPeriodicMonitoring();
            MonitoringStarted?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private static void ValidatePrimaryClusterConfiguration()
        {
            var port = Properties.Settings.Default.ClusterListenPort;
            if (port is < 1 or > 65535)
            {
                throw new InvalidOperationException("AI Weather primary listen port must be between 1 and 65535.");
            }
            if (!AIWeatherClusterProtocol.IsTokenUsable(Properties.Settings.Default.ClusterSharedToken))
            {
                throw new InvalidOperationException(
                    $"AI Weather primary mode requires a shared token with at least {AIWeatherClusterProtocol.MinimumTokenLength} characters.");
            }
        }

        private void StartPrimaryClusterServer()
        {
            _clusterServer = new AIWeatherClusterServer(
                Properties.Settings.Default.ClusterListenPort,
                Properties.Settings.Default.ClusterSharedToken,
                Environment.MachineName,
                BuildPrimaryClusterSnapshot);
            _clusterServer.Start();
        }

        private AIWeatherClusterSnapshot BuildPrimaryClusterSnapshot()
        {
            var result = _lastResult;
            var analysisUtc = _lastAnalysisUtc == DateTime.MinValue ? (DateTime?)null : _lastAnalysisUtc;
            return new AIWeatherClusterSnapshot
            {
                Sequence = Interlocked.Increment(ref _clusterSequence),
                Connected = Connected,
                Monitoring = _isMonitoring,
                IsSafe = IsSafe,
                SafetyReason = PrimarySafetyReasonCode(),
                WeatherCondition = result?.Condition.ToString() ?? WeatherCondition.Unknown.ToString(),
                CloudCoverage = result?.CloudCoverage ?? 0,
                Confidence = result?.Confidence ?? 0,
                RainDetected = result?.RainDetected ?? false,
                FogDetected = result?.FogDetected ?? false,
                Provider = result?.Provenance.Provider ?? "Unknown",
                Model = result?.Provenance.Model ?? "Unknown",
                AnalysisUtc = analysisUtc,
                AnalysisAgeSeconds = analysisUtc.HasValue
                    ? Math.Max(0, (DateTime.UtcNow - analysisUtc.Value).TotalSeconds)
                    : null,
                SourceFresh = !_isSolarAltitudeSuspended && IsAnalysisFresh()
            };
        }

        private string PrimarySafetyReasonCode()
        {
            if (!Connected) return "not-connected";
            if (_isSolarAltitudeSuspended) return _solarContextUnavailable ? "solar-context-unavailable" : "solar-altitude-suspended";
            if (_lastAnalysisUtc == DateTime.MinValue) return "waiting-first-analysis";
            if (!IsAnalysisFresh()) return "analysis-stale";
            if (Properties.Settings.Default.UseAscomSafetyMonitor && !IsExternalMonitorSafe()) return "external-monitor-unsafe";
            if (_lastResult?.RainDetected == true) return "rain";
            if (_lastResult?.FogDetected == true) return "fog";
            if (!_isCurrentlySafe) return "cloud-threshold";
            return "safe";
        }

        private async Task PollPrimaryAsync(CancellationToken cancellationToken)
        {
            if (!await _replicaPollGate.WaitAsync(0, cancellationToken))
            {
                Logger.Debug("AI Weather replica poll skipped because the previous poll is still running");
                return;
            }

            var client = _clusterClient;
            try
            {
                if (client == null)
                {
                    SetReplicaFailure(AIWeatherReplicaFailure.Network, "Replica client is not initialized.");
                    return;
                }

                var snapshot = await client.PollAsync(cancellationToken);
                _replicaSnapshot = snapshot;
                _replicaLastReceivedUtc = DateTime.UtcNow;
                _replicaFailure = AIWeatherReplicaFailure.None;
                _replicaLastError = string.Empty;
                _lastAnalysisUtc = snapshot.AnalysisUtc ?? DateTime.MinValue;
                _isCurrentlySafe = snapshot.IsSafe;

                if (!Enum.TryParse(snapshot.WeatherCondition, ignoreCase: true, out WeatherCondition condition))
                {
                    condition = WeatherCondition.Unknown;
                }
                _lastResult = new WeatherAnalysisResult
                {
                    Timestamp = snapshot.AnalysisUtc ?? snapshot.GeneratedUtc,
                    Condition = condition,
                    CloudCoverage = snapshot.CloudCoverage,
                    Confidence = snapshot.Confidence,
                    IsSafeForImaging = snapshot.IsSafe,
                    RainDetected = snapshot.RainDetected,
                    FogDetected = snapshot.FogDetected,
                    Description = $"Remote primary: {snapshot.SafetyReason}",
                    Provenance = new AnalysisProvenance
                    {
                        Provider = snapshot.Provider,
                        Model = snapshot.Model,
                        Origin = AnalysisOrigin.Unknown,
                        OnlineSucceeded = false,
                        IsFallback = false
                    }
                };

                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(IsSkyConditionSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
                RaisePropertyChanged(nameof(ReplicaConnectionSummary));
                WriteSafetyStatusFile();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AIWeatherClusterException ex)
            {
                SetReplicaFailure(ex.Failure, ex.Message);
            }
            catch (Exception ex)
            {
                SetReplicaFailure(AIWeatherReplicaFailure.Network, ex.Message);
            }
            finally
            {
                _replicaPollGate.Release();
            }
        }

        private void SetReplicaFailure(AIWeatherReplicaFailure failure, string message)
        {
            var changed = _replicaFailure != failure || !string.Equals(_replicaLastError, message, StringComparison.Ordinal);
            _replicaFailure = failure;
            _replicaLastError = message;
            if (changed)
            {
                Logger.Warning($"AI Weather replica synchronization problem ({failure}): {message}");
            }
            RaisePropertyChanged(nameof(IsSafe));
            RaisePropertyChanged(nameof(SafetyStateReason));
            RaisePropertyChanged(nameof(ReplicaConnectionSummary));
            WriteSafetyStatusFile();
        }

        private bool IsReplicaTransportFresh()
        {
            if (_replicaSnapshot == null || _replicaLastReceivedUtc == DateTime.MinValue)
            {
                return false;
            }
            var staleSeconds = Math.Clamp(Properties.Settings.Default.ClusterStaleSeconds, 3, 3600);
            return DateTime.UtcNow - _replicaLastReceivedUtc <= TimeSpan.FromSeconds(staleSeconds);
        }

        private void StopClusterTransport()
        {
            _clusterClient?.Dispose();
            _clusterClient = null;
            _clusterServer?.Dispose();
            _clusterServer = null;
        }

        public void Disconnect()
        {
            try
            {
                Logger.Info("Disconnecting All Sky Camera Safety Monitor");

                StopPeriodicMonitoring();
                StopClusterTransport();
                // Disconnect is reversible in NINA. Keep the singleton capture service alive
                // so a later Connect can build a fresh RTSP pipeline in the same process.
                _captureService.Reset();

                // Values are no longer being refreshed: blank the sequencer symbols so an
                // expression cannot keep acting on a stale reading.
                SequencerSymbolPublisher.ClearValues();

                // Drop the verdict with the connection, so a reconnect cannot start from a
                // SAFE inherited from before the disconnect.
                _lastAnalysisUtc = DateTime.MinValue;
                _isCurrentlySafe = false;
                _replicaSnapshot = null;
                _replicaLastReceivedUtc = DateTime.MinValue;
                _replicaFailure = AIWeatherReplicaFailure.Waiting;
                _replicaLastError = string.Empty;
                _isSolarAltitudeSuspended = false;
                _solarContextUnavailable = false;
                _currentSunAltitude = null;

                lock (_externalGate)
                {
                    _externalMonitor.Disconnect();
                    _externalSafeCached = false;
                    _externalReadUtc = DateTime.MinValue;
                }

                Connected = false;
                Logger.Info("All Sky Camera Safety Monitor disconnected");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error disconnecting: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// The state NINA acts on. Three independent conditions, all of which must hold:
        /// the sky verdict from the latest analysis, that verdict still being recent enough
        /// to describe the current sky, and the external ASCOM safety monitor (when one is
        /// configured). Anything unknown counts as unsafe — a missing answer is not a
        /// permission to keep imaging.
        /// </summary>
        public bool IsSafe
        {
            get
            {
                if (_connectedNodeMode == ClusterNodeMode.Replica)
                {
                    var snapshot = _replicaSnapshot;
                    var fatalProtocolFailure = _replicaFailure is AIWeatherReplicaFailure.Authentication
                        or AIWeatherReplicaFailure.Protocol;
                    return !fatalProtocolFailure
                           && IsReplicaTransportFresh()
                           && snapshot?.Connected == true
                           && snapshot.Monitoring
                           && snapshot.SourceFresh
                           && snapshot.IsSafe
                           && IsExternalMonitorSafe();
                }

                return !_isSolarAltitudeSuspended
                       && _isCurrentlySafe
                       && IsAnalysisFresh()
                       && IsExternalMonitorSafe();
            }
        }

        /// <summary>The sky verdict alone, without freshness or the external monitor.</summary>
        public bool IsSkyConditionSafe => _isCurrentlySafe;

        public ClusterNodeMode CurrentNodeMode => _connectedNodeMode;

        public string ReplicaConnectionSummary
        {
            get
            {
                if (_connectedNodeMode != ClusterNodeMode.Replica)
                {
                    return _connectedNodeMode.ToString();
                }
                if (_replicaSnapshot == null)
                {
                    return _replicaFailure == AIWeatherReplicaFailure.Waiting
                        ? UiLocalization.Text("Cluster.Waiting")
                        : UiLocalization.Text("Cluster.Error", _replicaLastError);
                }
                var age = Math.Max(0, (DateTime.UtcNow - _replicaLastReceivedUtc).TotalSeconds);
                return UiLocalization.Text(
                    "Cluster.Synchronized",
                    _replicaSnapshot.NodeId,
                    age,
                    _replicaSnapshot.SessionId.Length >= 8 ? _replicaSnapshot.SessionId[..8] : _replicaSnapshot.SessionId);
            }
        }

        public DatasetStatusSnapshot DatasetStatus => _datasetRecorder.Status;

        public string DatasetStatusText => DatasetStatus.ToDisplayString();

        public bool IsSolarAltitudeSuspended => _isSolarAltitudeSuspended;

        public double? CurrentSunAltitude => _currentSunAltitude;

        public double SunAltitudeLimitDegrees => _activeSunAltitudeLimit;

        /// <summary>
        /// Why the monitor is reporting what it reports, in one line for the panel. Until
        /// now this only existed in the log, which meant a user seeing UNSAFE on a clear
        /// night had no way to tell a cloudy verdict from a dead camera or an unreachable
        /// external device. Conditions are reported in the order they are evaluated, so the
        /// first thing that is actually wrong is the thing shown.
        /// </summary>
        public string SafetyStateReason
        {
            get
            {
                if (!Connected)
                {
                    return UiLocalization.Text("Runtime.NotConnected");
                }

                if (_connectedNodeMode == ClusterNodeMode.Replica)
                {
                    if (_replicaFailure == AIWeatherReplicaFailure.Authentication)
                    {
                        return UiLocalization.Text("Cluster.AuthenticationFailed");
                    }
                    if (_replicaFailure == AIWeatherReplicaFailure.Protocol)
                    {
                        return UiLocalization.Text("Cluster.ProtocolFailed", _replicaLastError);
                    }
                    if (_replicaSnapshot == null)
                    {
                        return UiLocalization.Text("Cluster.Waiting");
                    }
                    if (!IsReplicaTransportFresh())
                    {
                        var age = Math.Max(0, (DateTime.UtcNow - _replicaLastReceivedUtc).TotalSeconds);
                        return UiLocalization.Text(
                            "Cluster.TransportStale",
                            age,
                            Math.Clamp(Properties.Settings.Default.ClusterStaleSeconds, 3, 3600));
                    }
                    if (!_replicaSnapshot.Connected || !_replicaSnapshot.Monitoring)
                    {
                        return UiLocalization.Text("Cluster.PrimaryNotMonitoring");
                    }
                    if (!_replicaSnapshot.SourceFresh || !_replicaSnapshot.IsSafe)
                    {
                        return UiLocalization.Text("Cluster.PrimaryUnsafe", _replicaSnapshot.SafetyReason);
                    }
                    if (Properties.Settings.Default.UseAscomSafetyMonitor && !IsExternalMonitorSafe())
                    {
                        return _externalMonitor.Connected
                            ? UiLocalization.Text("Runtime.ExternalUnsafe")
                            : UiLocalization.Text("Runtime.ExternalUnreadable");
                    }
                    return UiLocalization.Text("Cluster.PrimarySafe");
                }

                if (_isSolarAltitudeSuspended)
                {
                    return _solarContextUnavailable || !_currentSunAltitude.HasValue
                        ? UiLocalization.Text("Runtime.SolarUnavailable")
                        : UiLocalization.Text(
                            "Runtime.SolarSuspended",
                            _currentSunAltitude.Value,
                            _activeSunAltitudeLimit);
                }

                if (_lastAnalysisUtc == DateTime.MinValue)
                {
                    return UiLocalization.Text("Runtime.WaitingFirst");
                }

                if (!IsAnalysisFresh())
                {
                    var age = (DateTime.UtcNow - _lastAnalysisUtc).TotalMinutes;
                    return UiLocalization.Text("Runtime.Stale", age, MaxDataAge().TotalMinutes);
                }

                if (Properties.Settings.Default.UseAscomSafetyMonitor && !IsExternalMonitorSafe())
                {
                    return _externalMonitor.Connected
                        ? UiLocalization.Text("Runtime.ExternalUnsafe")
                        : UiLocalization.Text("Runtime.ExternalUnreadable");
                }

                if (!_isCurrentlySafe)
                {
                    var r = _lastResult;
                    if (r == null)
                    {
                        return UiLocalization.Text("Runtime.NoUsable");
                    }
                    if (r.RainDetected)
                    {
                        return UiLocalization.Text("Runtime.Rain");
                    }
                    if (r.FogDetected)
                    {
                        return UiLocalization.Text("Runtime.Fog");
                    }
                    return UiLocalization.Text(
                        "Runtime.CloudUnsafe",
                        r.CloudCoverage,
                        Properties.Settings.Default.CloudCoverageSafeThreshold);
                }

                return UiLocalization.Text("Runtime.SkyClear");
            }
        }

        /// <summary>
        /// Maximum age of the latest successful analysis. Configurable; 0 means automatic
        /// (three check intervals, never below ten minutes) so a long polling interval
        /// cannot make the state permanently stale by construction.
        /// </summary>
        private static TimeSpan MaxDataAge()
        {
            var configured = Properties.Settings.Default.MaxDataAgeMinutes;
            if (configured > 0)
            {
                return TimeSpan.FromMinutes(configured);
            }

            var interval = Math.Max(1, Properties.Settings.Default.CheckIntervalMinutes);
            var automatic = TimeSpan.FromMinutes(interval * 3);
            return automatic < MinimumAutomaticDataAge ? MinimumAutomaticDataAge : automatic;
        }

        /// <summary>
        /// Whether the latest analysis is recent enough to be acted upon. Logged once per
        /// transition rather than per call: NINA polls IsSafe continuously.
        /// </summary>
        private bool IsAnalysisFresh()
        {
            if (_lastAnalysisUtc == DateTime.MinValue)
            {
                return false; // nothing analysed yet since connecting
            }

            var age = DateTime.UtcNow - _lastAnalysisUtc;
            if (age <= MaxDataAge())
            {
                return true;
            }

            if (!_staleLogged)
            {
                _staleLogged = true;
                Logger.Warning($"Safety monitor reporting UNSAFE: no successful sky analysis for {age.TotalMinutes:F1} minutes " +
                               $"(limit {MaxDataAge().TotalMinutes:F0} min). Check the all-sky camera source.");
            }
            return false;
        }

        /// <summary>
        /// The external ASCOM safety monitor's verdict, or true when the feature is off.
        /// Every failure mode - not configured, driver missing, connection lost, read error -
        /// reports unsafe, because an external monitor that cannot be read is exactly the
        /// situation its user installed it to be protected from. Reads are cached briefly
        /// and reconnects are rate-limited so a polled property cannot hammer a COM driver.
        /// </summary>
        private bool IsExternalMonitorSafe()
        {
            if (!Properties.Settings.Default.UseAscomSafetyMonitor)
            {
                return true;
            }

            lock (_externalGate)
            {
                var now = DateTime.UtcNow;
                if (now - _externalReadUtc < ExternalReadCacheDuration)
                {
                    return _externalSafeCached;
                }
                _externalReadUtc = now;

                var progId = Properties.Settings.Default.AscomSafetyMonitorProgId;
                if (string.IsNullOrWhiteSpace(progId))
                {
                    _externalSafeCached = false;
                    LogExternalFailureOnce("the external ASCOM safety monitor is enabled but no driver is selected");
                    return false;
                }

                if (!_externalMonitor.Connected || !string.Equals(_externalMonitor.ProgId, progId, StringComparison.OrdinalIgnoreCase))
                {
                    if (now - _externalConnectAttemptUtc < ExternalReconnectInterval)
                    {
                        _externalSafeCached = false;
                        return false;
                    }

                    _externalConnectAttemptUtc = now;
                    if (!_externalMonitor.TryConnect(progId))
                    {
                        _externalSafeCached = false;
                        LogExternalFailureOnce($"cannot connect to the external ASCOM safety monitor '{progId}'");
                        return false;
                    }
                }

                if (!_externalMonitor.TryGetIsSafe(out var externalSafe))
                {
                    // The driver answered before and does not now: drop the connection so the
                    // next cycle rebuilds it instead of polling a dead object forever.
                    _externalMonitor.Disconnect();
                    _externalSafeCached = false;
                    LogExternalFailureOnce($"cannot read IsSafe from the external ASCOM safety monitor '{progId}'");
                    return false;
                }

                if (_externalFailureLogged)
                {
                    _externalFailureLogged = false;
                    Logger.Info($"External ASCOM safety monitor '{progId}' is readable again");
                }

                _externalSafeCached = externalSafe;
                return externalSafe;
            }
        }

        private void LogExternalFailureOnce(string message)
        {
            if (_externalFailureLogged)
            {
                return;
            }
            _externalFailureLogged = true;
            Logger.Warning($"Safety monitor reporting UNSAFE: {message}");
        }

        private void UpdateSafetyState(WeatherAnalysisResult result)
        {
            if (result == null)
            {
                _isCurrentlySafe = false;
                return;
            }

            var unsafeThreshold = Properties.Settings.Default.CloudCoverageThreshold;
            var safeThreshold = Properties.Settings.Default.CloudCoverageSafeThreshold;

            // Provider-supplied IsSafeForImaging is useful for display and disagreement
            // sampling, but it is not a visual ground truth: every provider bakes different
            // hard-coded thresholds into that field. The N.I.N.A. safety decision is owned
            // here and uses the user's High/Low thresholds plus rain/fog and validity.
            bool baseConditionsSafe = result.Condition != WeatherCondition.Unknown
                                      && result.Confidence > 0
                                      && !result.RainDetected
                                      && !result.FogDetected;

            if (!baseConditionsSafe)
            {
                _isCurrentlySafe = false;
            }
            else
            {
                // Hysteresis logic
                if (_isCurrentlySafe)
                {
                    // Stay safe until coverage exceeds the high/unsafe threshold
                    if (result.CloudCoverage >= unsafeThreshold)
                    {
                        _isCurrentlySafe = false;
                    }
                }
                else
                {
                    // Stay unsafe until coverage drops below the low/safe threshold
                    if (result.CloudCoverage < safeThreshold)
                    {
                        _isCurrentlySafe = true;
                    }
                }
            }

            Logger.Debug($"Safety check: {(_isCurrentlySafe ? "SAFE" : "UNSAFE")} - " +
                       $"Cloud coverage: {result.CloudCoverage:F1}%, " +
                       $"Safe Threshold: {safeThreshold}%, Unsafe Threshold: {unsafeThreshold}%, " +
                       $"Rain: {result.RainDetected}, Fog: {result.FogDetected}, " +
                       $"Condition: {result.Condition}");
        }

        // IDevice methods required by interface
        public string Action(string actionName, string actionParameters)
        {
            return string.Empty;
        }

        public string SendCommandString(string command, bool raw = true)
        {
            return string.Empty;
        }

        public bool SendCommandBool(string command, bool raw = true)
        {
            return false;
        }

        public void SendCommandBlind(string command, bool raw = true)
        {
            // No-op
        }

        public string DisplayName
        {
            get => Name;
            set { }
        }

        public IList<string> SupportedActions => new List<string>();

        #endregion

        private void StartPeriodicMonitoring()
        {
            if (_isMonitoring) return;

            _cts = new CancellationTokenSource();
            _isMonitoring = true;

            var replicaMode = _connectedNodeMode == ClusterNodeMode.Replica;
            var intervalValue = replicaMode
                ? Math.Clamp(Properties.Settings.Default.ClusterPollSeconds, 1, 300)
                : Math.Max(1, Properties.Settings.Default.CheckIntervalMinutes);
            var interval = replicaMode
                ? TimeSpan.FromSeconds(intervalValue)
                : TimeSpan.FromMinutes(intervalValue);
            var intervalDescription = replicaMode
                ? $"{intervalValue} seconds"
                : $"{intervalValue} minutes";

            var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
            Logger.Debug($"Starting periodic monitoring every {intervalDescription} (node: {_connectedNodeMode}, capture: {captureMode})");

            _monitoringTimer = new Timer(_ =>
            {
                var currentMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Debug($"Timer fired - Interval: {intervalDescription}, node: {_connectedNodeMode}, capture: {currentMode}");
                
                if (_cts?.Token.IsCancellationRequested ?? true)
                {
                    Logger.Warning("Timer fired but cancellation was requested - skipping");
                    return;
                }

                try
                {
                    Logger.Debug($"Launching weather check task from timer (Mode: {currentMode})");
                    Task.Run(async () =>
                    {
                        try
                        {
                            Logger.Debug($"Executing periodic weather check (Mode: {currentMode})");
                            if (_connectedNodeMode == ClusterNodeMode.Replica)
                            {
                                await PollPrimaryAsync(_cts.Token);
                            }
                            else
                            {
                                await PerformWeatherCheckAsync(_cts.Token);
                            }
                            Logger.Debug($"Monitoring cycle complete - next check in {intervalDescription}");
                        }
                        catch (Exception ex)
                        {
                            Logger.Error($"Error in periodic weather check: {ex.Message}", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to start weather check task: {ex.Message}", ex);
                }
            }, null, TimeSpan.Zero, interval);
            
            Logger.Debug($"Timer created and started - first check will run immediately");
        }

        private void StopPeriodicMonitoring()
        {
            _isMonitoring = false;
            _cts?.Cancel();
            _monitoringTimer?.Dispose();
            _monitoringTimer = null;
            Logger.Info("Stopped periodic monitoring");
        }

        private void RequestImmediateWeatherCheck()
        {
            var tokenSource = _cts;
            if (!Connected || tokenSource == null || tokenSource.IsCancellationRequested)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_connectedNodeMode == ClusterNodeMode.Replica)
                    {
                        await PollPrimaryAsync(tokenSource.Token);
                    }
                    else
                    {
                        await PerformWeatherCheckAsync(tokenSource.Token);
                    }
                }
                catch (OperationCanceledException) when (tokenSource.IsCancellationRequested)
                {
                    // Normal disconnect/shutdown race.
                }
                catch (Exception ex)
                {
                    Logger.Error($"Immediate weather check after Sun-altitude setting change failed: {ex.Message}", ex);
                }
            });
        }

        private AstroContext? TryComputeAstroContext(DateTime utcNow)
        {
            try
            {
                if (_profileService == null)
                {
                    Logger.Warning("Cannot compute astronomical context: N.I.N.A. profile service is unavailable");
                    return null;
                }

                var astro = _profileService.ActiveProfile.AstrometrySettings;
                var context = AstroContext.Compute(
                    astro.Latitude,
                    astro.Longitude,
                    astro.Elevation,
                    utcNow);
                Logger.Info(
                    $"Astro context: Sun {context.SunAltitude:F1}° ({context.SunState}), " +
                    $"Moon {context.MoonIllumination:F0}% {context.MoonPhase} at {context.MoonAltitude:F1}°");
                return context;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to compute astronomical context: {ex.Message}");
                return null;
            }
        }

        private void EnterSolarAltitudeSuspension(
            SolarAltitudeGateDecision decision,
            AstroContext? astroContext)
        {
            var wasSuspended = _isSolarAltitudeSuspended;
            _isSolarAltitudeSuspended = true;
            _solarContextUnavailable = !decision.HasAstronomicalContext;
            _currentSunAltitude = decision.SunAltitude;
            _activeSunAltitudeLimit = decision.LimitDegrees;
            _lastAstroContext = astroContext;
            _isCurrentlySafe = false;

            if (!wasSuspended)
            {
                // A new observing window must earn a fresh verdict. Never let tonight's
                // first check inherit yesterday's safe state or keep a daytime frame ready
                // for manual dataset collection.
                _lastAnalysisUtc = DateTime.MinValue;
                _lastResult = null;
                _lastAnalysisBundle = null;
                _lastImage?.Dispose();
                _lastImage = null;
                _staleLogged = false;

                if (decision.HasAstronomicalContext && decision.SunAltitude.HasValue)
                {
                    Logger.Info(
                        $"Sun-altitude guard suspended weather analysis before capture: " +
                        $"Sun {decision.SunAltitude.Value:F1}° >= limit {decision.LimitDegrees:F1}°. " +
                        "No frame, model/API call, or dataset sample will be produced.");
                }
                else
                {
                    Logger.Warning(
                        "Sun-altitude guard suspended weather analysis before capture because " +
                        "astronomical context is unavailable. Safety remains UNSAFE.");
                }
            }
            else
            {
                Logger.Debug("Sun-altitude guard remains active; skipping capture and analysis");
            }

            SequencerSymbolPublisher.PublishSuspended();
            RaisePropertyChanged(nameof(IsSolarAltitudeSuspended));
            RaisePropertyChanged(nameof(CurrentSunAltitude));
            RaisePropertyChanged(nameof(SunAltitudeLimitDegrees));
            RaisePropertyChanged(nameof(IsSafe));
            RaisePropertyChanged(nameof(SafetyStateReason));
            WriteSafetyStatusFile();
        }

        private void LeaveSolarAltitudeSuspension(
            SolarAltitudeGateDecision decision,
            AstroContext? astroContext)
        {
            var wasSuspended = _isSolarAltitudeSuspended;
            _isSolarAltitudeSuspended = false;
            _solarContextUnavailable = false;
            _currentSunAltitude = decision.SunAltitude;
            _activeSunAltitudeLimit = decision.LimitDegrees;
            _lastAstroContext = astroContext;

            if (wasSuspended)
            {
                Logger.Info(
                    $"Sun-altitude guard released: Sun {decision.SunAltitude:F1}° is below " +
                    $"limit {decision.LimitDegrees:F1}°. Weather analysis is resuming.");
                RaisePropertyChanged(nameof(IsSolarAltitudeSuspended));
                RaisePropertyChanged(nameof(CurrentSunAltitude));
                RaisePropertyChanged(nameof(SunAltitudeLimitDegrees));
                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
            }
        }

        private async Task PerformWeatherCheckAsync(CancellationToken cancellationToken)
        {
            await _checkGate.WaitAsync(cancellationToken);
            Bitmap? frame = null;
            try
            {
                var captureMode = (CaptureMode)Properties.Settings.Default.CaptureMode;
                Logger.Debug($"PerformWeatherCheckAsync - Mode: {captureMode}");

                // The Sun gate deliberately runs before opening/capturing the camera and
                // before invoking any model. This is what prevents daylight overexposure
                // from entering the dataset and avoids spending online API quota.
                var astroContext = TryComputeAstroContext(DateTime.UtcNow);
                var solarDecision = SolarAltitudeGuard.Evaluate(
                    Properties.Settings.Default.UseSunAltitudeLimit,
                    Properties.Settings.Default.SunAltitudeLimitDegrees,
                    astroContext);
                if (solarDecision.ShouldSuspend)
                {
                    EnterSolarAltitudeSuspension(solarDecision, astroContext);
                    return;
                }

                LeaveSolarAltitudeSuspension(solarDecision, astroContext);

                // Capture image from all modes
                Logger.Debug($"Capturing image from {captureMode} source");
                frame = await _captureService.CaptureImageAsync(cancellationToken);

                if (frame == null)
                {
                    // No new data. The state is deliberately NOT flipped here on a single
                    // miss - a dropped frame on an RTSP stream would make the monitor flap
                    // and abort sequences - but it is not silently kept either: the analysis
                    // ages, and IsSafe turns unsafe once it passes the maximum data age.
                    Logger.Warning($"Failed to capture image from {captureMode} source; " +
                                   $"last successful analysis is {LastAnalysisAgeDescription()} old");
                    RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
                    return;
                }

                Logger.Debug($"Image captured from {captureMode}, size: {frame.Width}x{frame.Height}");
                var capturedUtc = DateTime.UtcNow;

                // Analyze the frame
                var analysisService = await EnsureAnalysisServiceInitializedAsync(cancellationToken);
                Logger.Debug($"Starting AI analysis using {analysisService.GetType().Name}");

                var analysis = await _analysisOrchestrator.AnalyzeAsync(
                    analysisService,
                    frame,
                    astroContext,
                    cancellationToken);
                var result = analysis.EffectiveResult;
                Logger.Debug(
                    $"AI analysis completed: effective={result.Provenance.Origin}, " +
                    $"fallback={analysis.UsedFallback}, " +
                    $"teacher={analysis.Teacher?.Provenance.Provider ?? "none"}, " +
                    $"student={analysis.Student.Provenance.Model}");
                _lastResult = result;
                _lastAnalysisBundle = analysis;
                _lastAstroContext = astroContext;

                // Unknown/zero-confidence is a failure result, not fresh sky data. Preserve
                // the previous timestamp so the fail-safe age limit can expire it.
                if (result.Condition != WeatherCondition.Unknown && result.Confidence > 0)
                {
                    _lastAnalysisUtc = DateTime.UtcNow;
                    _staleLogged = false;
                }

                // Store a copy of the image for UI restoration
                _lastImage?.Dispose();
                _lastImage = new Bitmap(frame);

                // Update Safety State (Hysteresis)
                UpdateSafetyState(result);

                var externalSafe = Properties.Settings.Default.UseAscomSafetyMonitor
                    ? IsExternalMonitorSafe()
                    : (bool?)null;

                // Fast selection + image clone only. Resize, hashing, JPEG encoding and I/O
                // happen on a bounded background writer. A false return is deliberately
                // ignored: dataset collection must never affect the safety verdict.
                _datasetRecorder.TryEnqueue(
                    frame,
                    capturedUtc,
                    astroContext,
                    analysis,
                    effectiveSafe: IsSafe,
                    visualSafe: _isCurrentlySafe,
                    externalSafetyMonitorSafe: externalSafe,
                    highThreshold: Properties.Settings.Default.CloudCoverageThreshold,
                    lowThreshold: Properties.Settings.Default.CloudCoverageSafeThreshold);

                // Expose the reading to the Advanced Sequencer's Symbols sidebar (N.I.N.A. 3.3+).
                // The published Safe symbol is the composite state, so an expression in the
                // sequencer sees the same verdict NINA's safety monitor sees.
                SequencerSymbolPublisher.Publish(result, IsSafe);

                // Log the results
                Logger.Info($"Weather Analysis - Condition: {result.Condition}, " +
                          $"Cloud Coverage: {result.CloudCoverage:F1}%, " +
                          $"Safe: {result.IsSafeForImaging}, " +
                          $"Confidence: {result.Confidence:F1}%, " +
                          $"Source: {result.Provenance.Provider}/{result.Provenance.Model}, " +
                          $"Fallback: {result.Provenance.IsFallback}");

                if (analysis.TeacherStudentCloudDifference is double difference)
                {
                    Logger.Info(
                        $"Teacher/student shadow comparison - teacher: " +
                        $"{analysis.Teacher!.Result!.CloudCoverage:F1}%, student: " +
                        $"{analysis.Student.CloudCoverage:F1}%, difference: {difference:F1}%");
                }

                if (Properties.Settings.Default.UseAscomSafetyMonitor)
                {
                    Logger.Info($"Safety state - sky: {(_isCurrentlySafe ? "SAFE" : "UNSAFE")}, " +
                                $"external ASCOM monitor: {(IsExternalMonitorSafe() ? "SAFE" : "UNSAFE")}, " +
                                $"combined: {(IsSafe ? "SAFE" : "UNSAFE")}");
                }

                // Append state changes to the shared LLM wiki daily digest (raw/)
                LlmWikiRawWriter.RecordAnalysis(result);

                // Raise property changed to notify NINA of safety status change
                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));

                // Write safety status to file if enabled
                WriteSafetyStatusFile();

                // Save frame for debugging/logging (optional)
                var captureFolder = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "AllSkyCameraPlugin");
                var imagePath = Path.Combine(
                    captureFolder,
                    $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

                // Save image (HTTP/Folder modes only, RTSP handled above)
                await _captureService.SaveImageAsync(frame, imagePath, cancellationToken);

                PruneCaptureFolder(captureFolder);

            }
            catch (Exception ex)
            {
                // Same contract as a failed capture: no new verdict, so the existing one
                // keeps ageing toward the freshness limit rather than being trusted forever.
                Logger.Error($"Error performing weather check: {ex.Message}", ex);
                RaisePropertyChanged(nameof(IsSafe));
                RaisePropertyChanged(nameof(SafetyStateReason));
            }
            finally
            {
                frame?.Dispose();
                _checkGate.Release();
            }
        }

        private string LastAnalysisAgeDescription()
        {
            return _lastAnalysisUtc == DateTime.MinValue
                ? "no analysis yet"
                : $"{(DateTime.UtcNow - _lastAnalysisUtc).TotalMinutes:F1} min";
        }

        // Debug captures are only needed for recent history; keep the folder bounded
        // so an always-on monitor cannot fill the disk over long sessions.
        private const int MaxSavedCaptures = 25;

        private static void PruneCaptureFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                var files = new DirectoryInfo(folder).GetFiles("capture_*.jpg");
                if (files.Length <= MaxSavedCaptures)
                {
                    return;
                }

                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                for (var i = MaxSavedCaptures; i < files.Length; i++)
                {
                    try
                    {
                        files[i].Delete();
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug($"Failed to delete old capture {files[i].Name}: {ex.Message}");
                    }
                }

                Logger.Debug($"Pruned capture folder to {MaxSavedCaptures} most recent images ({files.Length - MaxSavedCaptures} deleted)");
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to prune capture folder: {ex.Message}");
            }
        }

        public void SetupDialog()
        {
            // This would open a settings dialog
            // For now, settings are managed through plugin options
            Logger.Info("Setup dialog requested - use NINA Plugin Options");
        }

        /// <summary>
        /// Get the latest weather analysis result
        /// </summary>
        public WeatherAnalysisResult? GetLatestResult() => _lastResult;

        /// <summary>
        /// Get the latest captured image
        /// </summary>
        public Bitmap? GetLatestImage() => _lastImage != null ? new Bitmap(_lastImage) : null;

        /// <summary>
        /// Adds the most recent frame to the high-priority review queue. This remains a
        /// no-op when dataset collection is disabled and never re-runs the online teacher.
        /// </summary>
        public async Task<bool> KeepLatestFrameForReviewAsync(
            CancellationToken cancellationToken = default)
        {
            await _checkGate.WaitAsync(cancellationToken);
            try
            {
                if (_lastImage == null || _lastAnalysisBundle == null)
                {
                    return false;
                }

                using var image = new Bitmap(_lastImage);
                var externalSafe = Properties.Settings.Default.UseAscomSafetyMonitor
                    ? IsExternalMonitorSafe()
                    : (bool?)null;
                return _datasetRecorder.TryEnqueue(
                    image,
                    DateTime.UtcNow,
                    _lastAstroContext,
                    _lastAnalysisBundle,
                    effectiveSafe: IsSafe,
                    visualSafe: _isCurrentlySafe,
                    externalSafetyMonitorSafe: externalSafe,
                    highThreshold: Properties.Settings.Default.CloudCoverageThreshold,
                    lowThreshold: Properties.Settings.Default.CloudCoverageSafeThreshold,
                    manualReview: true);
            }
            finally
            {
                _checkGate.Release();
            }
        }

        public Task ShutdownAsync()
        {
            StopPeriodicMonitoring();
            StopClusterTransport();
            return _datasetRecorder.StopAsync(TimeSpan.FromSeconds(5));
        }

        private sealed class NinaDatasetRecorderLogger : IDatasetRecorderLogger
        {
            public void Info(string message) => Logger.Info(message);
            public void Warning(string message) => Logger.Warning(message);
            public void Error(string message, Exception exception) =>
                Logger.Error(message, exception);
        }

        /// <summary>
        /// Force an immediate weather check
        /// </summary>
        public async Task<WeatherAnalysisResult?> ForceCheckAsync(CancellationToken cancellationToken = default)
        {
            if (_connectedNodeMode == ClusterNodeMode.Replica)
            {
                await PollPrimaryAsync(cancellationToken);
            }
            else
            {
                await PerformWeatherCheckAsync(cancellationToken);
            }
            return _lastResult;
        }

        /// <summary>
        /// Write safety status to file if enabled
        /// </summary>
        private void WriteSafetyStatusFile()
        {
            try
            {
                if (!Properties.Settings.Default.WriteSafetyStatusFile)
                {
                    return;
                }

                var filePath = Properties.Settings.Default.SafetyStatusFilePath;
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    Logger.Warning("Safety status file writing is enabled but no file path is configured");
                    return;
                }

                // The exported status is the same composite state NINA acts on - hysteresis,
                // data freshness and the external monitor included. It used to be recomputed
                // from the raw result here, which could disagree with IsSafe and hand third
                // party software a different answer than the one driving the sequence.
                var status = IsSafe ? "Safe" : "Unsafe";

                // Write plain SAFE/UNSAFE — compatible with ASCOM Generic File SafetyMonitor
                File.WriteAllText(filePath, status);
                Logger.Debug($"Safety status written to file: {filePath} - Status: {status}");
            }
            catch (Exception ex)
            {
                Logger.Error($"Error writing safety status file: {ex.Message}", ex);
            }
        }
    }
}
