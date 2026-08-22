using System;

namespace AIWeather.Services
{
    internal readonly record struct VideoFitSize(double Width, double Height);

    internal static class VideoFitCalculator
    {
        public static VideoFitSize FitInside(
            double containerWidth,
            double containerHeight,
            double contentWidth,
            double contentHeight)
        {
            if (!IsUsable(containerWidth)
                || !IsUsable(containerHeight)
                || !IsUsable(contentWidth)
                || !IsUsable(contentHeight))
            {
                return new VideoFitSize(1, 1);
            }

            var scale = Math.Min(
                containerWidth / contentWidth,
                containerHeight / contentHeight);

            if (!IsUsable(scale))
            {
                return new VideoFitSize(1, 1);
            }

            return new VideoFitSize(
                Math.Max(1, contentWidth * scale),
                Math.Max(1, contentHeight * scale));
        }

        private static bool IsUsable(double value)
        {
            return !double.IsNaN(value)
                && !double.IsInfinity(value)
                && value > 0;
        }
    }
}
