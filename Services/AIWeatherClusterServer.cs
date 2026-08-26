using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Small fixed-surface HTTP/1.1 server for trusted-LAN status replication. TcpListener
    /// avoids Windows HttpListener URL ACL setup and keeps the exposed surface auditable.
    /// </summary>
    public sealed class AIWeatherClusterServer : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly int _port;
        private readonly string _token;
        private readonly Func<AIWeatherClusterSnapshot> _snapshotFactory;
        private readonly string _nodeId;
        private readonly string _sessionId = Guid.NewGuid().ToString("D");
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private TcpListener? _listener;
        private Task? _acceptLoop;
        private bool _disposed;

        public AIWeatherClusterServer(
            int port,
            string token,
            string nodeId,
            Func<AIWeatherClusterSnapshot> snapshotFactory)
        {
            if (port is < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }
            if (!AIWeatherClusterProtocol.IsTokenUsable(token))
            {
                throw new ArgumentException(
                    $"Cluster token must contain at least {AIWeatherClusterProtocol.MinimumTokenLength} characters.",
                    nameof(token));
            }

            _port = port;
            _token = token.Trim();
            _nodeId = string.IsNullOrWhiteSpace(nodeId) ? Environment.MachineName : nodeId;
            _snapshotFactory = snapshotFactory ?? throw new ArgumentNullException(nameof(snapshotFactory));
        }

        public string SessionId => _sessionId;
        public bool IsRunning => _listener != null;

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener != null)
            {
                return;
            }

            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start(backlog: 16);
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Logger.Info($"AI Weather primary-node service listening on TCP {_port}; session {_sessionId}");
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    _ = HandleClientAsync(client, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    client?.Dispose();
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    client?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    client?.Dispose();
                    Logger.Warning($"AI Weather cluster accept failed: {ex.Message}");
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                    using var stream = client.GetStream();
                    var request = await ReadHeadersAsync(stream, timeout.Token).ConfigureAwait(false);
                    if (request == null)
                    {
                        await WriteErrorAsync(stream, 413, "request_too_large", "Request headers are too large.", false, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteErrorAsync(stream, 405, "method_not_allowed", "Only GET is supported.", false, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    var suppliedToken = ExtractBearer(request.Headers);
                    if (!AIWeatherClusterProtocol.FixedTimeTokenEquals(_token, suppliedToken))
                    {
                        Logger.Warning($"AI Weather cluster rejected an unauthorized request from {remote}");
                        await WriteErrorAsync(stream, 401, "unauthorized", "Authentication failed.", false, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(request.Path, "/api/v1/health", StringComparison.Ordinal))
                    {
                        await WriteJsonAsync(stream, 200, new AIWeatherClusterHealth
                        {
                            NodeId = _nodeId,
                            SessionId = _sessionId,
                            GeneratedUtc = DateTime.UtcNow
                        }, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(request.Path, "/api/v1/status", StringComparison.Ordinal))
                    {
                        var snapshot = _snapshotFactory();
                        snapshot.SchemaVersion = AIWeatherClusterProtocol.SchemaVersion;
                        snapshot.Product = AIWeatherClusterProtocol.Product;
                        snapshot.NodeId = _nodeId;
                        snapshot.SessionId = _sessionId;
                        snapshot.GeneratedUtc = DateTime.UtcNow;
                        await WriteJsonAsync(stream, 200, snapshot, timeout.Token).ConfigureAwait(false);
                        return;
                    }

                    await WriteErrorAsync(stream, 404, "not_found", "Unknown cluster endpoint.", false, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // A slow or disconnected LAN client must not hold a server task forever.
                }
                catch (Exception ex)
                {
                    Logger.Debug($"AI Weather cluster request ended with an error: {ex.Message}");
                }
            }
        }

        private static async Task<ParsedRequest?> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
        {
            var bytes = new List<byte>(1024);
            var one = new byte[1];
            while (bytes.Count < AIWeatherClusterProtocol.MaximumRequestHeaderBytes)
            {
                var read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                bytes.Add(one[0]);
                var n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == '\r' && bytes[n - 3] == '\n'
                    && bytes[n - 2] == '\r' && bytes[n - 1] == '\n')
                {
                    var text = Encoding.ASCII.GetString(bytes.ToArray());
                    var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    var requestLine = lines[0].Split(' ');
                    if (requestLine.Length < 2)
                    {
                        return new ParsedRequest("", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                    }

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var i = 1; i < lines.Length; i++)
                    {
                        var separator = lines[i].IndexOf(':');
                        if (separator > 0)
                        {
                            headers[lines[i][..separator].Trim()] = lines[i][(separator + 1)..].Trim();
                        }
                    }
                    return new ParsedRequest(requestLine[0], requestLine[1].Split('?')[0], headers);
                }
            }
            return null;
        }

        private static string? ExtractBearer(IReadOnlyDictionary<string, string> headers)
        {
            if (!headers.TryGetValue("Authorization", out var authorization)
                || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return authorization[7..].Trim();
        }

        private static Task WriteErrorAsync(
            NetworkStream stream,
            int status,
            string code,
            string message,
            bool retryable,
            CancellationToken token) =>
            WriteJsonAsync(stream, status, new ClusterErrorResponse
            {
                Code = code,
                Message = message,
                Retryable = retryable
            }, token);

        private static async Task WriteJsonAsync<T>(
            NetworkStream stream,
            int status,
            T value,
            CancellationToken token)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            var reason = status switch
            {
                200 => "OK",
                401 => "Unauthorized",
                404 => "Not Found",
                405 => "Method Not Allowed",
                413 => "Content Too Large",
                _ => "Error"
            };
            var header = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {reason}\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(body, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _cts.Cancel();
            _listener?.Stop();
            _listener = null;
            _cts.Dispose();
        }

        private sealed record ParsedRequest(
            string Method,
            string Path,
            IReadOnlyDictionary<string, string> Headers);
    }
}
