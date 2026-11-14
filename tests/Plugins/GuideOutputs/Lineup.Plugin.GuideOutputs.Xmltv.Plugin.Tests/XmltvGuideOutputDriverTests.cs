using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Guides;
using Lineup.Core.Common.Streaming;
using Xunit;

namespace Lineup.Plugin.GuideOutputs.Xmltv.Plugin.Tests;

/// <summary>Verifies XMLTV guide-output configuration and initialization.</summary>
public sealed class XmltvGuideOutputDriverTests
{
    /// <summary>Verifies output settings reach XMLTV publication and survive unrelated directory edits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Settings_UnavailablePrograms_ControlsPublication(bool includeUnavailablePrograms)
    {
        // Arrange
        var driver = new XmltvGuideOutputDriver();
        var configuration = new AdapterConfiguration("xmltv", JsonSerializer.SerializeToElement(new { OutputPath = "guide.xml" }));
        Assert.False(driver.GetSettings(configuration).Values.Single(value => value.Key == "includeUnavailablePrograms").Value!.Value.GetBoolean());
        var edit = driver.ApplySettings(configuration,
        [
            new AdapterSettingEdit
            {
                Key = "includeUnavailablePrograms",
                Action = AdapterSettingEditAction.Set,
                Value = JsonSerializer.SerializeToElement(includeUnavailablePrograms)
            }
        ]);
        Assert.True(edit.Succeeded);
        var directoryEdit = driver.ApplySettings(edit.Configuration!,
        [
            new AdapterSettingEdit
            {
                Key = "outputDirectoryPath",
                Action = AdapterSettingEditAction.Set,
                Value = JsonSerializer.SerializeToElement(Path.GetTempPath())
            }
        ]);
        Assert.True(directoryEdit.Succeeded);
        var deviceId = VirtualDeviceId.New();
        var snapshot = new VirtualGuideSnapshot
        {
            VirtualDeviceId = deviceId,
            Channels =
            [
                new VirtualGuideChannel
                {
                    ChannelId = VirtualChannelId.New(deviceId),
                    Number = "7.1",
                    Name = "No guide",
                    Programs = []
                }
            ]
        };
        using var destination = new MemoryStream();

        // Act
        await new XmltvGuidePublisher().PublishAsync(snapshot, destination, directoryEdit.Configuration!, TestContext.Current.CancellationToken);
        var document = XDocument.Parse(Encoding.UTF8.GetString(destination.ToArray()));

        // Assert
        Assert.Single(document.Root!.Elements("channel"));
        Assert.Equal(includeUnavailablePrograms ? 1 : 0, document.Root.Elements("programme").Count());
        Assert.Equal(includeUnavailablePrograms, driver.GetSettings(directoryEdit.Configuration!).Values.Single(value => value.Key == "includeUnavailablePrograms").Value!.Value.GetBoolean());
    }

    /// <summary>Verifies invalid boolean edits cannot silently change the placeholder policy.</summary>
    [Fact]
    public void Settings_UnavailablePrograms_RejectsNonBoolean()
    {
        // Arrange
        var driver = new XmltvGuideOutputDriver();
        var configuration = XmltvGuideOutputDriver.CreateBuiltIn("guide.xml").Adapter;

        // Act
        var result = driver.ApplySettings(configuration,
        [
            new AdapterSettingEdit
            {
                Key = "includeUnavailablePrograms",
                Action = AdapterSettingEditAction.Set,
                Value = JsonSerializer.SerializeToElement("true")
            }
        ]);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("includeUnavailablePrograms", result.Errors.Keys);
    }

    /// <summary>Verifies a fresh topology gains one stable global output using the configured default path.</summary>
    [Fact]
    public void Normalize_FreshConfiguration_CreatesGlobalOutput()
    {
        // Arrange
        var driver = new XmltvGuideOutputDriver();
        var configuration = new StreamingConfiguration
        {
            GuideOutputs = []
        };

        // Act
        var normalized = driver.Normalize(
            configuration,
            CreateContext("guide.xml"));

        // Assert
        Assert.Equal(StreamingConfiguration.CurrentSchemaVersion, normalized.SchemaVersion);
        var output = Assert.Single(normalized.GuideOutputs);
        Assert.Equal(XmltvGuideOutputDriver.BuiltInId, output.Id);
        Assert.Equal(XmltvGuideOutputDriver.BuiltInName, output.Name);
        Assert.True(output.IsBuiltIn);
        Assert.Equal(GuideOutputSelectionMode.All, output.VirtualDeviceSelectionMode);
        Assert.Equal(GuideOutputSelectionMode.All, output.GuideSourceSelectionMode);
        Assert.Equal(
            Path.GetFullPath("guide.xml"),
            driver.ResolveDestination(output.Adapter).Path,
            ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>Verifies built-in outputs owned by another plugin are not claimed as the global XMLTV output.</summary>
    [Fact]
    public void Normalize_OtherBuiltInOutput_PreservesPluginOwnership()
    {
        // Arrange
        var driver = new XmltvGuideOutputDriver();
        var otherBuiltIn = new GuideOutputDefinition
        {
            Id = GuideOutputId.New(),
            Name = "Other",
            IsBuiltIn = true,
            Adapter = new AdapterConfiguration("other", JsonSerializer.SerializeToElement(new { }))
        };

        // Act
        var normalized = driver.Normalize(new StreamingConfiguration { GuideOutputs = [otherBuiltIn] }, CreateContext("guide.xml"));

        // Assert
        Assert.Contains(normalized.GuideOutputs, output => output.Id == otherBuiltIn.Id);
        Assert.Contains(normalized.GuideOutputs, output => output.Id == XmltvGuideOutputDriver.BuiltInId);
    }

    /// <summary>Verifies the portable settings descriptor edits and validates the output path.</summary>
    [Fact]
    public void Settings_OutputPath_RoundTrips()
    {
        // Arrange
        var driver = new XmltvGuideOutputDriver();
        var configuration = driver.CreateDefaultConfiguration(
            GuideOutputId.New(),
            new GuideOutputCreationContext
            {
                DefaultOutputDirectoryPath = "/xmltv",
                OutputKey = "consumer"
            });

        // Act
        var result = driver.ApplySettings(
            configuration,
            [
                new AdapterSettingEdit
                {
                    Key = "outputDirectoryPath",
                    Action = AdapterSettingEditAction.Set,
                    Value = JsonSerializer.SerializeToElement("/consumer")
                }
            ]);

        // Assert
        Assert.Empty(AdapterSettingsValidator.ValidateDescriptor(driver.SettingsDescriptor));
        Assert.True(result.Succeeded);
        Assert.Equal(
            Path.GetFullPath("/consumer"),
            driver.GetSettings(result.Configuration!).Values.Single(value => value.Key == "outputDirectoryPath").Value?.GetString(),
            ignoreCase: OperatingSystem.IsWindows());
        Assert.EndsWith(
            Path.Combine("consumer", "lineup-consumer.xml"),
            driver.ResolveDestination(result.Configuration!).Path,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies new XMLTV outputs default beneath the host-provided guide-output directory.</summary>
    [Fact]
    public void CreateDefaultConfiguration_OutputDirectory_UsesUniqueXmltvFile()
    {
        // Arrange
        var driver = new XmltvGuideOutputDriver();
        var outputId = GuideOutputId.New();
        var directory = Path.Combine(Path.GetTempPath(), "lineup-guide-outputs");

        // Act
        var configuration = driver.CreateDefaultConfiguration(
            outputId,
            new GuideOutputCreationContext
            {
                DefaultOutputDirectoryPath = directory,
                OutputKey = "xmltv_2"
            });

        // Assert
        var outputPath = driver.ResolveDestination(configuration).Path;
        Assert.Equal(Path.Combine(directory, "lineup-xmltv_2.xml"), outputPath);
    }

    private static StreamingConfigurationInitializationContext CreateContext(string path) =>
        new() { DefaultXmltvOutputPath = path };
}
