using System.Configuration;
using AIWeather.Services;

namespace AIWeather.Properties
{
    // Keep source namespaces for compatibility, but never share persisted settings
    // with the upstream plugin when both independent assemblies are installed.
    [SettingsGroupName(SkySentinelSettingsMigration.CurrentGroup)]
    internal sealed partial class Settings
    {
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool SkySentinelMigrationCompleted
        {
            get => (bool)this[nameof(SkySentinelMigrationCompleted)];
            set => this[nameof(SkySentinelMigrationCompleted)] = value;
        }
    }
}
