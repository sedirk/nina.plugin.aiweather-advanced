using System;
using System.IO;

namespace AIWeather
{
    public static class PluginInfo
    {
        public const string PLUGIN_NAME = "Sky Sentinel";
        public const string PLUGIN_VERSION = "1.36.0.1";
        public const string PLUGIN_AUTHOR = "sedirk";
        public const string PLUGIN_DESCRIPTION = "AI-powered all-sky camera weather monitoring with automatic safety protection";
        
        public const string PLUGIN_GUID = "83bb4fd5-b304-4655-8fb5-cf01195ad34f";
        public static readonly Guid PLUGIN_ID = new Guid(PLUGIN_GUID);
        
        public static string GetPluginDataPath()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA",
                "SkySentinel");
            
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            
            return path;
        }
        
        public static string GetCaptureImagePath()
        {
            var path = Path.Combine(GetPluginDataPath(), "Captures");
            
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            
            return path;
        }
    }
}
