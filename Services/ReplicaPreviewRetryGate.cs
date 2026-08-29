using System;

namespace AIWeather.Services
{
    /// <summary>
    /// Prevents repeated cluster-status notifications from turning one failed RTSP
    /// connection into a tight reconnect loop. Callers serialize access to this gate.
    /// </summary>
    internal sealed class ReplicaPreviewRetryGate
    {
        private string _failedSourceIdentity = string.Empty;
        private DateTime _retryAfterUtc = DateTime.MinValue;

        internal DateTime RetryAfterUtc => _retryAfterUtc;

        internal bool ShouldAttempt(string sourceIdentity, DateTime utcNow, bool forceRestart)
        {
            if (forceRestart)
            {
                Clear();
                return true;
            }

            return !string.Equals(
                       sourceIdentity,
                       _failedSourceIdentity,
                       StringComparison.Ordinal)
                   || utcNow >= _retryAfterUtc;
        }

        internal void RecordFailure(string sourceIdentity, DateTime utcNow, TimeSpan retryDelay)
        {
            _failedSourceIdentity = sourceIdentity ?? string.Empty;
            _retryAfterUtc = utcNow.Add(retryDelay);
        }

        internal void Clear()
        {
            _failedSourceIdentity = string.Empty;
            _retryAfterUtc = DateTime.MinValue;
        }
    }
}
