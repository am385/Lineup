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
                CreateChannel("7.1", "Primary"),
                CreateChannel("7.1", "Duplicate"),
                CreateChannel("9.1", "Secondary")
            ]);
            var service = new ChannelLineupRefreshService(
                NullLogger<ChannelLineupRefreshService>.Instance,
                provider,
                store);

            // Act
            var refreshed = await service.RefreshAsync(TestContext.Current.CancellationToken);
            var persisted = await store.ReadAsync(TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(["7.1", "9.1"], refreshed.Channels.Select(channel => channel.GuideNumber));
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

    private static HDHomeRunChannel CreateChannel(string number, string name) => new()
    {
        GuideNumber = number,
        GuideName = name,
        URL = $"http://device/auto/v{number}"
    };
}
