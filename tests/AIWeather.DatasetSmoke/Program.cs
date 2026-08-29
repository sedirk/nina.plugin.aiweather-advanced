using AIWeather.Models;
using AIWeather.Services;
using AIWeather.Localization;
using AIWeather.Equipment;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.DatasetSmoke;

internal static class Program
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<int> Main(string[] args)
    {
        // NINA's SDK packages deliberately model the application as the runtime
        // host and some omit their own assemblies from a standalone executable's
        // deps.json. The smoke harness still copies those DLLs; resolve them from
        // its output directory when the NINA host is absent.
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(candidate)
                ? System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate)
                : null;
        };

        if (args.Contains("--rtsp-live", StringComparer.OrdinalIgnoreCase))
        {
            return await RunLiveRtspFreshnessCheckAsync();
        }

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
            VerifyLatestRtspFrameBuffer();
            VerifyReplicaPreviewSourcePolicy();
            VerifyReplicaPreviewRetryGate();
            await VerifySharedRtspPreviewFrameProviderAsync();
            await VerifyUnifiedCapturePrefersSharedPreviewAsync();
            VerifyRtspPreviewFit();
            VerifyRtspPreviewHealthWatchdog();
            VerifyGeminiQuotaPolicy();
            VerifyGeminiTransportRefreshesAfterProxyChange();
            await VerifyAIWeatherClusterProtocolAsync();
            await VerifyGeminiRequestPacingAsync();
            await VerifyGeminiServiceSuppressesQuotaRetriesAsync();
            await VerifyGeminiTemporaryFailoverReturnsToPrimaryAsync();
            await VerifyGeminiQuotaPausedAlternateReturnsToPrimaryAsync();
            await VerifyGemini503DiagnosticsSurviveRetryBudgetAsync();
            await VerifyQuotaFallbackMetadataAsync();
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

    private static void VerifyGeminiTransportRefreshesAfterProxyChange()
    {
        var proxyFingerprint = "disabled";
        var createdClients = 0;
        var provider = new SystemProxyAwareHttpClientProvider(
            () => proxyFingerprint,
            () =>
            {
                createdClients++;
                return new HttpClient(new StaticResponseHandler(
                    HttpStatusCode.OK,
                    "{}"));
            });

        var first = provider.GetClient();
        var unchanged = provider.GetClient();
        Assert(ReferenceEquals(first, unchanged),
            "Gemini transport was recreated even though proxy settings were unchanged");
        Assert(createdClients == 1 && provider.Generation == 1,
            "Gemini transport did not retain its first connection pool");

        proxyFingerprint = "enabled:127.0.0.1:10808";
        var refreshed = provider.GetClient();
        Assert(!ReferenceEquals(first, refreshed),
            "Gemini transport did not refresh after the system proxy changed");
        Assert(createdClients == 2 && provider.Generation == 2,
            "Gemini proxy refresh did not create exactly one new transport generation");

        var stableAfterRefresh = provider.GetClient();
        Assert(ReferenceEquals(refreshed, stableAfterRefresh) && createdClients == 2,
            "Gemini transport did not stabilize after the proxy refresh");
    }

    private static async Task VerifyAIWeatherClusterProtocolAsync()
    {
        var generatedToken = AIWeatherClusterProtocol.GenerateSharedToken();
        var secondGeneratedToken = AIWeatherClusterProtocol.GenerateSharedToken();
        Assert(generatedToken.Length == 64 && generatedToken.All(Uri.IsHexDigit),
            "generated cluster token is not a 256-bit hexadecimal value");
        Assert(AIWeatherClusterProtocol.IsTokenUsable(generatedToken),
            "generated cluster token does not satisfy authentication requirements");
        Assert(!string.Equals(generatedToken, secondGeneratedToken, StringComparison.Ordinal),
            "successive generated cluster tokens unexpectedly repeated");

        const string token = "cluster-test-token-123456";
        Assert(AIWeatherClusterProtocol.IsTokenUsable(token), "cluster token length validation");
        Assert(AIWeatherClusterProtocol.FixedTimeTokenEquals(token, token), "cluster constant-time token match");
        Assert(!AIWeatherClusterProtocol.FixedTimeTokenEquals(token, token + "x"), "cluster token mismatch");

        var authenticationTime = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var authentication = AIWeatherClusterProtocol.CreateRequestAuthentication(
            token,
            "GET",
            "/api/v1/status",
            "smoke-replica",
            authenticationTime,
            "00112233445566778899AABBCCDDEEFF");
        var authenticationHeaders = AuthenticationHeaders(authentication);
        Assert(AIWeatherClusterProtocol.TryValidateRequestAuthentication(
                token,
                "GET",
                "/api/v1/status",
                authenticationHeaders,
                authenticationTime.AddSeconds(10),
                out _,
                out _),
            "cluster HMAC request authentication");
        Assert(!AIWeatherClusterProtocol.TryValidateRequestAuthentication(
                token,
                "GET",
                "/api/v1/health",
                authenticationHeaders,
                authenticationTime.AddSeconds(10),
                out _,
                out _),
            "cluster HMAC did not bind the request path");
        Assert(!AIWeatherClusterProtocol.TryValidateRequestAuthentication(
                token,
                "GET",
                "/api/v1/status",
                authenticationHeaders,
                authenticationTime.Add(AIWeatherClusterProtocol.AuthenticationClockSkew).AddSeconds(1),
                out _,
                out _),
            "cluster HMAC accepted an expired request timestamp");

        var failoverConfiguration = new AIWeatherFailoverConfiguration
        {
            CaptureMode = (int)CaptureMode.RTSPStream,
            RtspUrl = "rtsp://embedded-user:embedded-secret@camera.test/stream?api_key=query-secret",
            RtspUsername = "camera-user",
            RtspPassword = "camera-secret",
            CheckIntervalMinutes = 3,
            UseSunAltitudeLimit = true,
            SunAltitudeLimitDegrees = -6,
            CloudCoverageThreshold = 70,
            CloudCoverageSafeThreshold = 40,
            AnalysisProvider = "Gemini",
            SelectedModel = "gemini-test",
            GeminiKey = "gemini-secret",
            GeminiRequestEveryChecks = 2
        };
        var encrypted = AIWeatherClusterProtocol.EncryptFailoverConfiguration(
            failoverConfiguration,
            token,
            "smoke-primary",
            "smoke-session",
            authenticationTime.UtcDateTime);
        var decrypted = AIWeatherClusterProtocol.DecryptFailoverConfiguration(encrypted, token);
        Assert(decrypted.RtspUrl == failoverConfiguration.RtspUrl
               && decrypted.RtspPassword == failoverConfiguration.RtspPassword
               && decrypted.GeminiKey == failoverConfiguration.GeminiKey,
            "encrypted failover configuration round trip");
        var replicaSummary = AIWeatherReplicaConfigurationSummary.FromConfiguration(
            decrypted,
            encrypted.Revision,
            encrypted.GeneratedUtc);
        var serializedSummary = JsonSerializer.Serialize(replicaSummary);
        Assert(replicaSummary.AnalysisProvider == "Gemini"
               && replicaSummary.SelectedModel == "gemini-test"
               && replicaSummary.ApiCredentialRequired
               && replicaSummary.ApiCredentialConfigured,
            "replica synchronized configuration summary omitted provider state");
        Assert(replicaSummary.CaptureCredentialsConfigured
               && replicaSummary.CaptureSource.Contains("[REDACTED]", StringComparison.Ordinal)
               && !serializedSummary.Contains("camera-secret", StringComparison.Ordinal)
               && !serializedSummary.Contains("embedded-secret", StringComparison.Ordinal)
               && !serializedSummary.Contains("query-secret", StringComparison.Ordinal)
               && !serializedSummary.Contains("gemini-secret", StringComparison.Ordinal),
            "replica synchronized configuration summary exposed a secret");
        var cachedSummary = AIWeatherReplicaConfigurationSummary.FromEncryptedCache(
            JsonSerializer.Serialize(encrypted),
            token);
        Assert(cachedSummary.Revision == encrypted.Revision
               && cachedSummary.GeneratedUtc == encrypted.GeneratedUtc,
            "replica synchronized configuration cache summary lost envelope identity");
        var httpOllamaSummary = AIWeatherReplicaConfigurationSummary.FromConfiguration(
            new AIWeatherFailoverConfiguration
            {
                CaptureMode = (int)CaptureMode.INDICamera,
                HttpImageUrl = "https://camera-user:camera-pass@camera.test/latest.jpg?token=image-secret",
                AnalysisProvider = "Ollama",
                SelectedModel = "vision-test",
                OllamaBaseUrl = "http://model-user:model-pass@model.test/v1?api_key=model-secret"
            },
            "SAFE-DISPLAY",
            authenticationTime.UtcDateTime);
        var serializedHttpOllamaSummary = JsonSerializer.Serialize(httpOllamaSummary);
        Assert(httpOllamaSummary.CaptureCredentialsConfigured
               && !serializedHttpOllamaSummary.Contains("camera-pass", StringComparison.Ordinal)
               && !serializedHttpOllamaSummary.Contains("image-secret", StringComparison.Ordinal)
               && !serializedHttpOllamaSummary.Contains("model-pass", StringComparison.Ordinal)
               && !serializedHttpOllamaSummary.Contains("model-secret", StringComparison.Ordinal),
            "replica synchronized HTTP/Ollama summary exposed URL credentials");
        AssertThrows<CryptographicException>(
            () => AIWeatherClusterProtocol.DecryptFailoverConfiguration(
                encrypted,
                "different-cluster-token-123456"),
            "failover configuration decrypted with the wrong token");
        var tampered = JsonSerializer.Deserialize<AIWeatherFailoverConfigurationEnvelope>(
                           JsonSerializer.Serialize(encrypted))
                       ?? throw new InvalidOperationException("could not clone encrypted failover envelope");
        var tamperedTag = Convert.FromBase64String(tampered.Tag);
        tamperedTag[0] ^= 0x80;
        tampered.Tag = Convert.ToBase64String(tamperedTag);
        AssertThrows<CryptographicException>(
            () => AIWeatherClusterProtocol.DecryptFailoverConfiguration(tampered, token),
            "tampered failover configuration authentication tag was accepted");

        VerifyFailoverStateMachine();

        var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        long sequence = 0;
        var failoverRevision = AIWeatherClusterProtocol.ComputeConfigurationRevision(
            failoverConfiguration,
            token);
        using var server = new AIWeatherClusterServer(
            port,
            token,
            "smoke-primary",
            () => new AIWeatherClusterSnapshot
            {
                Sequence = Interlocked.Increment(ref sequence),
                Connected = true,
                Monitoring = true,
                IsSafe = true,
                SafetyReason = "safe",
                WeatherCondition = WeatherCondition.Clear.ToString(),
                CloudCoverage = 5,
                Confidence = 99,
                Provider = "Smoke",
                Model = "deterministic",
                AnalysisUtc = DateTime.UtcNow,
                SourceFresh = true,
                FailoverConfigurationAvailable = true,
                FailoverConfigurationRevision = failoverRevision
            },
            () => failoverConfiguration);
        server.Start();

        using var client = new AIWeatherClusterClient(
            $"http://127.0.0.1:{port}",
            token,
            TimeSpan.FromSeconds(3));
        var first = await client.PollAsync(CancellationToken.None);
        var second = await client.PollAsync(CancellationToken.None);
        Assert(first.Product == AIWeatherClusterProtocol.Product, "cluster product identity");
        Assert(first.SchemaVersion == AIWeatherClusterProtocol.SchemaVersion, "cluster schema identity");
        Assert(first.NodeId == "smoke-primary", "cluster node identity");
        Assert(first.SessionId == second.SessionId, "cluster stable session");
        Assert(second.Sequence > first.Sequence, "cluster monotonic sequence");
        Assert(second.IsSafe && second.SourceFresh, "cluster weather status round trip");
        Assert(first.FailoverConfigurationAvailable
               && first.FailoverConfigurationRevision == failoverRevision,
            "cluster failover configuration advertisement");
        var fetchedEnvelope = await client.FetchFailoverConfigurationAsync(
            second,
            CancellationToken.None);
        var fetchedConfiguration = client.DecryptFailoverConfiguration(fetchedEnvelope);
        Assert(fetchedConfiguration.RtspPassword == failoverConfiguration.RtspPassword
               && fetchedConfiguration.GeminiKey == failoverConfiguration.GeminiKey,
            "cluster encrypted failover configuration exchange");

        using (var rawClient = new HttpClient(new HttpClientHandler { UseProxy = false }))
        {
            var replayAuthentication = AIWeatherClusterProtocol.CreateRequestAuthentication(
                token,
                "GET",
                "/api/v1/health",
                "replay-test-node");
            using var acceptedReplay = await rawClient.SendAsync(
                BuildSignedRequest(
                    $"http://127.0.0.1:{port}/api/v1/health",
                    replayAuthentication));
            using var rejectedReplay = await rawClient.SendAsync(
                BuildSignedRequest(
                    $"http://127.0.0.1:{port}/api/v1/health",
                    replayAuthentication));
            Assert(acceptedReplay.IsSuccessStatusCode
                   && rejectedReplay.StatusCode == HttpStatusCode.Unauthorized,
                "cluster nonce replay protection");
        }

        using var unauthorized = new AIWeatherClusterClient(
            $"http://127.0.0.1:{port}",
            "wrong-token-1234567890",
            TimeSpan.FromSeconds(3));
        try
        {
            await unauthorized.PollAsync(CancellationToken.None);
            throw new InvalidOperationException("cluster unauthorized request was accepted");
        }
        catch (AIWeatherClusterException ex)
        {
            Assert(ex.Failure == AIWeatherReplicaFailure.Authentication, "cluster authentication failure category");
        }
    }

    private static Dictionary<string, string> AuthenticationHeaders(
        AIWeatherRequestAuthentication authentication) => new(StringComparer.OrdinalIgnoreCase)
    {
        [AIWeatherClusterProtocol.AuthenticationVersionHeader] = authentication.Version,
        [AIWeatherClusterProtocol.AuthenticationNodeHeader] = authentication.NodeId,
        [AIWeatherClusterProtocol.AuthenticationTimestampHeader] = authentication.UnixTimeSeconds.ToString(CultureInfo.InvariantCulture),
        [AIWeatherClusterProtocol.AuthenticationNonceHeader] = authentication.Nonce,
        [AIWeatherClusterProtocol.AuthenticationSignatureHeader] = authentication.Signature
    };

    private static HttpRequestMessage BuildSignedRequest(
        string url,
        AIWeatherRequestAuthentication authentication)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var header in AuthenticationHeaders(authentication))
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return request;
    }

    private static void VerifyFailoverStateMachine()
    {
        var state = new AIWeatherFailoverStateMachine();
        var start = new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        var failoverAfter = TimeSpan.FromSeconds(60);
        var recovery = TimeSpan.FromSeconds(30);
        Assert(state.Observe(
                AIWeatherFailoverObservation.NetworkUnavailable,
                start,
                enabled: true,
                configurationReady: true,
                failoverAfter,
                recovery) == AIWeatherFailoverTransition.None,
            "failover activated on the first network miss");
        Assert(state.Observe(
                AIWeatherFailoverObservation.NetworkUnavailable,
                start.AddSeconds(59),
                enabled: true,
                configurationReady: true,
                failoverAfter,
                recovery) == AIWeatherFailoverTransition.None,
            "failover activated before the outage threshold");
        Assert(state.Observe(
                AIWeatherFailoverObservation.NetworkUnavailable,
                start.AddSeconds(60),
                enabled: true,
                configurationReady: true,
                failoverAfter,
                recovery) == AIWeatherFailoverTransition.ActivateLocal
               && state.LocalActive,
            "failover did not activate at the outage threshold");
        Assert(state.Observe(
                AIWeatherFailoverObservation.PrimaryReachable,
                start.AddSeconds(61),
                enabled: true,
                configurationReady: true,
                failoverAfter,
                recovery) == AIWeatherFailoverTransition.None
               && state.LocalActive,
            "failover returned on the first recovered poll");
        Assert(state.Observe(
                AIWeatherFailoverObservation.PrimaryReachable,
                start.AddSeconds(91),
                enabled: true,
                configurationReady: true,
                failoverAfter,
                recovery) == AIWeatherFailoverTransition.ReturnToPrimary
               && !state.LocalActive,
            "failover did not return after a stable recovery window");

        var fatal = new AIWeatherFailoverStateMachine();
        Assert(fatal.Observe(
                AIWeatherFailoverObservation.FatalConfigurationFailure,
                start,
                enabled: true,
                configurationReady: true,
                TimeSpan.Zero,
                TimeSpan.Zero) == AIWeatherFailoverTransition.None
               && !fatal.LocalActive,
            "authentication/protocol failure incorrectly activated local failover");
    }

    private static void VerifyLatestRtspFrameBuffer()
    {
        using var buffer = new LatestRtspFrameBuffer();
        var now = DateTime.UtcNow;

        buffer.Publish(CreateSolidBitmap(Color.Red), now);
        Assert(buffer.TryCloneNewerThan(
                0,
                now.AddMilliseconds(100),
                TimeSpan.FromSeconds(10),
                out var first,
                out var firstSequence,
                out var firstReceivedUtc,
                out var firstAge),
            "The first fresh RTSP frame was not returned");
        using (first)
        {
            Assert(first!.GetPixel(0, 0).ToArgb() == Color.Red.ToArgb(),
                "The RTSP latest-frame buffer returned the wrong first frame");
        }
        Assert(firstSequence == 1 && firstReceivedUtc == now && firstAge == TimeSpan.FromMilliseconds(100),
            "RTSP frame metadata did not match the published frame");

        Assert(!buffer.TryCloneNewerThan(
                firstSequence,
                now.AddMilliseconds(200),
                TimeSpan.FromSeconds(10),
                out var repeated,
                out _,
                out _,
                out _),
            "The same buffered RTSP frame was delivered twice");
        repeated?.Dispose();

        buffer.Publish(CreateSolidBitmap(Color.Blue), now.AddSeconds(1));
        Assert(buffer.TryCloneNewerThan(
                firstSequence,
                now.AddSeconds(1.1),
                TimeSpan.FromSeconds(10),
                out var second,
                out var secondSequence,
                out _,
                out _),
            "A newer RTSP frame did not replace the previous frame");
        using (second)
        {
            Assert(second!.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(),
                "The RTSP latest-frame buffer did not retain the newest frame");
        }

        buffer.Publish(CreateSolidBitmap(Color.Green), now.AddSeconds(2));
        Assert(!buffer.TryCloneNewerThan(
                secondSequence,
                now.AddSeconds(13),
                TimeSpan.FromSeconds(10),
                out var stale,
                out _,
                out _,
                out _),
            "An expired RTSP frame was incorrectly returned as fresh");
        stale?.Dispose();

        buffer.Clear();
        Assert(!buffer.TryCloneNewerThan(
                0,
                now,
                TimeSpan.FromSeconds(10),
                out var cleared,
                out _,
                out _,
                out _),
            "Clearing the RTSP latest-frame buffer left a frame available");
        cleared?.Dispose();
    }

    private static Bitmap CreateSolidBitmap(Color color)
    {
        var bitmap = new Bitmap(2, 2);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static void VerifyReplicaPreviewSourcePolicy()
    {
        var synchronized = new AIWeatherFailoverConfiguration
        {
            CaptureMode = (int)CaptureMode.RTSPStream,
            RtspUrl = "rtsp://camera.local:554/live/main",
            RtspUsername = "replica-user",
            RtspPassword = "replica-secret"
        };

        var resolved = AIWeatherSafetyMonitor.TryResolveReplicaPreviewSource(
            ClusterNodeMode.Replica,
            synchronized,
            out var mode,
            out var source,
            out var username,
            out var password);
        Assert(resolved
               && mode == CaptureMode.RTSPStream
               && source == synchronized.RtspUrl
               && username == synchronized.RtspUsername
               && password == synchronized.RtspPassword,
            "A following replica could not resolve its synchronized local preview source");

        Assert(!AIWeatherSafetyMonitor.TryResolveReplicaPreviewSource(
                ClusterNodeMode.Primary,
                synchronized,
                out _,
                out _,
                out _,
                out _),
            "A non-replica node incorrectly resolved the replica-only preview source");
    }

    private static void VerifyReplicaPreviewRetryGate()
    {
        var gate = new ReplicaPreviewRetryGate();
        var now = new DateTime(2026, 8, 29, 8, 0, 0, DateTimeKind.Utc);
        const string firstSource = "camera-a:554/live";
        const string secondSource = "camera-b:554/live";

        Assert(gate.ShouldAttempt(firstSource, now, forceRestart: false),
            "A fresh replica preview source was incorrectly held in retry backoff");

        gate.RecordFailure(firstSource, now, TimeSpan.FromMinutes(1));
        Assert(!gate.ShouldAttempt(firstSource, now.AddSeconds(5), forceRestart: false),
            "Repeated cluster notifications bypassed replica preview failure backoff");
        Assert(gate.ShouldAttempt(secondSource, now.AddSeconds(5), forceRestart: false),
            "Changing the synchronized RTSP source did not bypass stale-source backoff");
        Assert(gate.ShouldAttempt(firstSource, now.AddSeconds(5), forceRestart: true),
            "Manual replica preview retry did not bypass automatic backoff");

        gate.RecordFailure(firstSource, now, TimeSpan.FromMinutes(1));
        Assert(gate.ShouldAttempt(firstSource, now.AddMinutes(1), forceRestart: false),
            "Replica preview did not become retryable after the backoff expired");
    }

    private static async Task VerifySharedRtspPreviewFrameProviderAsync()
    {
        var provider = new SharedRtspPreviewFrameProvider();
        var reservedIdentity = string.Empty;
        provider.SourceReserved += identity =>
        {
            reservedIdentity = identity;
            return Task.CompletedTask;
        };

        var registration = await provider.RegisterAsync(
            "rtsp://camera-user:camera-secret@CAMERA.local:554/live/main",
            _ => Task.FromResult<Bitmap?>(CreateSolidBitmap(Color.Purple)));
        try
        {
            Assert(!reservedIdentity.Contains("camera-user", StringComparison.Ordinal)
                   && !reservedIdentity.Contains("camera-secret", StringComparison.Ordinal),
                "The shared RTSP source identity retained credentials");

            var captured = await provider.TryCaptureAsync(
                "rtsp://different-user:different-secret@camera.local:554/live/main",
                TimeSpan.FromSeconds(1));
            Assert(captured.Status == SharedRtspPreviewCaptureStatus.Captured,
                "A matching shared preview was not used when only credentials differed");
            using (captured.Frame)
            {
                Assert(captured.Frame!.GetPixel(0, 0).ToArgb() == Color.Purple.ToArgb(),
                    "The shared preview returned the wrong frame");
            }

            var wrongSource = await provider.TryCaptureAsync(
                "rtsp://camera.local:554/live/sub",
                TimeSpan.FromSeconds(1));
            Assert(wrongSource.Status == SharedRtspPreviewCaptureStatus.Unavailable,
                "A shared preview was incorrectly reused for a different RTSP path");
        }
        finally
        {
            registration.Dispose();
        }

        var afterRelease = await provider.TryCaptureAsync(
            "rtsp://camera.local:554/live/main",
            TimeSpan.FromSeconds(1));
        Assert(afterRelease.Status == SharedRtspPreviewCaptureStatus.Unavailable,
            "A released preview registration remained visible");
    }

    private static async Task VerifyUnifiedCapturePrefersSharedPreviewAsync()
    {
        var provider = new SharedRtspPreviewFrameProvider();
        using var decoder = new FakeRtspFrameCaptureService();
        using var capture = new UnifiedCaptureService(null, provider, decoder);
        capture.CurrentMode = CaptureMode.RTSPStream;
        capture.ConfigureRTSP(
            "rtsp://camera.local:554/live/main",
            "configured-user",
            "configured-secret");

        var successfulPreview = await provider.RegisterAsync(
            "rtsp://embedded-user:embedded-secret@camera.local:554/live/main",
            _ => Task.FromResult<Bitmap?>(CreateSolidBitmap(Color.Orange)));
        try
        {
            // The same registered preview is consumed before and after a notional replica
            // takeover. No lifecycle transition may open a second decoder in this terminal.
            using var followerFrame = await capture.CaptureImageAsync();
            using var takeoverFrame = await capture.CaptureImageAsync();
            Assert(followerFrame != null
                   && followerFrame.GetPixel(0, 0).ToArgb() == Color.Orange.ToArgb()
                   && takeoverFrame != null
                   && takeoverFrame.GetPixel(0, 0).ToArgb() == Color.Orange.ToArgb(),
                "Unified capture did not return the active shared preview frame");
            Assert(decoder.InitializeCalls == 0 && decoder.CaptureCalls == 0,
                "Unified capture opened the independent RTSP decoder beside an active preview");
        }
        finally
        {
            successfulPreview.Dispose();
        }

        var failedPreview = await provider.RegisterAsync(
            "rtsp://camera.local:554/live/main",
            _ => Task.FromResult<Bitmap?>(null));
        try
        {
            decoder.NextFrameColor = Color.CadetBlue;
            using var recovered = await capture.CaptureImageAsync();
            Assert(recovered != null
                   && recovered.GetPixel(0, 0).ToArgb() == Color.CadetBlue.ToArgb(),
                "A failed shared preview did not fall back to the independent RTSP decoder");
            Assert(decoder.InitializeCalls == 1 && decoder.CaptureCalls == 1,
                "The independent RTSP health fallback was not attempted exactly once");
        }
        finally
        {
            failedPreview.Dispose();
        }

        decoder.NextFrameColor = Color.DarkCyan;
        using var backgroundFrame = await capture.CaptureImageAsync();
        Assert(backgroundFrame != null
               && backgroundFrame.GetPixel(0, 0).ToArgb() == Color.DarkCyan.ToArgb(),
            "Unified capture did not retain the independent decoder when no preview existed");
        Assert(decoder.InitializeCalls == 1 && decoder.CaptureCalls == 2,
            "The healthy background RTSP decoder was not reused after preview fallback");
    }

    private static void VerifyRtspPreviewFit()
    {
        var landscape = VideoFitCalculator.FitInside(676, 733, 3840, 2160);
        Assert(Math.Abs(landscape.Width - 676) < 0.001,
            "A landscape RTSP preview did not use the available width");
        Assert(Math.Abs(landscape.Height - 380.25) < 0.001,
            "A landscape RTSP preview did not preserve its aspect ratio");

        var portrait = VideoFitCalculator.FitInside(800, 600, 1080, 1920);
        Assert(Math.Abs(portrait.Width - 337.5) < 0.001,
            "A portrait RTSP preview did not preserve its aspect ratio");
        Assert(Math.Abs(portrait.Height - 600) < 0.001,
            "A portrait RTSP preview did not use the available height");

        var invalid = VideoFitCalculator.FitInside(0, 600, 1920, 1080);
        Assert(invalid.Width == 1 && invalid.Height == 1,
            "Invalid RTSP preview dimensions did not use the safe fallback size");
    }

    private static void VerifyRtspPreviewHealthWatchdog()
    {
        Assert(RtspPreviewHealthMonitor.IsLateFrameMessage("More than 11 late frames, dropping frame"),
            "The RTSP preview watchdog did not recognize VLC's late-frame warning");
        Assert(RtspPreviewHealthMonitor.IsLateFrameMessage("more than 5 seconds of late video -> dropping frame"),
            "The RTSP preview watchdog did not recognize VLC's sustained late-video error");
        Assert(RtspPreviewHealthMonitor.IsLateFrameMessage("picture is too late to be displayed (missing 6006 ms)"),
            "The RTSP preview watchdog did not recognize VLC's late-picture warning");
        Assert(!RtspPreviewHealthMonitor.IsLateFrameMessage("RTSP stream playing successfully"),
            "The RTSP preview watchdog classified a healthy playback message as late");

        var monitor = new RtspPreviewHealthMonitor(
            burstResetGap: TimeSpan.FromSeconds(2),
            minimumLateDuration: TimeSpan.FromSeconds(3),
            minimumLateMessages: 4,
            summaryInterval: TimeSpan.FromSeconds(2),
            recoveryCooldown: TimeSpan.FromSeconds(10));
        var started = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        var first = monitor.Observe("picture is too late", started);
        Assert(first.ShouldLogSummary && !first.ShouldRecover && first.LateMessageCount == 1,
            "The first late-frame observation did not start a throttled diagnostic burst");
        Assert(!monitor.Observe("picture is too late", started.AddSeconds(1)).ShouldRecover,
            "The RTSP preview watchdog recovered before the sustained-duration threshold");
        Assert(!monitor.Observe("late video", started.AddSeconds(2)).ShouldRecover,
            "The RTSP preview watchdog recovered before the message threshold");
        var recovery = monitor.Observe("late frames", started.AddSeconds(3));
        Assert(recovery.ShouldRecover && recovery.LateMessageCount == 4,
            "The RTSP preview watchdog did not recover after a sustained late-frame burst");

        monitor.ResetBurst();
        monitor.Observe("picture is too late", started.AddSeconds(4));
        monitor.Observe("picture is too late", started.AddSeconds(5));
        monitor.Observe("picture is too late", started.AddSeconds(6));
        var cooledDown = monitor.Observe("picture is too late", started.AddSeconds(7));
        Assert(!cooledDown.ShouldRecover,
            "The RTSP preview watchdog ignored its recovery cooldown and would create a restart loop");

        var gapMonitor = new RtspPreviewHealthMonitor(
            burstResetGap: TimeSpan.FromSeconds(1),
            minimumLateDuration: TimeSpan.Zero,
            minimumLateMessages: 3,
            summaryInterval: TimeSpan.FromSeconds(10),
            recoveryCooldown: TimeSpan.Zero);
        gapMonitor.Observe("late frames", started);
        gapMonitor.Observe("late frames", started.AddMilliseconds(500));
        var afterGap = gapMonitor.Observe("late frames", started.AddSeconds(3));
        Assert(afterGap.LateMessageCount == 1 && !afterGap.ShouldRecover,
            "A quiet gap did not reset the RTSP preview late-frame burst");
    }

    private static async Task<int> RunLiveRtspFreshnessCheckAsync()
    {
        var rtspUrl = Environment.GetEnvironmentVariable("AIWEATHER_RTSP_TEST_URL");
        if (string.IsNullOrWhiteSpace(rtspUrl))
        {
            Console.Error.WriteLine("AIWEATHER_RTSP_TEST_URL is required for --rtsp-live.");
            return 2;
        }

        var outputRoot = Environment.GetEnvironmentVariable("AIWEATHER_RTSP_TEST_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            outputRoot = Path.Combine(Path.GetTempPath(), "AIWeatherRtspFreshness");
        }
        Directory.CreateDirectory(outputRoot);

        using var service = new RtspCaptureService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        if (!await service.InitializeAsync(rtspUrl, timeout.Token))
        {
            Console.Error.WriteLine("FAIL live RTSP freshness: initialization failed (URL redacted).");
            return 1;
        }

        using var first = await service.CaptureFrameAsync(timeout.Token);
        if (first == null)
        {
            Console.Error.WriteLine("FAIL live RTSP freshness: first fresh frame was unavailable.");
            return 1;
        }

        var firstPath = Path.Combine(outputRoot, $"freshness_1_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
        first.Save(firstPath, System.Drawing.Imaging.ImageFormat.Jpeg);

        await Task.Delay(TimeSpan.FromSeconds(12), timeout.Token);
        using var second = await service.CaptureFrameAsync(timeout.Token);
        if (second == null)
        {
            Console.Error.WriteLine("FAIL live RTSP freshness: second fresh frame was unavailable.");
            return 1;
        }

        var secondPath = Path.Combine(outputRoot, $"freshness_2_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
        second.Save(secondPath, System.Drawing.Imaging.ImageFormat.Jpeg);

        Console.WriteLine("PASS live RTSP freshness: two newly received frames were captured 12 seconds apart.");
        Console.WriteLine(firstPath);
        Console.WriteLine(secondPath);
        return 0;
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
        Assert(Properties.Settings.Default.GeminiRequestEveryChecks == 1,
            "Default Gemini request pacing must preserve one online call per weather check");
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
            Assert(UiLocalization.Text("Review.Delete") == "Delete sample permanently",
                "English permanent-delete localization failed");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            Assert(UiLocalization.Text("Preview.ActivityLog") == "活动日志",
                "Chinese localization was not selected for zh-CN");
            Assert(UiLocalization.Text("Preview.VideoRetry") == "重试本机视频"
                   && UiLocalization.Text("Preview.VideoSurfaceUnavailable").Contains("无法显示", StringComparison.Ordinal),
                "Chinese local-preview diagnostics were not localized");
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
            Assert(UiLocalization.Text("Review.Delete") == "永久删除样本",
                "Chinese permanent-delete localization failed");

            fallback.Provenance.FailureCategory = AnalysisFailureCategory.QuotaExhausted;
            fallback.Provenance.RetryAfterUtc = DateTime.UtcNow.AddMinutes(10);
            var quotaDescription = UiLocalization.AnalysisDescription(fallback, "Gemini");
            Assert(quotaDescription.Contains("Gemini API 配额暂不可用", StringComparison.Ordinal)
                   && quotaDescription.Contains("下次在线尝试时间", StringComparison.Ordinal),
                "Gemini quota fallback description was not localized for zh-CN");
            Assert(UiLocalization.FallbackStatus(fallback.Provenance).Contains("API 配额暂停至", StringComparison.Ordinal),
                "Gemini quota source summary was not localized for zh-CN");

            fallback.Provenance.QuotaId = "GenerateRequestsPerDayPerProjectPerModel-FreeTier";
            var dailyQuotaDescription = UiLocalization.AnalysisDescription(fallback, "Gemini");
            Assert(dailyQuotaDescription.Contains("每日 API 配额已用尽", StringComparison.Ordinal)
                   && dailyQuotaDescription.Contains("太平洋时间午夜", StringComparison.Ordinal)
                   && dailyQuotaDescription.Contains("本地时间 UTC", StringComparison.Ordinal),
                "Gemini daily-quota reset and time zone were not explained in zh-CN");
            Assert(UiLocalization.FallbackStatus(fallback.Provenance).Contains("预计于", StringComparison.Ordinal),
                "Gemini daily-quota source summary was not localized for zh-CN");
            fallback.Provenance.QuotaId = null;

            fallback.Provenance.FailureCategory = AnalysisFailureCategory.ScheduledLocal;
            fallback.Provenance.RequestEveryChecks = 12;
            var scheduledDescription = UiLocalization.AnalysisDescription(fallback, "Gemini");
            Assert(scheduledDescription.Contains("每 12 次天气检查在线调用一次", StringComparison.Ordinal)
                   && scheduledDescription.Contains("本次使用本地分析", StringComparison.Ordinal),
                "Gemini scheduled-local description was not localized for zh-CN");
            Assert(UiLocalization.FallbackStatus(fallback.Provenance).Contains("Gemini 每 12 次调用一次", StringComparison.Ordinal),
                "Gemini scheduled-local source summary was not localized for zh-CN");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
            Assert(UiLocalization.Text("Preview.ActivityLog") == "Activity Log",
                "Unsupported cultures must fall back to English");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static void VerifyGeminiQuotaPolicy()
    {
        const string quotaJson = """
        {
          "error": {
            "code": 429,
            "message": "You exceeded your current quota. Please retry in 57.57808206s.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "generativelanguage.googleapis.com/generate_content_free_tier_requests",
                    "quotaId": "GenerateRequestsPerDayPerProjectPerModel-FreeTier"
                  }
                ]
              },
              {
                "@type": "type.googleapis.com/google.rpc.RetryInfo",
                "retryDelay": "57.578082060s"
              }
            ]
          }
        }
        """;

        var quota = GeminiQuotaParser.Parse((HttpStatusCode)429, quotaJson);
        Assert(quota.IsQuotaFailure, "Gemini QuotaFailure envelope was not recognized");
        Assert(quota.ProviderFailureCode == "quota_exhausted",
            "Gemini quota provider failure code is unstable");
        Assert(quota.QuotaMetric?.EndsWith("generate_content_free_tier_requests", StringComparison.Ordinal) == true,
            "Gemini quota metric was not preserved");
        Assert(quota.QuotaId?.Contains("PerDay", StringComparison.Ordinal) == true,
            "Gemini quota id was not preserved");
        Assert(quota.IsDailyQuota, "Gemini per-day quota identity was not recognized");
        Assert(quota.RetryDelay.HasValue
               && Math.Abs(quota.RetryDelay.Value.TotalSeconds - 57.578082060) < 0.001,
            "Gemini RetryInfo duration was not parsed without truncation");

        var generic429 = GeminiQuotaParser.Parse(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":429,\"message\":\"Too many requests\"}}");
        Assert(!generic429.IsQuotaFailure,
            "A generic HTTP 429 was incorrectly promoted to an explicit quota failure");

        var currentDailyCode = GeminiQuotaParser.Parse(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":\"quota_exceeded\",\"message\":\"Daily quota exhausted\"}}");
        Assert(currentDailyCode.IsQuotaFailure && currentDailyCode.IsDailyQuota,
            "The current Gemini quota_exceeded machine code was not treated as daily quota");

        var currentRateCode = GeminiQuotaParser.Parse(
            (HttpStatusCode)429,
            "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Too many requests\"}}");
        Assert(!currentRateCode.IsQuotaFailure,
            "The current Gemini rate_limit_exceeded code was incorrectly treated as daily quota");

        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var dailyBreaker = new GeminiQuotaCircuitBreaker();
        var daily = dailyBreaker.RecordFailure(now, quota);
        Assert(daily.IsDailyQuota, "Daily Gemini quota state was not retained");
        Assert(daily.RetryAfterUtc == new DateTimeOffset(2026, 8, 23, 7, 2, 0, TimeSpan.Zero),
            "Daily Gemini quota did not pause until Pacific midnight plus the safety margin");

        var shortQuota = new GeminiQuotaInfo
        {
            IsQuotaFailure = true,
            ProviderFailureCode = "quota_exhausted",
            QuotaMetric = quota.QuotaMetric,
            QuotaId = "GenerateRequestsPerMinutePerProjectPerModel-FreeTier",
            RetryDelay = quota.RetryDelay,
            IsDailyQuota = false
        };
        var breaker = new GeminiQuotaCircuitBreaker();
        var first = breaker.RecordFailure(now, shortQuota);
        Assert(Math.Abs((first.RetryAfterUtc - now).TotalSeconds - 57.578082060) < 0.001,
            "First Gemini quota failure did not honor the full provider RetryInfo");
        Assert(breaker.TryGetActive(now.AddSeconds(30), out _),
            "Gemini quota circuit did not suppress a premature API request");

        var secondAt = first.RetryAfterUtc.AddSeconds(1);
        var second = breaker.RecordFailure(secondAt, shortQuota);
        Assert(second.RetryAfterUtc - secondAt >= TimeSpan.FromMinutes(10),
            "Second consecutive quota failure did not escalate to 10 minutes");

        var thirdAt = second.RetryAfterUtc.AddSeconds(1);
        var third = breaker.RecordFailure(thirdAt, shortQuota);
        Assert(third.RetryAfterUtc - thirdAt >= TimeSpan.FromMinutes(30),
            "Third consecutive quota failure did not escalate to 30 minutes");

        var fourthAt = third.RetryAfterUtc.AddSeconds(1);
        var fourth = breaker.RecordFailure(fourthAt, shortQuota);
        Assert(fourth.RetryAfterUtc - fourthAt >= TimeSpan.FromMinutes(60),
            "Fourth consecutive quota failure did not escalate to 60 minutes");
        Assert(breaker.Reset(), "Gemini quota circuit did not report a state reset");
        Assert(!breaker.TryGetActive(fourthAt, out _),
            "Gemini quota circuit remained open after a successful-response reset");
    }

    private static async Task VerifyQuotaFallbackMetadataAsync()
    {
        var retryAfterUtc = DateTime.UtcNow.AddMinutes(10);
        var orchestrator = new WeatherAnalysisOrchestrator();
        using var frame = CreateFrame();
        var bundle = await orchestrator.AnalyzeAsync(
            new QuotaTeacher(retryAfterUtc),
            frame,
            astroContext: null,
            CancellationToken.None);

        Assert(bundle.UsedFallback, "Quota-suppressed Gemini teacher did not select local fallback");
        Assert(bundle.EffectiveResult.Provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted,
            "Quota failure category was not copied to the effective local result");
        Assert(bundle.EffectiveResult.Provenance.RetryAfterUtc == retryAfterUtc,
            "Quota retry timestamp was not copied to the effective local result");
        Assert(bundle.EffectiveResult.Provenance.RequestSuppressed,
            "Quota request-suppression state was not copied to the effective local result");
        Assert(bundle.EffectiveResult.Provenance.QuotaId == "test-daily-quota",
            "Quota identity was not copied to the effective local result");
    }

    private static async Task VerifyGeminiServiceSuppressesQuotaRetriesAsync()
    {
        const string quotaJson = """
        {
          "error": {
            "code": 429,
            "message": "Quota exceeded. Please retry in 57.5s.",
            "status": "RESOURCE_EXHAUSTED",
            "details": [
              {
                "@type": "type.googleapis.com/google.rpc.QuotaFailure",
                "violations": [
                  {
                    "quotaMetric": "test.requests",
                    "quotaId": "GenerateRequestsPerMinutePerProjectPerModel-FreeTier"
                  }
                ]
              },
              {
                "@type": "type.googleapis.com/google.rpc.RetryInfo",
                "retryDelay": "57.5s"
              }
            ]
          }
        }
        """;

        var handler = new QuotaResponseHandler(quotaJson);
        using var http = new HttpClient(handler);
        var breaker = new GeminiQuotaCircuitBreaker();
        var now = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test",
            http,
            breaker,
            () => now);
        Assert(await service.InitializeAsync(), "Injected Gemini service failed to initialize");

        using var frame = CreateFrame();
        var first = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(!first.Success
               && first.Provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted,
            "Gemini service did not expose an explicit quota failure");
        Assert(first.Provenance.Attempts == 1 && handler.RequestCount == 1,
            "Gemini service retried an explicit quota response within the same weather check");
        Assert(!first.Provenance.RequestSuppressed && first.Provenance.HttpStatus == 429,
            "The quota-triggering HTTP request was incorrectly marked as suppressed");

        var suppressed = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(!suppressed.Success
               && suppressed.Provenance.RequestSuppressed
               && suppressed.Provenance.Attempts == 0,
            "Gemini service did not mark an open-circuit check as request-suppressed");
        Assert(handler.RequestCount == 1,
            "Gemini service sent traffic while its quota circuit was open");

        now = first.Provenance.RetryAfterUtc!.Value.AddSeconds(1);
        var secondFailure = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(handler.RequestCount == 2 && secondFailure.Provenance.Attempts == 1,
            "Gemini service did not perform exactly one probe after the provider delay expired");
        Assert(secondFailure.Provenance.RetryAfterUtc - now.UtcDateTime >= TimeSpan.FromMinutes(10),
            "Repeated Gemini quota rejection did not escalate the service circuit to 10 minutes");
    }

    private static async Task VerifyGeminiRequestPacingAsync()
    {
        const string successfulEnvelope = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "{\"condition\":\"Clear\",\"cloudCoverage\":5,\"rainDetected\":false,\"fogDetected\":false,\"isSafe\":true,\"description\":\"Clear sky\",\"confidence\":95}"
                  }
                ]
              }
            }
          ]
        }
        """;

        var handler = new StaticResponseHandler(HttpStatusCode.OK, successfulEnvelope);
        using var http = new HttpClient(handler);
        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test",
            http,
            new GeminiQuotaCircuitBreaker(),
            () => new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero),
            requestEveryChecks: 3);
        Assert(await service.InitializeAsync(), "Paced Gemini service failed to initialize");

        using var frame = CreateFrame();
        var first = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var second = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var third = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var fourth = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var fifth = await service.TryAnalyzeOnlineOnlyAsync(frame);

        Assert(first.Success && fourth.Success,
            "Gemini pacing did not run online on checks 1 and 4 for N=3");
        Assert(!second.Success && !third.Success && !fifth.Success,
            "Gemini pacing unexpectedly called online between scheduled checks");
        Assert(second.Provenance.FailureCategory == AnalysisFailureCategory.ScheduledLocal
               && second.Provenance.RequestSuppressed
               && second.Provenance.RequestEveryChecks == 3
               && second.Provenance.RequestSequence == 2,
            "Gemini pacing metadata did not explain the scheduled local check");
        Assert(handler.RequestCount == 2,
            "Gemini pacing sent an unexpected number of HTTP requests");

        var probeHandler = new StaticResponseHandler(HttpStatusCode.OK, successfulEnvelope);
        using var probeHttp = new HttpClient(probeHandler);
        var probeBreaker = new GeminiQuotaCircuitBreaker();
        var probeNow = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
        var probeService = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test",
            probeHttp,
            probeBreaker,
            () => probeNow,
            requestEveryChecks: 3);
        Assert(await probeService.InitializeAsync(), "Quota-probe Gemini service failed to initialize");
        Assert((await probeService.TryAnalyzeOnlineOnlyAsync(frame)).Success,
            "Quota-probe setup call failed");

        probeBreaker.RecordFailure(probeNow, new GeminiQuotaInfo
        {
            IsQuotaFailure = true,
            ProviderFailureCode = "quota_exhausted",
            QuotaId = "GenerateRequestsPerMinutePerProjectPerModel-FreeTier",
            RetryDelay = TimeSpan.FromMinutes(1),
            IsDailyQuota = false
        });
        probeNow = probeNow.AddMinutes(2);
        var forcedProbe = await probeService.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(forcedProbe.Success && probeHandler.RequestCount == 2,
            "An expired Gemini quota pause did not force an immediate probe between paced calls");
    }

    private static async Task VerifyGeminiTemporaryFailoverReturnsToPrimaryAsync()
    {
        const string primary = "gemini-3.5-flash-lite";
        const string alternate = "gemini-3.5-flash";
        var handler = new ScriptedGeminiResponseHandler(
            new Dictionary<string, Queue<HttpStatusCode>>(StringComparer.OrdinalIgnoreCase)
            {
                [primary] = new Queue<HttpStatusCode>(new[]
                {
                    HttpStatusCode.ServiceUnavailable,
                    HttpStatusCode.OK
                }),
                [alternate] = new Queue<HttpStatusCode>(new[]
                {
                    HttpStatusCode.OK,
                    HttpStatusCode.OK
                })
            });
        using var http = new HttpClient(handler);
        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            primary,
            http,
            new GeminiQuotaCircuitBreaker(),
            () => new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            failoverCandidates: new[] { alternate });
        Assert(await service.InitializeAsync(), "Failover Gemini service failed to initialize");

        using var frame = CreateFrame();
        var first = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var second = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var third = await service.TryAnalyzeOnlineOnlyAsync(frame);

        Assert(first.Success && first.Provenance.Model == alternate,
            "HTTP 503 did not temporarily fail over to the configured same-family alternate");
        Assert(first.Provenance.AttemptDiagnostics.Count == 2
               && first.Provenance.AttemptDiagnostics[0].Model == primary
               && first.Provenance.AttemptDiagnostics[0].HttpStatus == 503
               && first.Provenance.AttemptDiagnostics[1].Model == alternate
               && first.Provenance.AttemptDiagnostics[1].HttpStatus == 200,
            "Failover provenance did not retain the primary 503 and alternate success");
        Assert(second.Success && second.Provenance.Model == alternate,
            "Temporary alternate was not held for the requested short backoff window");
        Assert(third.Success && third.Provenance.Model == primary,
            "Gemini service did not probe and return to the configured primary after two alternate successes");
        Assert(handler.RequestedModels.SequenceEqual(new[] { primary, alternate, alternate, primary }),
            "Gemini failover/return request order was not deterministic");
    }

    private static async Task VerifyGeminiQuotaPausedAlternateReturnsToPrimaryAsync()
    {
        const string primary = "gemini-3.5-flash-lite";
        const string alternate = "gemini-3.5-flash";
        var handler = new ScriptedGeminiResponseHandler(
            new Dictionary<string, Queue<HttpStatusCode>>(StringComparer.OrdinalIgnoreCase)
            {
                [primary] = new Queue<HttpStatusCode>(new[]
                {
                    HttpStatusCode.ServiceUnavailable,
                    HttpStatusCode.OK
                }),
                [alternate] = new Queue<HttpStatusCode>(new[]
                {
                    (HttpStatusCode)429
                })
            });
        using var http = new HttpClient(handler);
        var circuits = new Dictionary<string, GeminiQuotaCircuitBreaker>(StringComparer.OrdinalIgnoreCase);
        GeminiQuotaCircuitBreaker CircuitFor(string model)
        {
            if (!circuits.TryGetValue(model, out var circuit))
            {
                circuit = new GeminiQuotaCircuitBreaker();
                circuits[model] = circuit;
            }
            return circuit;
        }

        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            primary,
            http,
            CircuitFor,
            () => new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            failoverCandidates: new[] { alternate });
        Assert(await service.InitializeAsync(), "Quota-aware failover Gemini service failed to initialize");

        using var frame = CreateFrame();
        var alternateQuota = await service.TryAnalyzeOnlineOnlyAsync(frame);
        var primaryRecovery = await service.TryAnalyzeOnlineOnlyAsync(frame);

        Assert(!alternateQuota.Success
               && alternateQuota.Provenance.Model == alternate
               && alternateQuota.Provenance.FailureCategory == AnalysisFailureCategory.QuotaExhausted,
            "The alternate model's independent quota failure was not retained");
        Assert(primaryRecovery.Success && primaryRecovery.Provenance.Model == primary,
            "A quota-paused temporary alternate pinned the service instead of probing the configured primary");
        Assert(handler.RequestedModels.SequenceEqual(new[] { primary, alternate, primary }),
            "Quota-aware failover did not return to the configured primary on the next check");
    }

    private static async Task VerifyGemini503DiagnosticsSurviveRetryBudgetAsync()
    {
        var handler = new StaticResponseHandler(
            HttpStatusCode.ServiceUnavailable,
            "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}");
        using var http = new HttpClient(handler);
        var service = new GeminiAnalysisService(
            "test-key-never-sent-to-network",
            "gemini-test-primary",
            http,
            new GeminiQuotaCircuitBreaker(),
            () => new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            failoverCandidates: Array.Empty<string>());
        Assert(await service.InitializeAsync(), "503 diagnostic Gemini service failed to initialize");

        using var frame = CreateFrame();
        var failure = await service.TryAnalyzeOnlineOnlyAsync(frame);
        Assert(!failure.Success
               && failure.Provenance.FailureCategory == AnalysisFailureCategory.ServiceUnavailable
               && failure.Provenance.HttpStatus == 503
               && failure.Provenance.ProviderFailureCode == "service_unavailable",
            "Gemini final failure hid the concrete HTTP 503 behind a generic timeout/unknown result");
        Assert(failure.Provenance.AttemptDiagnostics.Count == 3
               && failure.Provenance.AttemptDiagnostics.All(item => item.HttpStatus == 503),
            "Gemini per-attempt diagnostics did not retain all HTTP 503 responses");
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

        var deletedLabelPath = reloadedSelection.LabelFilePath;
        var deletedImagePath = reloadedSelection.ImageFilePath
                               ?? throw new InvalidOperationException("review sample image path missing");
        var deletedReviewPath = reloadedSelection.ReviewFilePath
                                ?? throw new InvalidOperationException("review sidecar path missing");
        var retainedEntry = reloaded.Single(entry => entry.SampleId != reloadedSelection.SampleId);
        var deletion = await service.DeleteSampleAsync(reloadedSelection);

        Assert(deletion.SampleId == reloadedSelection.SampleId,
            "deletion result returned the wrong sample id");
        Assert(deletion.DeletedFileCount == 2 && deletion.ReleasedBytes > 0,
            "shared-image deletion did not remove the label and review sidecar");
        Assert(deletion.RetainedSharedImage,
            "shared content-addressed image was not protected");
        Assert(!File.Exists(deletedLabelPath)
               && File.Exists(deletedImagePath)
               && !File.Exists(deletedReviewPath),
            "shared-image deletion did not preserve exactly the shared image");
        Assert(File.Exists(retainedEntry.LabelFilePath)
               && retainedEntry.ImageFilePath != null
               && File.Exists(retainedEntry.ImageFilePath),
            "sample deletion damaged an unrelated dataset entry");

        var afterDeletion = await service.LoadAsync();
        Assert(afterDeletion.Count == 1
               && afterDeletion[0].SampleId == retainedEntry.SampleId,
            "flat reviewer index did not remove the deleted sample");

        var finalDeletion = await service.DeleteSampleAsync(afterDeletion[0]);
        Assert(finalDeletion.DeletedFileCount == 2 && finalDeletion.ReleasedBytes > 0,
            "last-reference deletion did not remove the label and image");
        Assert(!finalDeletion.RetainedSharedImage && !File.Exists(deletedImagePath),
            "last image reference was deleted but its image was not released");
        Assert((await service.LoadAsync()).Count == 0,
            "reviewer index was not empty after deleting the final sample");

        var deletionAudit = Directory.GetFiles(
            Path.Combine(root, "review"),
            "deletions-*.jsonl",
            SearchOption.TopDirectoryOnly);
        Assert(deletionAudit.Length == 1 && File.ReadAllLines(deletionAudit[0]).Length == 2,
            "sample deletion tombstone audits were not written");
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

    private sealed class FakeRtspFrameCaptureService : IRtspFrameCaptureService
    {
        private string _initializedUrl = string.Empty;

        public int InitializeCalls { get; private set; }

        public int CaptureCalls { get; private set; }

        public int ResetCalls { get; private set; }

        public Color NextFrameColor { get; set; } = Color.Black;

        public bool IsInitializedFor(string rtspUrl) =>
            string.Equals(_initializedUrl, rtspUrl, StringComparison.Ordinal);

        public Task<bool> InitializeAsync(
            string rtspUrl,
            CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            _initializedUrl = rtspUrl;
            return Task.FromResult(true);
        }

        public Task<Bitmap?> CaptureFrameAsync(
            CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            return Task.FromResult<Bitmap?>(CreateSolidBitmap(NextFrameColor));
        }

        public Task<bool> SaveFrameAsync(
            Bitmap frame,
            string filePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public void Reset()
        {
            ResetCalls++;
            _initializedUrl = string.Empty;
        }

        public void Dispose()
        {
            _initializedUrl = string.Empty;
        }
    }

    private sealed class QuotaTeacher : IOnlineWeatherAnalysisService
    {
        private readonly DateTime _retryAfterUtc;

        public QuotaTeacher(DateTime retryAfterUtc)
        {
            _retryAfterUtc = retryAfterUtc;
        }

        public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<WeatherAnalysisResult> AnalyzeImageAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new WeatherAnalysisResult
            {
                Condition = WeatherCondition.Unknown,
                Confidence = 0,
                IsSafeForImaging = false
            });
        }

        public Task<OnlineAnalysisAttempt> TryAnalyzeOnlineOnlyAsync(
            Bitmap image,
            AstroContext? astroContext = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(OnlineAnalysisAttempt.Failed(
                new AnalysisProvenance
                {
                    Origin = AnalysisOrigin.Gemini,
                    Provider = "Gemini",
                    Model = "gemini-test",
                    PromptVersion = WeatherAnalysisPrompts.PromptVersion,
                    FailureCategory = AnalysisFailureCategory.QuotaExhausted,
                    OnlineSucceeded = false,
                    Attempts = 0,
                    ProviderFailureCode = "quota_exhausted",
                    RetryAfterUtc = _retryAfterUtc,
                    QuotaMetric = "test-quota-metric",
                    QuotaId = "test-daily-quota",
                    ConsecutiveQuotaFailures = 2,
                    RequestSuppressed = true
                },
                "Gemini quota circuit active"));
        }
    }

    private sealed class QuotaResponseHandler : StaticResponseHandler
    {
        public QuotaResponseHandler(string responseBody)
            : base((HttpStatusCode)429, responseBody)
        {
        }
    }

    private sealed class ScriptedGeminiResponseHandler : HttpMessageHandler
    {
        private const string SuccessfulEnvelope = """
        {
          "candidates": [
            {
              "content": {
                "parts": [
                  {
                    "text": "{\"condition\":\"Clear\",\"cloudCoverage\":5,\"rainDetected\":false,\"fogDetected\":false,\"isSafe\":true,\"description\":\"Clear sky\",\"confidence\":95}"
                  }
                ]
              }
            }
          ]
        }
        """;

        private readonly Dictionary<string, Queue<HttpStatusCode>> _responses;

        public ScriptedGeminiResponseHandler(Dictionary<string, Queue<HttpStatusCode>> responses)
        {
            _responses = responses;
        }

        public List<string> RequestedModels { get; } = new List<string>();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            const string prefix = "/v1beta/models/";
            var start = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            var model = start >= 0 ? path.Substring(start + prefix.Length) : string.Empty;
            var suffix = model.IndexOf(':');
            if (suffix >= 0)
            {
                model = model.Substring(0, suffix);
            }
            model = Uri.UnescapeDataString(model);
            RequestedModels.Add(model);

            if (!_responses.TryGetValue(model, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException($"No scripted Gemini response remains for {model}");
            }

            var status = queue.Dequeue();
            var body = status == HttpStatusCode.OK
                ? SuccessfulEnvelope
                : (int)status == 429
                    ? "{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"Per-model daily quota exhausted\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"quotaMetric\":\"generativelanguage.googleapis.com/generate_content_free_tier_requests\",\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\"}]}]}}"
                    : "{\"error\":{\"code\":503,\"status\":\"UNAVAILABLE\"}}";
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body)
            });
        }
    }

    private class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public StaticResponseHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody)
            });
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
