using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using NINA.Core.Utility;

namespace AIWeather.Services
{
    internal static class SkySentinelSettingsMigration
    {
        public const string CurrentGroup = "SkySentinel.Properties.Settings";
        internal const string MigrationFlag = "SkySentinelMigrationCompleted";
        internal static readonly string[] LegacyGroups = { "AIWeather.Properties.Settings", "AllSkyCameraPlugin.Properties.Settings" };

        public static bool Import(ApplicationSettingsBase target, string userConfigPath, string? legacyDatasetPath = null)
        {
            if ((bool)target[MigrationFlag]) return false;
            XElement? settings = null;
            if (File.Exists(userConfigPath))
            {
                using var reader = XmlReader.Create(userConfigPath, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
                settings = XDocument.Load(reader).Root?.Element("userSettings");
            }
            // Existing independent settings always win, including an intentionally empty key.
            if (settings?.Element(CurrentGroup) != null)
            {
                target[MigrationFlag] = true;
                return true;
            }
            var legacy = LegacyGroups.Select(name => settings?.Element(name)).FirstOrDefault(group => group != null);
            var imports = new Dictionary<string, object?>();
            if (legacy != null)
            {
                foreach (var entry in legacy.Elements("setting"))
                {
                    var name = (string?)entry.Attribute("name");
                    if (name == null || name == MigrationFlag || name == "UpdateSettings") continue;
                    var property = target.Properties[name];
                    var value = entry.Element("value");
                    if (property == null || property.IsReadOnly || value == null) continue;
                    try
                    {
                        // All current settings are strings/primitives. SettingsPropertyValue
                        // silently substitutes defaults on malformed numbers even in strict mode.
                        // Convert explicitly so damaged thresholds cannot become new defaults.
                        if (property.SerializeAs != SettingsSerializeAs.String)
                            throw new NotSupportedException();
                        var parsed = property.PropertyType == typeof(string) ? value.Value :
                            TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(value.Value);
                        if (parsed is double number && !double.IsFinite(number)) throw new FormatException();
                        imports[name] = parsed;
                    }
                    catch
                    {
                        // Do not include the original value or conversion exception (possible secrets).
                        throw new ConfigurationErrorsException($"Sky Sentinel could not import legacy setting '{name}' ({property.PropertyType.Name}); the legacy configuration was preserved");
                    }
                }
                // Reuse the existing dataset in place, without copying/renaming GB of samples.
                legacyDatasetPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NINA", "AIWeather", "dataset", "v1");
                if ((!imports.TryGetValue("DatasetDirectory", out var root) || string.IsNullOrWhiteSpace(root as string))
                    && Directory.Exists(legacyDatasetPath) && target.Properties["DatasetDirectory"] != null)
                    imports["DatasetDirectory"] = legacyDatasetPath;
                if (imports.TryGetValue("WriteSafetyStatusFile", out var write) && write is true &&
                    (!imports.TryGetValue("SafetyStatusFilePath", out var statusPath) || string.IsNullOrWhiteSpace(statusPath as string)) &&
                    target.Properties["SafetyStatusFilePath"] != null)
                    imports["SafetyStatusFilePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                        "N.I.N.A", "sky_conditions.txt"); // preserve an explicitly enabled legacy integration
                // A rollback copy may contain secrets; it stays beside the local config, never in packages/logs.
                File.Copy(userConfigPath, userConfigPath + ".before-skysentinel-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak", false);
            }
            foreach (var entry in imports) target[entry.Key] = entry.Value;
            target[MigrationFlag] = true;
            Logger.Info($"Sky Sentinel settings migration prepared: {imports.Count} values; legacy group preserved; no values logged");
            return true;
        }
    }
}
