using System;
using System.Threading;
using System.Threading.Tasks;

namespace AIWeather.Services
{
    /// <summary>
    /// Keeps probing an already-playing LibVLC instance when its native video surface
    /// is slower than the initial connection handshake. The probe supplied by the view
    /// must reuse that same player; this coordinator never opens another RTSP session.
    /// </summary>
    public sealed class RtspPreviewSurfaceRecovery
    {
        public static readonly TimeSpan DefaultProbeInterval = TimeSpan.FromSeconds(2);
        // Each same-player snapshot probe can itself take up to about 1.5 seconds.
        // Fifteen probes plus the two-second gaps keep the background recovery
        // bounded to roughly one minute in the worst case.
        public const int DefaultMaximumProbes = 15;

        private readonly TimeSpan _probeInterval;
        private readonly int _maximumProbes;

        public RtspPreviewSurfaceRecovery(
            TimeSpan? probeInterval = null,
            int maximumProbes = DefaultMaximumProbes)
        {
            _probeInterval = probeInterval ?? DefaultProbeInterval;
            if (_probeInterval < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(probeInterval));
            }

            if (maximumProbes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumProbes));
            }

            _maximumProbes = maximumProbes;
        }

        public async Task<bool> WaitForSurfaceAsync(
            Func<CancellationToken, Task<bool>> probeAsync,
            CancellationToken cancellationToken = default)
        {
            if (probeAsync == null)
            {
                throw new ArgumentNullException(nameof(probeAsync));
            }

            for (var attempt = 0; attempt < _maximumProbes; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await probeAsync(cancellationToken).ConfigureAwait(true))
                {
                    return true;
                }

                if (attempt + 1 < _maximumProbes && _probeInterval > TimeSpan.Zero)
                {
                    await Task.Delay(_probeInterval, cancellationToken).ConfigureAwait(true);
                }
            }

            return false;
        }
    }
}
