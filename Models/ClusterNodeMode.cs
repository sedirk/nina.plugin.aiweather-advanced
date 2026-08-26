using System;

namespace AIWeather.Models
{
    /// <summary>
    /// One plugin package supports all three roles. Persist the enum names rather than
    /// localized display strings so profiles remain portable between N.I.N.A. languages.
    /// </summary>
    public enum ClusterNodeMode
    {
        Standalone = 0,
        Primary = 1,
        Replica = 2
    }

    public static class ClusterNodeModeParser
    {
        public static ClusterNodeMode Parse(string? value)
        {
            return Enum.TryParse(value, ignoreCase: true, out ClusterNodeMode parsed)
                && Enum.IsDefined(parsed)
                    ? parsed
                    : ClusterNodeMode.Standalone;
        }
    }
}
