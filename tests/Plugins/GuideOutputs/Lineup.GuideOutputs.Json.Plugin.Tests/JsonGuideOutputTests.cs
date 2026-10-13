using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
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
            SupplementalXml =
            [
                "<source-info-name>Lineup</source-info-name>",
                "<source-info-name>Backup</source-info-name>"
            ],
            Channels =
            [
                new VirtualGuideChannel
                {
                    ChannelId = virtualChannelId,
                    Number = "7.1",
                    Name = "Example",
                    Affiliate = "EX",
                    IsFavorite = true,
                    SupplementalXml = """
                        <supplemental xmlns="urn:lineup:xmltv-supplemental:v1">
                          <attributes>
                            <attribute name="source" namespace="" value="channel-feed" />
                          </attributes>
                          <elements>
                            <display-name xmlns="" lang="es">Ejemplo</display-name>
                            <display-name xmlns="">Example Alternate</display-name>
                          </elements>
                        </supplemental>
                        """,
                    Programs =
                    [
                        new GuideProgram
                        {
                            ChannelId = new GuideChannelId(guideSourceId, "provider-channel"),
                            StartUtc = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero),
                            EndUtc = new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero),
                            Title = "Evening News",
                            Categories = ["News"],
                            SupplementalXml = """
                                <supplemental xmlns="urn:lineup:xmltv-supplemental:v1">
                                  <owned key="rating">
                                    <rating xmlns="" system="MPAA" />
                                  </owned>
                                </supplemental>
                                """
                        }
                    ]
                }
            ]
        };
        await using var destination = new MemoryStream();

        // Act
        await new JsonGuidePublisher().PublishAsync(snapshot, destination, TestContext.Current.CancellationToken);
        var json = Encoding.UTF8.GetString(destination.ToArray());
        destination.Position = 0;
        var document = await JsonGuideSerialization.DeserializeAsync(destination, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(document);
        Assert.Contains("\"supplemental\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("supplementalXml", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(JsonGuideDocument.CurrentSchemaVersion, document.SchemaVersion);
        Assert.Equal(virtualDeviceId.Value, document.VirtualDeviceId);
        var sourceNames = Assert.IsType<JsonArray>(document.Supplemental["source-info-name"]);
        Assert.Equal(["Lineup", "Backup"], sourceNames.Select(value => value!.GetValue<string>()));
        var channel = Assert.Single(document.Channels);
        Assert.Equal(virtualChannelId.Value, channel.Id);
        Assert.Equal("7.1", channel.Number);
        Assert.NotNull(channel.Supplemental);
        Assert.Equal("channel-feed", channel.Supplemental["source"]!.GetValue<string>());
        var displayNames = Assert.IsType<JsonArray>(channel.Supplemental["display-name"]);
        var localizedDisplayName = Assert.IsType<JsonObject>(displayNames[0]);
        Assert.Equal("Ejemplo", localizedDisplayName["$value"]!.GetValue<string>());
        Assert.Equal("es", localizedDisplayName["lang"]!.GetValue<string>());
        Assert.Equal("Example Alternate", displayNames[1]!.GetValue<string>());
        var program = Assert.Single(channel.Programs);
        Assert.Equal("Evening News", program.Title);
        Assert.Equal(guideSourceId.Value, program.ChannelId.GuideSourceId);
        Assert.Equal("provider-channel", program.ChannelId.ProviderKey);
        Assert.NotNull(program.Supplemental);
        var rating = Assert.IsType<JsonObject>(program.Supplemental["rating"]);
        Assert.Equal("MPAA", rating["system"]!.GetValue<string>());
        Assert.DoesNotContain("namespaceUri", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"elements\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ownedElements\"", json, StringComparison.Ordinal);
    }
}
