using System;

namespace AIWeather.Services
{
    /// <summary>
    /// Aggregates LibVLC's repeated late-frame diagnostics and decides when the
    /// preview player should be rebuilt. This monitor is deliberately limited to
    /// the UI preview path; weather-analysis capture has its own freshness checks.
    /// </summary>
    public sealed class RtspPreviewHealthMonitor
    {
        public static readonly TimeSpan DefaultBurstResetGap = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan DefaultMinimumLateDuration = TimeSpan.FromSeconds(15);
        public static readonly TimeSpan DefaultSummaryInterval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan DefaultRecoveryCooldown = TimeSpan.FromMinutes(1);
        public const int DefaultMinimumLateMessages = 30;

        private readonly object _gate = new object();
        private readonly TimeSpan _burstResetGap;
        private readonly TimeSpan _minimumLateDuration;
        private readonly TimeSpan _summaryInterval;
        private readonly TimeSpan _recoveryCooldown;
        private readonly int _minimumLateMessages;

        private DateTime _burstStartedUtc;
        private DateTime _lastLateMessageUtc;
        private DateTime _lastSummaryUtc;
        private DateTime _lastRecoveryUtc;
        private int _lateMessageCount;

        public RtspPreviewHealthMonitor(
            TimeSpan? burstResetGap = null,
            TimeSpan? minimumLateDuration = null,
            int minimumLateMessages = DefaultMinimumLateMessages,
            TimeSpan? summaryInterval = null,
            TimeSpan? recoveryCooldown = null)
        {
            _burstResetGap = RequirePositive(burstResetGap ?? DefaultBurstResetGap, nameof(burstResetGap));
            _minimumLateDuration = RequireNonNegative(minimumLateDuration ?? DefaultMinimumLateDuration, nameof(minimumLateDuration));
            _summaryInterval = RequirePositive(summaryInterval ?? DefaultSummaryInterval, nameof(summaryInterval));
            _recoveryCooldown = RequireNonNegative(recoveryCooldown ?? DefaultRecoveryCooldown, nameof(recoveryCooldown));
            if (minimumLateMessages <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumLateMessages));
            }

            _minimumLateMessages = minimumLateMessages;
        }

        public static bool IsLateFrameMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            return message.Contains("late frames", StringComparison.OrdinalIgnoreCase)
                || message.Contains("late video", StringComparison.OrdinalIgnoreCase)
                || message.Contains("picture is too late", StringComparison.OrdinalIgnoreCase)
                || message.Contains("buffer too late", StringComparison.OrdinalIgnoreCase);
        }

        public RtspPreviewHealthObservation Observe(string message, DateTime observedUtc)
        {
            if (!IsLateFrameMessage(message))
            {
                return default;
            }

            observedUtc = observedUtc.Kind == DateTimeKind.Utc
                ? observedUtc
                : observedUtc.ToUniversalTime();

            lock (_gate)
            {
                if (_lastLateMessageUtc == default
                    || observedUtc < _lastLateMessageUtc
                    || observedUtc - _lastLateMessageUtc > _burstResetGap)
                {
                    _burstStartedUtc = observedUtc;
                    _lateMessageCount = 0;
                }

                _lastLateMessageUtc = observedUtc;
                _lateMessageCount++;
                var lateDuration = observedUtc - _burstStartedUtc;

                var shouldLogSummary = _lastSummaryUtc == default
                    || observedUtc < _lastSummaryUtc
                    || observedUtc - _lastSummaryUtc >= _summaryInterval;
                if (shouldLogSummary)
                {
                    _lastSummaryUtc = observedUtc;
                }

                var recoveryCooldownElapsed = _lastRecoveryUtc == default
                    || observedUtc < _lastRecoveryUtc
                    || observedUtc - _lastRecoveryUtc >= _recoveryCooldown;
                var shouldRecover = recoveryCooldownElapsed
                    && _lateMessageCount >= _minimumLateMessages
                    && lateDuration >= _minimumLateDuration;
                if (shouldRecover)
                {
                    _lastRecoveryUtc = observedUtc;
                }

                return new RtspPreviewHealthObservation(
                    IsLateFrameMessage: true,
                    LateMessageCount: _lateMessageCount,
                    LateDuration: lateDuration,
                    ShouldLogSummary: shouldLogSummary,
                    ShouldRecover: shouldRecover);
            }
        }

        /// <summary>
        /// Clears only the current late-frame burst. The recovery timestamp is
        /// retained so a broken source cannot create a tight restart loop.
        /// </summary>
        public void ResetBurst()
        {
            lock (_gate)
            {
                _burstStartedUtc = default;
                _lastLateMessageUtc = default;
                _lastSummaryUtc = default;
                _lateMessageCount = 0;
            }
        }

        public void ResetAll()
        {
            lock (_gate)
            {
                _burstStartedUtc = default;
                _lastLateMessageUtc = default;
                _lastSummaryUtc = default;
                _lastRecoveryUtc = default;
                _lateMessageCount = 0;
            }
        }

        private static TimeSpan RequirePositive(TimeSpan value, string parameterName)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return value;
        }

        private static TimeSpan RequireNonNegative(TimeSpan value, string parameterName)
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return value;
        }
    }

    public readonly record struct RtspPreviewHealthObservation(
        bool IsLateFrameMessage,
        int LateMessageCount,
        TimeSpan LateDuration,
        bool ShouldLogSummary,
        bool ShouldRecover);
}
