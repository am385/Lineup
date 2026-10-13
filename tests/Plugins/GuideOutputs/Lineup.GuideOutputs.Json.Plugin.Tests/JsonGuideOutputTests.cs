using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.GuideOutputs.Json.Plugin.Tests;

/// <summary>Verifies JSON guide-output configuration and serialization.</summary>
public sealed class JsonGuideOutputTests
{
    /// <summary>Verifies portable directory settings preserve the generated JSON filename.</summary>
    [Fact]
    public void Settings_OutputDirectory_RoundTrips()
    {
        // Arrange
        var driver = new JsonGuideOutputDriver();
        var configuration = driver.CreateDefaultConfiguration(
            GuideOutputId.New(),
            new GuideOutputCreationContext
            {
                DefaultOutputDirectoryPath = Path.GetFullPath("guide-outputs"),
                OutputKey = "json_1"
            });
        var changedDirectory = Path.GetFullPath("changed-guide-outputs");

        // Act
        var result = driver.ApplySettings(
            configuration,
            [
                new AdapterSettingEdit
                {
                    Key = "outputDirectoryPath",
                    Action = AdapterSettingEditAction.Set,
                    Value = JsonSerializer.SerializeToElement(changedDirectory)
                }
            ]);

        // Assert
        Assert.Empty(AdapterSettingsValidator.ValidateDescriptor(driver.SettingsDescriptor));
        Assert.True(result.Succeeded);
        Assert.Equal(
            Path.Combine(changedDirectory, "lineup-json_1.json"),
            driver.ResolveDestination(result.Configuration!).Path);
        Assert.Equal("application/json", driver.ResolveDestination(result.Configuration!).ContentType);
    }

    /// <summary>Verifies normalized guide data round-trips through the public JSON document contract.</summary>
    [Fact]
    public async Task PublishAsync_VirtualGuideSnapshot_WritesJsonDocument()
    {
        // Arrange
        var virtualDeviceId = VirtualDeviceId.New();
        var guideSourceId = GuideSourceId.New();
        var virtualChannelId = new VirtualChannelId(virtualDeviceId, Guid.NewGuid());
        var snapshot = new VirtualGuideSnapshot
        {
            VirtualDeviceId = virtualDeviceId,
            SupplementalXml = ["<source-info-name>Lineup</source-info-name>"],
            Channels =
            [
                new VirtualGuideChannel
                {
                    ChannelId = virtualChannelId,
                    Number = "7.1",
                    Name = "Example",
                    Affiliate = "EX",
                    IsFavorite = true,
                    Programs =
                    [
                        new GuideProgram
                        {
                            ChannelId = new GuideChannelId(guideSourceId, "provider-channel"),
                            StartUtc = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero),
                            EndUtc = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
                            Title = "Evening News",
                            Categories = ["News"]
                        }
                    ]
                }
            ]
        };
        await using var destination = new MemoryStream();

        // Act
        await new JsonGuidePublisher().PublishAsync(snapshot, destination, TestContext.Current.CancellationToken);
        destination.Position = 0;
        var document = await JsonGuideSerialization.DeserializeAsync(destination, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(document);
        Assert.Equal(JsonGuideDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(virtualDeviceId.Value, document.VirtualDeviceId);
        var channel = Assert.Single(document.Channels);
        Assert.Equal(virtualChannelId.Value, channel.Id);
        Assert.Equal("7.1", channel.Number);
        var program = Assert.Single(channel.Programs);
        Assert.Equal("Evening News", program.Title);
        Assert.Equal(guideSourceId.Value, program.ChannelId.GuideSourceId);
        Assert.Equal("provider-channel", program.ChannelId.ProviderKey);
    }
}
