using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.LineupLink.Guides;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Virtual;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Tui;
using Lineup.GuideOutputs.Xmltv.Plugin;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Lineup.Tui.Tests;

/// <summary>Verifies TUI plugin composition and topology activation ordering.</summary>
public sealed class ProgramTests
{
    /// <summary>Verifies the TUI composes every Lineup Link adapter category.</summary>
    [Fact]
    public void AddStreamingAdapters_RegistersLineupLinkPhysicalVirtualAndGuideDrivers()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        Program.AddStreamingAdapters(services);

        // Assert
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IPhysicalTunerDriver) &&
            descriptor.ImplementationType == typeof(LineupLinkPhysicalTunerDriver));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IVirtualTunerDriver) &&
            descriptor.ImplementationType == typeof(LineupLinkVirtualTunerDriver));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IGuideSourceDriver) &&
            descriptor.ImplementationType == typeof(LineupLinkGuideSourceDriver));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IGuideOutputDriver) &&
            descriptor.ImplementationFactory is not null);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(XmltvGuideOutputDriver));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(JsonGuideOutputDriver));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IGuidePublisher) &&
            descriptor.ImplementationFactory is not null);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(HdHomeRunVirtualTunerDriver) &&
            descriptor.ImplementationType == typeof(HdHomeRunVirtualTunerDriver));
    }

    /// <summary>Verifies physical devices are activated before dependent guide sources.</summary>
    [Fact]
    public async Task ApplyTopologyAsync_RefreshesPhysicalDevicesBeforeApplyingGuides()
    {
        // Arrange
        var calls = new List<string>();
        var configuration = new StreamingConfiguration();
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(configuration, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("physical");
                return Task.FromResult<IReadOnlyList<PhysicalLineupRefreshResult>>([]);
            });
        var guides = Substitute.For<IGuideOrchestrator>();
        guides.ApplyAsync(configuration, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("guides");
                return Task.CompletedTask;
            });

        // Act
        await Program.ApplyTopologyAsync(lineups, guides, configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["physical", "guides"], calls);
    }

    /// <summary>Verifies TUI export resolves the configured built-in XMLTV destination through the registered plugin.</summary>
    [Fact]
    public void ResolveBuiltInXmltvOutputPath_ConfiguredDirectory_ReturnsPluginDestination()
    {
        // Arrange
        var outputPath = Path.Combine(Path.GetTempPath(), "lineup-tests", "custom-guide", "epg.xml");
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                new GuideOutputDefinition
                {
                    Id = XmltvGuideOutputDriver.BuiltInId,
                    Name = XmltvGuideOutputDriver.BuiltInName,
                    Key = "global",
                    IsBuiltIn = true,
                    Adapter = new AdapterConfiguration(
                        XmltvGuideOutputDriver.AdapterKind,
                        JsonSerializer.SerializeToElement(new XmltvGuideOutputConfiguration { OutputPath = outputPath }))
                }
            ]
        };
        var driver = new XmltvGuideOutputDriver();
        var adapters = new StreamingAdapterRegistry([], [], [], [driver]);

        // Act
        var resolved = Program.ResolveBuiltInXmltvOutputPath(configuration, adapters);

        // Assert
        Assert.Equal(Path.GetFullPath(outputPath), resolved);
    }

    /// <summary>Verifies TUI export reports no destination when the built-in XMLTV output is disabled.</summary>
    [Fact]
    public void ResolveBuiltInXmltvOutputPath_DisabledBuiltInOutput_ReturnsNull()
    {
        // Arrange
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                XmltvGuideOutputDriver.CreateBuiltIn(Path.Combine(Path.GetTempPath(), "lineup-tests", "epg.xml")) with
                {
                    Enabled = false
                }
            ]
        };
        var adapters = new StreamingAdapterRegistry([], [], [], [new XmltvGuideOutputDriver()]);

        // Act
        var resolved = Program.ResolveBuiltInXmltvOutputPath(configuration, adapters);

        // Assert
        Assert.Null(resolved);
    }
}
