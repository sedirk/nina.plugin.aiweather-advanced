using System;

namespace AIWeather.Services
{
    /// <summary>
    /// Pure, side-effect-free decision used before camera capture and model initialization.
    /// When the guard is enabled, missing astronomical context fails closed: an unknown Sun
    /// altitude must never turn into permission to image or spend an online API request.
    /// </summary>
    public readonly record struct SolarAltitudeGateDecision(
        bool ShouldSuspend,
        bool HasAstronomicalContext,
        double? SunAltitude,
        double LimitDegrees);

    public static class SolarAltitudeGuard
    {
        public const double DefaultLimitDegrees = -6.0;

        public static SolarAltitudeGateDecision Evaluate(
            bool enabled,
            double configuredLimitDegrees,
            AstroContext? context)
        {
            var limit = NormalizeLimit(configuredLimitDegrees);
            if (!enabled)
            {
                return new SolarAltitudeGateDecision(
                    ShouldSuspend: false,
                    HasAstronomicalContext: context != null,
                    SunAltitude: context?.SunAltitude,
                    LimitDegrees: limit);
            }

            if (context == null || !double.IsFinite(context.SunAltitude))
            {
                return new SolarAltitudeGateDecision(
                    ShouldSuspend: true,
                    HasAstronomicalContext: false,
                    SunAltitude: null,
                    LimitDegrees: limit);
            }

            return new SolarAltitudeGateDecision(
                ShouldSuspend: context.SunAltitude >= limit,
                HasAstronomicalContext: true,
                SunAltitude: context.SunAltitude,
                LimitDegrees: limit);
        }

        public static double NormalizeLimit(double value)
        {
            if (!double.IsFinite(value))
            {
                return DefaultLimitDegrees;
            }

            return Math.Clamp(value, -90.0, 90.0);
        }
    }
}
