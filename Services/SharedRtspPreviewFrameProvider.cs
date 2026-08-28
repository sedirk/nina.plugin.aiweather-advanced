using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    internal enum SharedRtspPreviewCaptureStatus
    {
        Unavailable,
        Captured,
        Failed
    }

    internal readonly struct SharedRtspPreviewCaptureResult
    {
        private SharedRtspPreviewCaptureResult(
            SharedRtspPreviewCaptureStatus status,
            Bitmap? frame,
            string reason)
        {
            Status = status;
            Frame = frame;
            Reason = reason;
        }

        public SharedRtspPreviewCaptureStatus Status { get; }

        public Bitmap? Frame { get; }

        public string Reason { get; }

        public static SharedRtspPreviewCaptureResult Unavailable() =>
            new SharedRtspPreviewCaptureResult(
                SharedRtspPreviewCaptureStatus.Unavailable,
                null,
                "no matching active preview");

        public static SharedRtspPreviewCaptureResult Captured(Bitmap frame) =>
            new SharedRtspPreviewCaptureResult(
                SharedRtspPreviewCaptureStatus.Captured,
                frame,
                string.Empty);

        public static SharedRtspPreviewCaptureResult Failed(string reason) =>
            new SharedRtspPreviewCaptureResult(
                SharedRtspPreviewCaptureStatus.Failed,
                null,
                reason);
    }

    /// <summary>
    /// Process-wide bridge between the LibVLC preview and the safety monitor. An active
    /// preview registers an on-demand snapshot callback before it opens the stream. The
    /// analysis path then consumes that same decoded video instead of creating another
    /// OpenCV/FFmpeg RTSP session.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal sealed class SharedRtspPreviewFrameProvider
    {
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _captureGate = new SemaphoreSlim(1, 1);
        private string _sourceIdentity = string.Empty;
        private Func<CancellationToken, Task<Bitmap?>>? _captureAsync;
        private long _generation;

        public static SharedRtspPreviewFrameProvider Instance { get; } =
            new SharedRtspPreviewFrameProvider();

        /// <summary>
        /// Subscribers release any independent decoder for this endpoint before the
        /// preview opens it. Registration is already visible while subscribers run, so a
        /// concurrent weather check cannot race in and create another decoder.
        /// </summary>
        public event Func<string, Task>? SourceReserved;

        public async Task<IDisposable> RegisterAsync(
            string rtspUrl,
            Func<CancellationToken, Task<Bitmap?>> captureAsync,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(rtspUrl);
            ArgumentNullException.ThrowIfNull(captureAsync);

            var identity = CreateSourceIdentity(rtspUrl);
            long generation;

            lock (_sync)
            {
                generation = ++_generation;
                _sourceIdentity = identity;
                _captureAsync = captureAsync;
            }

            try
            {
                var handlers = SourceReserved?
                    .GetInvocationList()
                    .Cast<Func<string, Task>>()
                    .ToArray() ?? Array.Empty<Func<string, Task>>();

                foreach (var handler in handlers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await handler(identity);
                    }
                    catch (Exception ex)
                    {
                        // A stale decoder is an optimization concern, not a reason to tear
                        // down a preview that may be the only compatible camera client.
                        Logger.Warning(
                            $"Shared RTSP preview reservation listener failed: {ex.GetType().Name}");
                    }
                }

                return new Registration(this, generation);
            }
            catch
            {
                Release(generation);
                throw;
            }
        }

        public bool HasActiveSource(string rtspUrl)
        {
            var identity = CreateSourceIdentity(rtspUrl);
            lock (_sync)
            {
                return _captureAsync != null
                    && string.Equals(_sourceIdentity, identity, StringComparison.Ordinal);
            }
        }

        public async Task<SharedRtspPreviewCaptureResult> TryCaptureAsync(
            string rtspUrl,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var identity = CreateSourceIdentity(rtspUrl);
            await _captureGate.WaitAsync(cancellationToken);
            try
            {
                Func<CancellationToken, Task<Bitmap?>>? capture;
                long generation;
                lock (_sync)
                {
                    if (_captureAsync == null
                        || !string.Equals(_sourceIdentity, identity, StringComparison.Ordinal))
                    {
                        return SharedRtspPreviewCaptureResult.Unavailable();
                    }

                    capture = _captureAsync;
                    generation = _generation;
                }

                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                bounded.CancelAfter(timeout);

                Bitmap? frame;
                try
                {
                    frame = await capture(bounded.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return SharedRtspPreviewCaptureResult.Failed("preview snapshot timed out");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.Warning(
                        $"Shared RTSP preview snapshot failed: {ex.GetType().Name}");
                    return SharedRtspPreviewCaptureResult.Failed("preview snapshot raised an error");
                }

                lock (_sync)
                {
                    if (_captureAsync == null
                        || _generation != generation
                        || !string.Equals(_sourceIdentity, identity, StringComparison.Ordinal))
                    {
                        frame?.Dispose();
                        return SharedRtspPreviewCaptureResult.Unavailable();
                    }
                }

                return frame == null
                    ? SharedRtspPreviewCaptureResult.Failed("preview returned no current frame")
                    : SharedRtspPreviewCaptureResult.Captured(frame);
            }
            finally
            {
                _captureGate.Release();
            }
        }

        internal static string CreateSourceIdentity(string rtspUrl)
        {
            if (Uri.TryCreate(rtspUrl?.Trim(), UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme.ToLowerInvariant();
                var host = uri.IdnHost.ToLowerInvariant();
                var port = uri.IsDefaultPort ? DefaultPort(scheme) : uri.Port;
                var pathAndQuery = uri.GetComponents(
                    UriComponents.PathAndQuery,
                    UriFormat.UriEscaped);
                return $"{scheme}://{host}:{port}/{pathAndQuery.TrimStart('/')}";
            }

            // Invalid URLs will fail later in the owning decoder. Keep a stable identity
            // without ever retaining a userinfo prefix in this process-wide coordinator.
            var value = (rtspUrl ?? string.Empty).Trim();
            var schemeSeparator = value.IndexOf("://", StringComparison.Ordinal);
            var authorityStart = schemeSeparator >= 0 ? schemeSeparator + 3 : 0;
            var pathStart = value.IndexOf('/', authorityStart);
            var authorityEnd = pathStart >= 0 ? pathStart : value.Length;
            var authority = value.Substring(authorityStart, Math.Max(0, authorityEnd - authorityStart));
            var relativeAt = authority.LastIndexOf('@');
            if (relativeAt >= 0)
            {
                var at = authorityStart + relativeAt;
                value = value.Substring(0, authorityStart) + value.Substring(at + 1);
            }

            return value;
        }

        internal static string Fingerprint(string sourceIdentity)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity));
            return Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant();
        }

        private static int DefaultPort(string scheme) =>
            string.Equals(scheme, "rtsp", StringComparison.OrdinalIgnoreCase) ? 554 : -1;

        private void Release(long generation)
        {
            lock (_sync)
            {
                if (_generation != generation)
                {
                    return;
                }

                _captureAsync = null;
                _sourceIdentity = string.Empty;
                _generation++;
            }
        }

        private sealed class Registration : IDisposable
        {
            private SharedRtspPreviewFrameProvider? _owner;
            private readonly long _generation;

            public Registration(SharedRtspPreviewFrameProvider owner, long generation)
            {
                _owner = owner;
                _generation = generation;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Release(_generation);
            }
        }
    }
}
