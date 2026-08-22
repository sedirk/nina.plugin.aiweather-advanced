using System;
using System.Drawing;

namespace AIWeather.Services
{
    /// <summary>
    /// Thread-safe, latest-frame-only ownership boundary for the RTSP decoder.
    /// Publishing transfers ownership of <paramref name="frame"/> to this buffer;
    /// consumers only ever receive independent clones.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal sealed class LatestRtspFrameBuffer : IDisposable
    {
        private readonly object _sync = new object();
        private Bitmap? _latest;
        private DateTime _receivedUtc;
        private long _sequence;
        private bool _isDisposed;

        public long Sequence
        {
            get
            {
                lock (_sync)
                {
                    return _sequence;
                }
            }
        }

        public void Publish(Bitmap frame, DateTime receivedUtc)
        {
            ArgumentNullException.ThrowIfNull(frame);

            Bitmap? previous;
            lock (_sync)
            {
                if (_isDisposed)
                {
                    frame.Dispose();
                    return;
                }

                previous = _latest;
                _latest = frame;
                _receivedUtc = receivedUtc.Kind == DateTimeKind.Utc
                    ? receivedUtc
                    : receivedUtc.ToUniversalTime();
                _sequence++;
            }

            previous?.Dispose();
        }

        public bool TryCloneNewerThan(
            long minimumSequenceExclusive,
            DateTime nowUtc,
            TimeSpan maximumAge,
            out Bitmap? frame,
            out long sequence,
            out DateTime receivedUtc,
            out TimeSpan age)
        {
            lock (_sync)
            {
                var normalizedNow = nowUtc.Kind == DateTimeKind.Utc
                    ? nowUtc
                    : nowUtc.ToUniversalTime();
                var candidateAge = normalizedNow - _receivedUtc;

                if (_isDisposed
                    || _latest == null
                    || _sequence <= minimumSequenceExclusive
                    || candidateAge < TimeSpan.Zero
                    || candidateAge > maximumAge)
                {
                    frame = null;
                    sequence = _sequence;
                    receivedUtc = _receivedUtc;
                    age = candidateAge;
                    return false;
                }

                frame = new Bitmap(_latest);
                sequence = _sequence;
                receivedUtc = _receivedUtc;
                age = candidateAge;
                return true;
            }
        }

        public void Clear()
        {
            Bitmap? previous;
            lock (_sync)
            {
                previous = _latest;
                _latest = null;
                _receivedUtc = default;
            }

            previous?.Dispose();
        }

        public void Dispose()
        {
            Bitmap? previous;
            lock (_sync)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                previous = _latest;
                _latest = null;
                _receivedUtc = default;
            }

            previous?.Dispose();
        }
    }
}
