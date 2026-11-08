using System;
using System.Threading;

namespace AIWeather.Services
{
    /// <summary>Stop invalidates queued starts. Video output readiness is not analysis readiness.</summary>
    internal sealed class PreviewStartupState
    {
        private long _version;
        public long Start() => Interlocked.Increment(ref _version);
        public void Stop() => Interlocked.Increment(ref _version);
        public bool IsCurrent(long request) => request == Interlocked.Read(ref _version);
        public static bool CanReveal(bool playing, bool hasVideoOutput, bool hasDimensions, TimeSpan elapsed) =>
            playing && (hasVideoOutput || hasDimensions) && elapsed >= TimeSpan.FromMilliseconds(250);
    }
}
