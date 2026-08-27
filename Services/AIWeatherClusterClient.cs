using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    public sealed class AIWeatherClusterClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _client;
        private readonly string _token;
        private readonly string _nodeId;
        private string? _sessionId;
        private long _lastSequence = -1;

        public AIWeatherClusterClient(
            string primaryUrl,
            string token,
            TimeSpan timeout,
            string? nodeId = null)
        {
            if (!Uri.TryCreate(primaryUrl, UriKind.Absolute, out var baseUri)
                || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Primary URL must be an absolute HTTP or HTTPS URL.", nameof(primaryUrl));
            }
            if (!AIWeatherClusterProtocol.IsTokenUsable(token))
            {
                throw new ArgumentException(
                    $"Cluster token must contain at least {AIWeatherClusterProtocol.MinimumTokenLength} characters.",
                    nameof(token));
            }

            // LAN replication must not depend on a user-space internet proxy such as v2rayN.
            // A proxy being restarted should affect Gemini, not the safety heartbeat.
            var handler = new HttpClientHandler { UseProxy = false };
            _token = token.Trim();
            _nodeId = string.IsNullOrWhiteSpace(nodeId) ? Environment.MachineName : nodeId.Trim();
            _client = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/"),
                Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout
            };
        }

        public async Task<AIWeatherClusterSnapshot> PollAsync(CancellationToken cancellationToken)
        {
            using var request = CreateGetRequest("/api/v1/status");
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AIWeatherClusterException(AIWeatherReplicaFailure.Authentication, "Primary node rejected the shared token.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new AIWeatherClusterException(
                    AIWeatherReplicaFailure.Network,
                    $"Primary node returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var snapshot = await JsonSerializer.DeserializeAsync<AIWeatherClusterSnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                           ?? throw new AIWeatherClusterException(AIWeatherReplicaFailure.Protocol, "Primary node returned an empty status document.");

            Validate(snapshot);
            return snapshot;
        }

        public async Task<AIWeatherFailoverConfigurationEnvelope> FetchFailoverConfigurationAsync(
            AIWeatherClusterSnapshot expectedPrimary,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(expectedPrimary);
            using var request = CreateGetRequest("/api/v1/failover-config");
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AIWeatherClusterException(AIWeatherReplicaFailure.Authentication, "Primary node rejected the signed configuration request.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new AIWeatherClusterException(
                    AIWeatherReplicaFailure.Network,
                    $"Primary configuration endpoint returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var envelope = await JsonSerializer.DeserializeAsync<AIWeatherFailoverConfigurationEnvelope>(
                               stream,
                               JsonOptions,
                               cancellationToken).ConfigureAwait(false)
                           ?? throw new AIWeatherClusterException(
                               AIWeatherReplicaFailure.Protocol,
                               "Primary node returned an empty failover configuration envelope.");
            if (envelope.SchemaVersion != AIWeatherClusterProtocol.SchemaVersion
                || !string.Equals(envelope.Product, AIWeatherClusterProtocol.Product, StringComparison.Ordinal)
                || !string.Equals(envelope.PrimaryNodeId, expectedPrimary.NodeId, StringComparison.Ordinal)
                || !string.Equals(envelope.PrimarySessionId, expectedPrimary.SessionId, StringComparison.Ordinal)
                || !string.Equals(envelope.Revision, expectedPrimary.FailoverConfigurationRevision, StringComparison.Ordinal))
            {
                throw new AIWeatherClusterException(
                    AIWeatherReplicaFailure.Protocol,
                    "Primary failover configuration identity does not match the current status session.");
            }
            return envelope;
        }

        public AIWeatherFailoverConfiguration DecryptFailoverConfiguration(
            AIWeatherFailoverConfigurationEnvelope envelope) =>
            AIWeatherClusterProtocol.DecryptFailoverConfiguration(envelope, _token);

        private HttpRequestMessage CreateGetRequest(string path)
        {
            var normalizedPath = path.StartsWith('/') ? path : "/" + path;
            var authentication = AIWeatherClusterProtocol.CreateRequestAuthentication(
                _token,
                HttpMethod.Get.Method,
                normalizedPath,
                _nodeId);
            var request = new HttpRequestMessage(HttpMethod.Get, normalizedPath.TrimStart('/'));
            request.Headers.TryAddWithoutValidation(
                AIWeatherClusterProtocol.AuthenticationVersionHeader,
                authentication.Version);
            request.Headers.TryAddWithoutValidation(
                AIWeatherClusterProtocol.AuthenticationNodeHeader,
                authentication.NodeId);
            request.Headers.TryAddWithoutValidation(
                AIWeatherClusterProtocol.AuthenticationTimestampHeader,
                authentication.UnixTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(
                AIWeatherClusterProtocol.AuthenticationNonceHeader,
                authentication.Nonce);
            request.Headers.TryAddWithoutValidation(
                AIWeatherClusterProtocol.AuthenticationSignatureHeader,
                authentication.Signature);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }

        private void Validate(AIWeatherClusterSnapshot snapshot)
        {
            if (snapshot.SchemaVersion != AIWeatherClusterProtocol.SchemaVersion
                || !string.Equals(snapshot.Product, AIWeatherClusterProtocol.Product, StringComparison.Ordinal))
            {
                throw new AIWeatherClusterException(
                    AIWeatherReplicaFailure.Protocol,
                    $"Incompatible primary protocol ({snapshot.Product} schema {snapshot.SchemaVersion}).");
            }
            if (string.IsNullOrWhiteSpace(snapshot.SessionId)
                || string.IsNullOrWhiteSpace(snapshot.NodeId)
                || snapshot.Sequence < 0)
            {
                throw new AIWeatherClusterException(AIWeatherReplicaFailure.Protocol, "Primary status identity is incomplete.");
            }
            if (snapshot.GeneratedUtc == default || snapshot.GeneratedUtc.Kind != DateTimeKind.Utc)
            {
                throw new AIWeatherClusterException(AIWeatherReplicaFailure.Protocol, "Primary status timestamp is not UTC.");
            }

            if (!string.Equals(_sessionId, snapshot.SessionId, StringComparison.Ordinal))
            {
                _sessionId = snapshot.SessionId;
                _lastSequence = -1;
            }
            if (snapshot.Sequence < _lastSequence)
            {
                throw new AIWeatherClusterException(AIWeatherReplicaFailure.Protocol, "Primary status sequence moved backwards.");
            }
            _lastSequence = snapshot.Sequence;
        }

        public void Dispose() => _client.Dispose();
    }

    public sealed class AIWeatherClusterException : Exception
    {
        public AIWeatherClusterException(AIWeatherReplicaFailure failure, string message, Exception? inner = null)
            : base(message, inner)
        {
            Failure = failure;
        }

        public AIWeatherReplicaFailure Failure { get; }
    }
}
