using Lineup.Core.Common.Guides;
using Lineup.Core;
using Lineup.Core.Settings;
using Lineup.Core.Storage;
using Lineup.Core.Extensibility.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.GuideOutputs.Json.Plugin;
using Lineup.Plugin.LineupLink.Guides;
using Lineup.Plugin.LineupLink.Physical;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.Plugin.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.App.Tui;
using Lineup.Plugin.GuideOutputs.Xmltv.Plugin;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using System.Text.Json;
using Xunit;

namespace Lineup.App.Tui.Tests;

/// <summary>Verifies TUI plugin composition and topology activation ordering.</summary>
public sealed class ProgramTests
{
    /// <summary>Verifies the production TUI composition applies persisted retention to actual guide imports.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(48)]
    public async Task AddCoreServices_ConfiguredRetention_PrunesImportedHistory(int retentionHours)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-tui-retention-");
        try
        {
            var store = new AppDataStore(root.FullName);
            File.WriteAllText(store.SettingsPath, JsonSerializer.Serialize(new LineupSettings { EpgHistoryRetentionHours = retentionHours }));
            var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var clock = Substitute.For<TimeProvider>();
            clock.GetUtcNow().Returns(now);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton(clock);
            Program.AddCoreServices(services, store, store.DatabasePath);
            using var provider = services.BuildServiceProvider();
            var sourceId = GuideSourceId.New();
            var channelId = new GuideChannelId(sourceId, "7.1");
            var import = new GuideImport
            {
                SourceId = sourceId,
                Channels = [new GuideChannel { Id = channelId, Number = "7.1", Name = "News" }],
                Programs =
                [
                    new GuideProgram { ChannelId = channelId, StartUtc = now.AddHours(-36), EndUtc = now.AddHours(-35), Title = "Older" },
                    new GuideProgram { ChannelId = channelId, StartUtc = now.AddHours(-2), EndUtc = now.AddHours(-1), Title = "Recent" }
                ],
                CoverageStartUtc = now.AddDays(-2),
                CoverageEndUtc = now
            };

            // Act
            await provider.GetRequiredService<DeviceGuideStore>().ImportAsync(import, TestContext.Current.CancellationToken);
            await using var context = await provider.GetRequiredService<IDbContextFactory<EpgDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
            var titles = await context.GuideSourcePrograms.OrderBy(program => program.Title).Select(program => program.Title).ToArrayAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(retentionHours == 0 ? [] : new[] { "Older", "Recent" }, titles);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

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

    /// <summary>Verifies a completed factory reset permits settings persistence without restarting the TUI process.</summary>
    [Fact]
    public async Task ResetPersistedState_IncompatibleSettings_AllowsImmediateSave()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-tui-reset-");
        var appDataStore = new AppDataStore(root.FullName);
        await File.WriteAllTextAsync(
            appDataStore.SettingsPath,
            """{"schemaVersion":2}""",
            TestContext.Current.CancellationToken);
        var initialState = StartupSchemaGate.Check(appDataStore);
        Assert.True(initialState.IsFactoryResetRequired);

        // Act
        var resetState = Program.ResetPersistedState(appDataStore, appDataStore.DatabasePath);
        var settings = new LineupSettingsService(
            NullLogger<LineupSettingsService>.Instance,
            appDataStore,
            new ConfigurationBuilder().Build(),
            resetState);
        await settings.UpdateAsync(candidate => candidate.TargetDays = 5);

        // Assert
        Assert.False(resetState.IsFactoryResetRequired);
        Assert.Equal(5, settings.Settings.TargetDays);
        Assert.True(File.Exists(appDataStore.SettingsPath));
        root.Delete(recursive: true);
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
