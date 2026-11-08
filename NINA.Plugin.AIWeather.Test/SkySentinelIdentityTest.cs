using System.Configuration;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using AIWeather;
using AIWeather.Services;
using FluentAssertions;
using NUnit.Framework;

namespace NINA.Plugin.AIWeather.Test;

[TestFixture]
public class SkySentinelIdentityTest
{
    private string _root = null!;
    private string ConfigPath => Path.Combine(_root, "user.config");
    [SetUp] public void Setup() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SkySentinelMigrationTest", Guid.NewGuid().ToString("N"))).FullName;
    [TearDown] public void Cleanup() => Directory.Delete(_root, recursive: true);

    [SettingsGroupName(SkySentinelSettingsMigration.CurrentGroup)]
    private sealed class TestSettings : ApplicationSettingsBase
    {
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool SkySentinelMigrationCompleted { get => (bool)this[nameof(SkySentinelMigrationCompleted)]; set => this[nameof(SkySentinelMigrationCompleted)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string RtspUrl { get => (string)this[nameof(RtspUrl)]; set => this[nameof(RtspUrl)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string GeminiFreeKey { get => (string)this[nameof(GeminiFreeKey)]; set => this[nameof(GeminiFreeKey)] = value; }
        [UserScopedSetting, DefaultSettingValue("5")]
        public int CheckIntervalMinutes { get => (int)this[nameof(CheckIntervalMinutes)]; set => this[nameof(CheckIntervalMinutes)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool UseSunAltitudeLimit { get => (bool)this[nameof(UseSunAltitudeLimit)]; set => this[nameof(UseSunAltitudeLimit)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string DatasetDirectory { get => (string)this[nameof(DatasetDirectory)]; set => this[nameof(DatasetDirectory)] = value; }
        [UserScopedSetting, DefaultSettingValue("False")]
        public bool WriteSafetyStatusFile { get => (bool)this[nameof(WriteSafetyStatusFile)]; set => this[nameof(WriteSafetyStatusFile)] = value; }
        [UserScopedSetting, DefaultSettingValue("")]
        public string SafetyStatusFilePath { get => (string)this[nameof(SafetyStatusFilePath)]; set => this[nameof(SafetyStatusFilePath)] = value; }
    }

    private void WriteConfig(string sections) => File.WriteAllText(ConfigPath, "<configuration><userSettings>" + sections + "</userSettings></configuration>");
    private const string Legacy = """
        <AIWeather.Properties.Settings>
          <setting name="RtspUrl" serializeAs="String"><value>rtsp://camera.invalid/test?a=1&amp;b=2</value></setting>
          <setting name="GeminiFreeKey" serializeAs="String"><value>synthetic-test-secret</value></setting>
          <setting name="CheckIntervalMinutes" serializeAs="String"><value>2</value></setting>
          <setting name="UseSunAltitudeLimit" serializeAs="String"><value>True</value></setting>
          <setting name="UnknownOldSetting" serializeAs="String"><value>ignored</value></setting>
        </AIWeather.Properties.Settings>
        """;

    [Test] public void IndependentManifestAssemblyAndSettingsIdentity()
    {
        var assembly = typeof(PluginInfo).Assembly;
        assembly.GetName().Name.Should().Be("NINA.Plugin.SkySentinel");
        assembly.GetCustomAttribute<GuidAttribute>()!.Value.Should().Be(PluginInfo.PLUGIN_GUID);
        PluginInfo.PLUGIN_ID.Should().NotBe(Guid.Parse("A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D"));
        assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company.Should().Be("sedirk");
        assembly.GetCustomAttribute<AssemblyTitleAttribute>()!.Title.Should().Be("Sky Sentinel");
        new global::AIWeather.Properties.Settings().Context["GroupName"].Should().Be(SkySentinelSettingsMigration.CurrentGroup);
    }

    [Test] public void ImportsTypedCredentialsOnceAndPreservesOriginalFile()
    {
        WriteConfig(Legacy);
        var before = File.ReadAllText(ConfigPath);
        var target = new TestSettings();
        SkySentinelSettingsMigration.Import(target, ConfigPath).Should().BeTrue();
        target.RtspUrl.Should().Be("rtsp://camera.invalid/test?a=1&b=2");
        target.GeminiFreeKey.Should().Be("synthetic-test-secret");
        target.CheckIntervalMinutes.Should().Be(2);
        target.UseSunAltitudeLimit.Should().BeTrue();
        File.ReadAllText(ConfigPath).Should().Be(before);
        Directory.GetFiles(_root, "*.bak").Should().ContainSingle();
        target.GeminiFreeKey = "new-choice";
        SkySentinelSettingsMigration.Import(target, ConfigPath).Should().BeFalse();
        target.GeminiFreeKey.Should().Be("new-choice");
    }

    [Test] public void ExistingIndependentGroupIsNeverOverwritten()
    {
        WriteConfig(Legacy + "<SkySentinel.Properties.Settings />");
        var target = new TestSettings { RtspUrl = "my-new-camera" };
        SkySentinelSettingsMigration.Import(target, ConfigPath).Should().BeTrue();
        target.RtspUrl.Should().Be("my-new-camera");
        target.GeminiFreeKey.Should().BeEmpty();
        Directory.GetFiles(_root, "*.bak").Should().BeEmpty();
    }

    [Test] public void FreshInstallDoesNotDiscoverUnrelatedLegacyDataset()
    {
        var oldDataset = Directory.CreateDirectory(Path.Combine(_root, "old-dataset")).FullName;
        var target = new TestSettings();
        SkySentinelSettingsMigration.Import(target, ConfigPath, oldDataset).Should().BeTrue();
        target.DatasetDirectory.Should().BeEmpty();
        target.CheckIntervalMinutes.Should().Be(5);
    }

    [Test] public void ExistingDefaultDatasetIsReusedWithoutMovingData()
    {
        WriteConfig(Legacy);
        var oldDataset = Directory.CreateDirectory(Path.Combine(_root, "old-dataset")).FullName;
        File.WriteAllText(Path.Combine(oldDataset, "sample.txt"), "keep");
        var target = new TestSettings();
        SkySentinelSettingsMigration.Import(target, ConfigPath, oldDataset);
        target.DatasetDirectory.Should().Be(oldDataset);
        File.ReadAllText(Path.Combine(oldDataset, "sample.txt")).Should().Be("keep");
    }

    [Test] public void MalformedInputCannotPartiallyImportOrMarkComplete()
    {
        File.WriteAllText(ConfigPath, "<configuration><userSettings>");
        var target = new TestSettings();
        Action action = () => SkySentinelSettingsMigration.Import(target, ConfigPath);
        action.Should().Throw<System.Xml.XmlException>();
        target.SkySentinelMigrationCompleted.Should().BeFalse();
        target.RtspUrl.Should().BeEmpty();
    }

    [Test] public void InvalidTypedValueCannotPartiallyImportOrMarkComplete()
    {
        WriteConfig(Legacy.Replace("<value>2</value>", "<value>not-an-integer</value>"));
        var target = new TestSettings();
        Action action = () => SkySentinelSettingsMigration.Import(target, ConfigPath);
        action.Should().Throw<Exception>();
        target.SkySentinelMigrationCompleted.Should().BeFalse();
        target.RtspUrl.Should().BeEmpty();
        Directory.GetFiles(_root, "*.bak").Should().BeEmpty();
    }

    [Test] public void CustomDatasetPathIsKeptInsteadOfDiscoveringOldDefault()
    {
        var oldDefault = Directory.CreateDirectory(Path.Combine(_root, "old-default")).FullName;
        WriteConfig(Legacy.Replace("</AIWeather.Properties.Settings>",
            "<setting name='DatasetDirectory'><value>D:\\my-dataset</value></setting></AIWeather.Properties.Settings>"));
        var target = new TestSettings();
        SkySentinelSettingsMigration.Import(target, ConfigPath, oldDefault);
        target.DatasetDirectory.Should().Be(@"D:\my-dataset");
    }

    [Test] public void EnabledLegacyDefaultStatusFileIsPreserved()
    {
        WriteConfig(Legacy.Replace("</AIWeather.Properties.Settings>",
            "<setting name='WriteSafetyStatusFile'><value>True</value></setting></AIWeather.Properties.Settings>"));
        var target = new TestSettings();
        SkySentinelSettingsMigration.Import(target, ConfigPath);
        target.WriteSafetyStatusFile.Should().BeTrue();
        target.SafetyStatusFilePath.Should().Be(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "N.I.N.A", "sky_conditions.txt"));
    }

    [Test] public void ExternalXmlEntitiesAreRejected()
    {
        File.WriteAllText(ConfigPath, "<!DOCTYPE configuration [<!ENTITY outside SYSTEM 'file:///never-read'>]><configuration>&outside;</configuration>");
        Action action = () => SkySentinelSettingsMigration.Import(new TestSettings(), ConfigPath);
        action.Should().Throw<System.Xml.XmlException>();
    }
}
