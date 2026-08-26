using Microsoft.Win32;
using NINA.Core.Utility;
using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Reuses one HttpClient while the effective Windows/.NET proxy configuration is
    /// unchanged, and creates a fresh transport after that configuration changes.
    ///
    /// HttpClient resolves the default system proxy when its underlying handler is
    /// created.  A long-running N.I.N.A. process therefore cannot reliably recover when
    /// an operator disables and later re-enables v2rayN's system proxy unless the handler
    /// is recreated.  This provider keeps connection pooling during normal operation but
    /// makes a proxy toggle visible before the next Gemini request.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class SystemProxyAwareHttpClientProvider : IHttpClientProvider
    {
        private static readonly TimeSpan RetiredClientGracePeriod = TimeSpan.FromMinutes(2);
        private const string InternetSettingsPath =
            @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        private readonly object _gate = new object();
        private readonly Func<string> _fingerprintFactory;
        private readonly Func<HttpClient> _clientFactory;

        private HttpClient? _client;
        private string? _fingerprint;
        private int _generation;

        public SystemProxyAwareHttpClientProvider()
            : this(CaptureProxyFingerprint, () => new HttpClient())
        {
        }

        internal SystemProxyAwareHttpClientProvider(
            Func<string> fingerprintFactory,
            Func<HttpClient> clientFactory)
        {
            _fingerprintFactory = fingerprintFactory
                ?? throw new ArgumentNullException(nameof(fingerprintFactory));
            _clientFactory = clientFactory
                ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public HttpClient GetClient()
        {
            var currentFingerprint = GetFingerprintSafely();
            HttpClient? retiredClient = null;
            HttpClient selectedClient;
            var proxyChanged = false;
            var generation = 0;

            lock (_gate)
            {
                if (_client != null
                    && string.Equals(
                        _fingerprint,
                        currentFingerprint,
                        StringComparison.Ordinal))
                {
                    return _client;
                }

                proxyChanged = _client != null;
                retiredClient = _client;
                selectedClient = _clientFactory();
                _client = selectedClient;
                _fingerprint = currentFingerprint;
                generation = ++_generation;
            }

            // Another caller could still be finishing a request with the old transport.
            // Delay disposal so a proxy toggle does not cancel that in-flight request;
            // proxy changes are rare, so the temporary resource cost is negligible.
            if (retiredClient != null)
            {
                RetireAfterGracePeriod(retiredClient);
            }

            if (proxyChanged)
            {
                Logger.Info(
                    $"System proxy configuration changed; recreated Gemini HTTP transport " +
                    $"(generation {generation}). N.I.N.A. restart is not required.");
            }

            return selectedClient;
        }

        internal int Generation
        {
            get
            {
                lock (_gate)
                {
                    return _generation;
                }
            }
        }

        private string GetFingerprintSafely()
        {
            try
            {
                return _fingerprintFactory() ?? string.Empty;
            }
            catch (Exception ex)
            {
                // Proxy inspection must never prevent a weather check.  A stable fallback
                // fingerprint preserves the existing client until registry access works
                // again, at which point the changed value triggers a refresh.
                Logger.Debug($"Could not inspect system proxy configuration: {ex.Message}");
                return "proxy-inspection-unavailable";
            }
        }

        private static string CaptureProxyFingerprint()
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsPath);

            static string Value(RegistryKey? registryKey, string name)
            {
                return Convert.ToString(registryKey?.GetValue(name)) ?? string.Empty;
            }

            // Include both WinINET values (used by v2rayN's "Set system proxy") and the
            // environment variables understood by .NET.  The fingerprint is compared
            // only in memory and is never logged, so proxy URLs or credentials cannot be
            // exposed in N.I.N.A.'s log.
            return string.Join(
                "\u001f",
                Value(key, "ProxyEnable"),
                Value(key, "ProxyServer"),
                Value(key, "AutoConfigURL"),
                Value(key, "ProxyOverride"),
                Environment.GetEnvironmentVariable("HTTP_PROXY") ?? string.Empty,
                Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? string.Empty,
                Environment.GetEnvironmentVariable("ALL_PROXY") ?? string.Empty,
                Environment.GetEnvironmentVariable("NO_PROXY") ?? string.Empty);
        }

        private static void RetireAfterGracePeriod(HttpClient client)
        {
            _ = Task.Delay(RetiredClientGracePeriod).ContinueWith(
                _ => client.Dispose(),
                default,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    internal interface IHttpClientProvider
    {
        HttpClient GetClient();
    }

    internal sealed class FixedHttpClientProvider : IHttpClientProvider
    {
        private readonly HttpClient _client;

        public FixedHttpClientProvider(HttpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public HttpClient GetClient() => _client;
    }
}
