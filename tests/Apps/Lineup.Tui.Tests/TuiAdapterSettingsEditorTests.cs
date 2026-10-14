using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Tui;
using Xunit;

namespace Lineup.Tui.Tests;

/// <summary>
/// Verifies TUI portable-settings and topology editing behavior.
/// </summary>
public sealed class TuiAdapterSettingsEditorTests
{
    /// <summary>Verifies the TUI derives a client-facing URL from the dedicated listener.</summary>
    [Theory]
    [InlineData(VirtualOutputTransport.Http, "192.0.2.10, ::1", 8600, "http://192.0.2.10:8600/")]
    [InlineData(VirtualOutputTransport.Https, "*", 8443, "https://localhost:8443/")]
    public void BuildAutomaticPublicBaseUri_ListenerSettings_ReturnsClientUrl(
        VirtualOutputTransport transport,
        string addresses,
        int port,
        string expected)
    {
        // Arrange

        // Act
        var result = TuiAdapterSettingsEditor.BuildAutomaticPublicBaseUri(transport, addresses, port);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Verifies dedicated Lineup Link hosting derives protocol security from its client-facing URI.</summary>
    [Theory]
    [InlineData("http://lineup.example:8600/", false)]
    [InlineData("https://lineup.example:8600/", true)]
    public void ApplyDedicatedHosting_LineupLink_DerivesProtocolTransport(string publicBaseUri, bool expectedEncryption)
    {
        // Arrange
        var adapter = new AdapterConfiguration(LineupLinkProtocol.AdapterKind, JsonSerializer.SerializeToElement(new LineupLinkVirtualConfiguration
        {
            PublicBaseUrl = "https://old.example/",
            RequireEncryption = !expectedEncryption,
            CertificateSha256 = new string('A', 64)
        }));

        // Act
        var updated = TuiAdapterSettingsEditor.ApplyDedicatedHosting(adapter, publicBaseUri);

        // Assert
        var configuration = updated.Configuration.Deserialize<LineupLinkVirtualConfiguration>();
        Assert.NotNull(configuration);
        Assert.Equal(publicBaseUri, configuration.PublicBaseUrl);
        Assert.Equal(expectedEncryption, configuration.RequireEncryption);
        Assert.Equal(expectedEncryption ? new string('A', 64) : null, configuration.CertificateSha256);
    }

    /// <summary>Verifies a controlling edit immediately makes its dependent portable setting visible.</summary>
    [Fact]
    public void ApplyEditToSnapshot_EnableNetworkAdvertisement_ShowsAdvertisedBaseUrl()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var configuration = driver.CreateDefaultConfiguration(VirtualDeviceId.New());
        var current = driver.GetSettings(configuration).Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var advertisedBaseUrl = driver.SettingsDescriptor.Fields.Single(field => field.Key == "advertisedBaseUrl");
        var edit = new AdapterSettingEdit
        {
            Key = "enableNetworkAdvertisement",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement(true)
        };
        Assert.False(TuiAdapterSettingsEditor.IsVisible(advertisedBaseUrl, current));

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.True(TuiAdapterSettingsEditor.IsVisible(advertisedBaseUrl, current));
    }

    /// <summary>Verifies setting a controlling boolean to false immediately hides its dependent setting.</summary>
    [Fact]
    public void ApplyEditToSnapshot_DisableControllingBoolean_HidesDependentField()
    {
        // Arrange
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal)
        {
            ["enabled"] = Snapshot("enabled", true)
        };
        var dependent = ConditionalField("enabled", "true");
        var edit = new AdapterSettingEdit
        {
            Key = "enabled",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement(false)
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.False(TuiAdapterSettingsEditor.IsVisible(dependent, current));
    }

    /// <summary>Verifies clearing a controlling setting immediately hides its dependent setting.</summary>
    [Fact]
    public void ApplyEditToSnapshot_ClearControllingValue_HidesDependentField()
    {
        // Arrange
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal)
        {
            ["transportSecurity"] = Snapshot("transportSecurity", "Https")
        };
        var dependent = ConditionalField("transportSecurity", "Https");
        var edit = new AdapterSettingEdit
        {
            Key = "transportSecurity",
            Action = AdapterSettingEditAction.Clear
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.False(TuiAdapterSettingsEditor.IsVisible(dependent, current));
        Assert.False(current["transportSecurity"].HasValue);
    }

    /// <summary>Verifies string-valued controlling edits use the same case-insensitive comparison as persisted snapshots.</summary>
    [Fact]
    public void ApplyEditToSnapshot_SetStringValue_ShowsMatchingDependentField()
    {
        // Arrange
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal);
        var dependent = ConditionalField("transportSecurity", "Https");
        var edit = new AdapterSettingEdit
        {
            Key = "transportSecurity",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement("https")
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.True(TuiAdapterSettingsEditor.IsVisible(dependent, current));
    }

    /// <summary>Verifies preserving a controlling setting leaves its visibility state unchanged.</summary>
    [Fact]
    public void ApplyEditToSnapshot_PreserveControllingValue_KeepsDependentFieldVisible()
    {
        // Arrange
        var original = Snapshot("enabled", true);
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal)
        {
            ["enabled"] = original
        };
        var dependent = ConditionalField("enabled", "true");
        var edit = new AdapterSettingEdit
        {
            Key = "enabled",
            Action = AdapterSettingEditAction.Preserve
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.Same(original, current["enabled"]);
        Assert.True(TuiAdapterSettingsEditor.IsVisible(dependent, current));
    }

    /// <summary>Verifies physical-device removal cleans virtual and guide-source subscriptions.</summary>
    [Fact]
    public void RemovePhysicalDevice_SelectedSubscriptions_RemovesReferences()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var configuration = CreateConfiguration(physicalId, out _, out _);

        // Act
        var updated = TuiAdapterSettingsEditor.RemovePhysicalDevice(configuration, physicalId);

        // Assert
        Assert.DoesNotContain(updated.PhysicalDevices, device => device.Id == physicalId);
        Assert.DoesNotContain(physicalId, Assert.Single(updated.VirtualDevices).Subscription.SelectedDeviceIds);
        Assert.DoesNotContain(physicalId, Assert.Single(updated.GuideSources).Subscription.SelectedDeviceIds);
        Assert.Empty(StreamingConfigurationValidator.Validate(updated));
    }

    /// <summary>Verifies virtual-device removal cleans Guide Output selections.</summary>
    [Fact]
    public void RemoveVirtualDevice_GuideOutputSelection_RemovesReference()
    {
        // Arrange
        var configuration = CreateConfiguration(PhysicalDeviceId.New(), out var virtualId, out _);

        // Act
        var updated = TuiAdapterSettingsEditor.RemoveVirtualDevice(configuration, virtualId);

        // Assert
        Assert.DoesNotContain(updated.VirtualDevices, device => device.Id == virtualId);
        Assert.DoesNotContain(virtualId, Assert.Single(updated.GuideOutputs).SelectedVirtualDeviceIds);
        Assert.Empty(StreamingConfigurationValidator.Validate(updated));
    }

    /// <summary>Verifies guide-source removal cleans Guide Output selections.</summary>
    [Fact]
    public void RemoveGuideSource_GuideOutputSelection_RemovesReference()
    {
        // Arrange
        var configuration = CreateConfiguration(PhysicalDeviceId.New(), out _, out var guideSourceId);

        // Act
        var updated = TuiAdapterSettingsEditor.RemoveGuideSource(configuration, guideSourceId);

        // Assert
        Assert.DoesNotContain(updated.GuideSources, source => source.Id == guideSourceId);
        Assert.DoesNotContain(guideSourceId, Assert.Single(updated.GuideOutputs).SelectedGuideSourceIds);
        Assert.Empty(StreamingConfigurationValidator.Validate(updated));
    }

    /// <summary>Verifies custom outputs can be removed while built-in outputs remain protected.</summary>
    [Fact]
    public void RemoveGuideOutput_BuiltInAndCustom_RemovesOnlyCustomOutput()
    {
        // Arrange
        var customId = GuideOutputId.New();
        var builtInId = GuideOutputId.New();
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                new GuideOutputDefinition
                {
                    Id = customId,
                    Name = "Custom",
                    Key = "custom",
                    Adapter = Adapter("xmltv")
                },
                new GuideOutputDefinition
                {
                    Id = builtInId,
                    Name = "Global",
                    Key = "global",
                    IsBuiltIn = true,
                    Adapter = Adapter("xmltv")
                }
            ]
        };

        // Act
        var customRemoved = TuiAdapterSettingsEditor.RemoveGuideOutput(configuration, customId);
        var builtInProtected = TuiAdapterSettingsEditor.RemoveGuideOutput(customRemoved, builtInId);

        // Assert
        var remaining = Assert.Single(builtInProtected.GuideOutputs);
        Assert.Equal(builtInId, remaining.Id);
        Assert.True(remaining.IsBuiltIn);
    }

    private static StreamingConfiguration CreateConfiguration(
        PhysicalDeviceId physicalId,
        out VirtualDeviceId virtualId,
        out GuideSourceId guideSourceId)
    {
        virtualId = VirtualDeviceId.New();
        guideSourceId = GuideSourceId.New();
        return new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = physicalId,
                    Name = "Physical",
                    Adapter = Adapter("physical")
                }
            ],
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualId,
                    Name = "Virtual",
                    Adapter = Adapter("virtual"),
                    Subscription = new PhysicalDeviceSubscription
                    {
                        Mode = PhysicalDeviceSubscriptionMode.Selected,
                        SelectedDeviceIds = [physicalId]
                    }
                }
            ],
            GuideSources =
            [
                new GuideSourceDefinition
                {
                    Id = guideSourceId,
                    Name = "Guide",
                    Adapter = Adapter("guide"),
                    Subscription = new PhysicalDeviceSubscription
                    {
                        Mode = PhysicalDeviceSubscriptionMode.Selected,
                        SelectedDeviceIds = [physicalId]
                    }
                }
            ],
            GuideOutputs =
            [
                new GuideOutputDefinition
                {
                    Id = GuideOutputId.New(),
                    Name = "Output",
                    Key = "output",
                    Adapter = Adapter("xmltv"),
                    VirtualDeviceSelectionMode = GuideOutputSelectionMode.Selected,
                    SelectedVirtualDeviceIds = [virtualId],
                    GuideSourceSelectionMode = GuideOutputSelectionMode.Selected,
                    SelectedGuideSourceIds = [guideSourceId]
                }
            ]
        };
    }

    private static AdapterConfiguration Adapter(string kind) =>
        new(kind, JsonSerializer.SerializeToElement(new { }));

    private static AdapterSettingDescriptor ConditionalField(string fieldKey, string value) =>
        new()
        {
            Key = "dependent",
            Label = "Dependent",
            VisibleWhen =
            [
                new AdapterSettingCondition
                {
                    FieldKey = fieldKey,
                    Value = value
                }
            ]
        };

    private static AdapterSettingSnapshot Snapshot<T>(string key, T value) =>
        new()
        {
            Key = key,
            Value = JsonSerializer.SerializeToElement(value),
            HasValue = true
        };
}
