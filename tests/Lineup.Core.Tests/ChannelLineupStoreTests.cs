using System.Text.Json;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.Tests;

/// <summary>
/// Verifies independent physical tuner lineup persistence and refresh.
/// </summary>
public class ChannelLineupStoreTests
{
    /// <summary>
    /// Verifies a successful refresh persists a unique combined lineup for later guide filtering.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_PersistsLineupWithoutFetchingGuide()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var path = Path.Combine(root.FullName, "channels.json");
            var store = new ChannelLineupStore(path);
            var provider = Substitute.For<IChannelLineupProvider>();
            provider.FetchChannelLineupAsync(Arg.Any<CancellationToken>()).Returns(
            [
                CreateChannel("104.1", "High"),
                CreateChannel("7.1", "Primary"),
                CreateChannel("7.1", "Duplicate"),
                CreateChannel("9.1", "Secondary"),
                CreateChannel("10.1", "Ten")
            ]);
            var service = new ChannelLineupRefreshService(
                NullLogger<ChannelLineupRefreshService>.Instance,
                provider,
                store);

            // Act
            var refreshed = await service.RefreshAsync(TestContext.Current.CancellationToken);
            var persisted = await store.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(["7.1", "9.1", "10.1", "104.1"], refreshed.Channels.Select(channel => channel.GuideNumber));
            Assert.Equal(refreshed.RefreshedAtUtc, persisted!.RefreshedAtUtc);
            Assert.Equal(
                refreshed.Channels.Select(channel => channel.GuideNumber),
                persisted.Channels.Select(channel => channel.GuideNumber));
            await provider.Received(1).FetchChannelLineupAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies a failed refresh preserves the previously saved lineup.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenDeviceQueryFails_PreservesSavedLineup()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var store = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            await store.StoreAsync([CreateChannel("7.1", "Existing")], TestContext.Current.CancellationToken);
            var provider = Substitute.For<IChannelLineupProvider>();
            provider.FetchChannelLineupAsync(Arg.Any<CancellationToken>())
                .Returns<Task<List<HDHomeRunChannel>>>(_ => throw new InvalidOperationException("Device unavailable"));
            var service = new ChannelLineupRefreshService(
                NullLogger<ChannelLineupRefreshService>.Instance,
                provider,
                store);

            // Act
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RefreshAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal("7.1", Assert.Single((await store.ReadAsync(TestContext.Current.CancellationToken))!.Channels).GuideNumber);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies an unexpectedly empty device response does not replace a usable saved lineup.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_WhenNoChannelsReturned_PreservesSavedLineup()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var store = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            await store.StoreAsync([CreateChannel("7.1", "Existing")], TestContext.Current.CancellationToken);
            var provider = Substitute.For<IChannelLineupProvider>();
            provider.FetchChannelLineupAsync(Arg.Any<CancellationToken>()).Returns([]);
            var service = new ChannelLineupRefreshService(
                NullLogger<ChannelLineupRefreshService>.Instance,
                provider,
                store);

            // Act
            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.RefreshAsync(TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("previously saved lineup was preserved", exception.Message);
            Assert.Equal("7.1", Assert.Single((await store.ReadAsync(TestContext.Current.CancellationToken))!.Channels).GuideNumber);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies firmware-defined properties survive the persisted tuner lineup snapshot.
    /// </summary>
    [Fact]
    public async Task StoreAsync_PreservesUnknownFirmwareProperties()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var store = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            var channel = CreateChannel("7.1", "Primary") with
            {
                AdditionalProperties = new Dictionary<string, JsonElement>
                {
                    ["FirmwareMetric"] = JsonSerializer.SerializeToElement(new { Value = 42 })
                }
            };

            // Act
            await store.StoreAsync([channel], TestContext.Current.CancellationToken);
            var persisted = Assert.Single((await store.ReadAsync(TestContext.Current.CancellationToken))!.Channels);

            // Assert
            Assert.Equal(42, persisted.AdditionalProperties!["FirmwareMetric"].GetProperty("Value").GetInt32());
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies old snapshots without availability metadata keep every channel enabled.
    /// </summary>
    [Fact]
    public async Task ReadAsync_LegacySnapshot_DefaultsAllChannelsToEnabled()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var path = Path.Combine(root.FullName, "channels.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "RefreshedAtUtc": "2026-09-20T12:00:00Z",
                  "Channels": [
                    {
                      "GuideNumber": "7.1",
                      "GuideName": "Existing",
                      "URL": "http://device/auto/v7.1"
                    }
                  ]
                }
                """,
                TestContext.Current.CancellationToken);
            var store = new ChannelLineupStore(path);

            // Act
            var snapshot = await store.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.True(snapshot!.IsChannelEnabled("7.1"));
            Assert.Equal(1, snapshot.EnabledChannelCount);
            Assert.Empty(snapshot.DisabledGuideNumbers);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies channel availability changes persist without replacing tuner metadata.
    /// </summary>
    [Fact]
    public async Task SetChannelEnabledAsync_DisablesAndReenablesSavedChannel()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var store = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            var original = await store.StoreAsync(
                [CreateChannel("7.1", "Existing"), CreateChannel("9.1", "Other")],
                TestContext.Current.CancellationToken);

            // Act
            var disabled = await store.SetChannelEnabledAsync("7.1", enabled: false, TestContext.Current.CancellationToken);
            var enabled = await store.SetChannelEnabledAsync("7.1", enabled: true, TestContext.Current.CancellationToken);

            // Assert
            Assert.False(disabled.IsChannelEnabled("7.1"));
            Assert.Equal(1, disabled.EnabledChannelCount);
            Assert.True(enabled.IsChannelEnabled("7.1"));
            Assert.Equal(2, enabled.EnabledChannelCount);
            Assert.Equal(original.RefreshedAtUtc, enabled.RefreshedAtUtc);
            Assert.Equal("Existing", enabled.Channels[0].GuideName);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Verifies refreshes preserve matching choices, remove stale choices, and enable new channels.
    /// </summary>
    [Fact]
    public async Task RefreshAsync_PreservesMatchingDisabledChannelsAndEnablesNewChannels()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-channels-");
        try
        {
            var store = new ChannelLineupStore(Path.Combine(root.FullName, "channels.json"));
            await store.StoreAsync(
                [CreateChannel("7.1", "Existing"), CreateChannel("9.1", "Removed")],
                TestContext.Current.CancellationToken);
            await store.SetChannelEnabledAsync("7.1", enabled: false, TestContext.Current.CancellationToken);
            await store.SetChannelEnabledAsync("9.1", enabled: false, TestContext.Current.CancellationToken);
            var provider = Substitute.For<IChannelLineupProvider>();
            provider.FetchChannelLineupAsync(Arg.Any<CancellationToken>()).Returns(
                [CreateChannel("7.1", "Existing Updated"), CreateChannel("10.1", "New")]);
            var service = new ChannelLineupRefreshService(NullLogger<ChannelLineupRefreshService>.Instance, provider, store);

            // Act
            var refreshed = await service.RefreshAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.False(refreshed.IsChannelEnabled("7.1"));
            Assert.True(refreshed.IsChannelEnabled("10.1"));
            Assert.Equal(["7.1"], refreshed.DisabledGuideNumbers);
            Assert.Equal(1, refreshed.EnabledChannelCount);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static HDHomeRunChannel CreateChannel(string number, string name) => new()
    {
        GuideNumber = number,
        GuideName = name,
        URL = $"http://device/auto/v{number}"
    };
}
