using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.GuideOutputs.Json.Plugin;
using Lineup.Web.Services;
using Lineup.GuideOutputs.Xmltv.Plugin;
using System.Text.Json;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies provider-neutral topology editing behavior.
/// </summary>
public sealed class TopologySettingsEditorTests
{
    /// <summary>Verifies Lineup Link virtual forms persist only the token hash.</summary>
    [Fact]
    public void TryBuild_LineupLinkVirtualDevice_DoesNotPersistRawToken()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);
        var mutation = LineupLinkAccessGrantManager.Create(
            new LineupLinkVirtualConfiguration { InstanceId = device.LinkInstanceId },
            "Test access",
            null,
            DateTime.UtcNow);
        var persistedGrant = Assert.Single(mutation.Configuration!.AccessGrants);
        var grant = LineupLinkAccessGrantSettingsModel.Create(persistedGrant, mutation.GeneratedToken);
        device.LinkAccessGrants.Add(grant);
        var rawToken = grant.RawToken;

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);

        // Assert
        Assert.Empty(errors);
        var persistedDevice = configuration.VirtualDevices.Single(item => item.Id == device.Id);
        Assert.Null(persistedDevice.Hosting);
        var persisted = persistedDevice.Adapter.Configuration;
        Assert.DoesNotContain(rawToken, persisted.GetRawText(), StringComparison.Ordinal);
        var savedGrant = Assert.Single(persisted.GetProperty("AccessGrants").EnumerateArray());
        Assert.Equal(LineupLinkToken.Hash(rawToken), savedGrant.GetProperty("TokenHash").GetString());
    }

    /// <summary>Verifies a new Lineup Link virtual device persists with remote access disabled until a grant is created.</summary>
    [Fact]
    public void TryBuild_NewLineupLinkVirtualDevice_PersistsWithoutAccessGrants()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);

        // Assert
        Assert.Empty(errors);
        Assert.Empty(device.LinkAccessGrants);
        var persisted = configuration.VirtualDevices.Single(item => item.Id == device.Id).Adapter.Configuration;
        Assert.Equal(string.Empty, persisted.GetProperty("PublicBaseUrl").GetString());
        Assert.Empty(persisted.GetProperty("AccessGrants").EnumerateArray());
    }

    /// <summary>Verifies dedicated listener and worker policies round-trip without remaining in adapter-owned settings.</summary>
    [Fact]
    public void TryBuild_DedicatedLineupLinkListener_PreservesHostOwnedPolicies()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);
        device.UseDedicatedListener = true;
        device.LinkRequireEncryption = true;
        device.LinkCertificateSha256 = new string('A', 64);
        device.ListenerTransport = VirtualOutputTransport.Http;
        device.ListenerAddresses = "127.0.0.1, ::1";
        device.ListenerPort = 8765;
        device.OverridePublicBaseUri = true;
        device.PublicBaseUri = "http://lineup.example:8765/";
        device.AllowedNetworks = "192.0.2.0/24";
        device.MaximumConnections = 12;
        device.MaximumRestarts = 2;
        device.MaximumMemoryMib = 512;

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);
        var roundTrip = editor.Edit(configuration);

        // Assert
        Assert.Empty(errors);
        var persisted = configuration.VirtualDevices.Single(item => item.Id == device.Id);
        var hosting = Assert.IsType<VirtualOutputHostingDefinition>(persisted.Hosting);
        var binding = Assert.Single(hosting.Bindings);
        Assert.Equal(8765, binding.Port);
        Assert.Equal(["127.0.0.1", "::1"], binding.Addresses);
        var security = Assert.Single(
            configuration.VirtualOutputSecurityPolicies,
            policy => string.Equals(policy.Key, hosting.Bindings[0].SecurityPolicyKey, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["192.0.2.0/24"], security.AllowedNetworks);
        Assert.Equal(12, security.MaximumConnections);
        var worker = Assert.Single(
            configuration.VirtualOutputWorkerPolicies,
            policy => string.Equals(policy.Key, hosting.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, worker.MaximumRestarts);
        Assert.Equal(512, worker.MaximumMemoryMib);
        Assert.False(persisted.Adapter.Configuration.GetProperty("RequireEncryption").GetBoolean());
        Assert.Equal(JsonValueKind.Null, persisted.Adapter.Configuration.GetProperty("CertificateSha256").ValueKind);
        var rebuilt = roundTrip.VirtualDevices.Single(item => item.Id == device.Id);
        Assert.True(rebuilt.UseDedicatedListener);
        Assert.Equal("http://lineup.example:8765/", rebuilt.PublicBaseUri);
    }

    /// <summary>Verifies a dedicated HTTPS listener makes Lineup Link require encryption independently of the management-host setting.</summary>
    [Fact]
    public void TryBuild_DedicatedHttpsLineupLinkListener_DerivesEncryptionRequirement()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);
        device.UseDedicatedListener = true;
        device.LinkRequireEncryption = false;
        device.ListenerTransport = VirtualOutputTransport.Https;
        device.ListenerAddresses = "192.0.2.10";
        device.ListenerPort = 8765;
        device.CertificateReference = "lineup-link.pfx";

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);

        // Assert
        Assert.Empty(errors);
        var persisted = configuration.VirtualDevices.Single(item => item.Id == device.Id);
        Assert.True(persisted.Adapter.Configuration.GetProperty("RequireEncryption").GetBoolean());
        Assert.Equal("https://192.0.2.10:8765/", persisted.Adapter.Configuration.GetProperty("PublicBaseUrl").GetString());
    }

    /// <summary>Verifies reverse-proxy TLS termination keeps the listener on HTTP while requiring HTTPS from clients.</summary>
    [Fact]
    public void TryBuild_DedicatedHttpLineupLinkListenerWithHttpsPublicUri_RequiresEncryption()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);
        device.UseDedicatedListener = true;
        device.ListenerTransport = VirtualOutputTransport.Http;
        device.ListenerAddresses = "0.0.0.0";
        device.ListenerPort = 8765;
        device.OverridePublicBaseUri = true;
        device.PublicBaseUri = "https://lineup.example/";
        device.AllowForwardedHeaders = true;
        device.TrustedProxies = "192.0.2.20";

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);

        // Assert
        Assert.Empty(errors);
        var persisted = configuration.VirtualDevices.Single(item => item.Id == device.Id);
        var binding = Assert.Single(Assert.IsType<VirtualOutputHostingDefinition>(persisted.Hosting).Bindings);
        Assert.Equal(VirtualOutputTransport.Http, binding.Transport);
        Assert.Equal("https://lineup.example/", binding.PublicBaseUri);
        Assert.True(persisted.Adapter.Configuration.GetProperty("RequireEncryption").GetBoolean());
        Assert.Equal("https://lineup.example/", persisted.Adapter.Configuration.GetProperty("PublicBaseUrl").GetString());
    }

    /// <summary>Verifies one virtual output can advertise a Docker-published port that differs from its internal listener.</summary>
    [Fact]
    public void TryBuild_DedicatedLineupLinkListener_PublishedPortOverridesPublicUriOnly()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);
        device.UseDedicatedListener = true;
        device.LinkRequireEncryption = false;
        device.ListenerTransport = VirtualOutputTransport.Http;
        device.ListenerAddresses = "192.0.2.10";
        var listenerPort = device.ListenerPort;
        device.PublishedPort = 9600;

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);
        var roundTrip = editor.Edit(configuration);

        // Assert
        Assert.Empty(errors);
        var persisted = configuration.VirtualDevices.Single(item => item.Id == device.Id);
        var binding = Assert.Single(Assert.IsType<VirtualOutputHostingDefinition>(persisted.Hosting).Bindings);
        Assert.Equal(listenerPort, binding.Port);
        Assert.Equal("http://192.0.2.10:9600/", binding.PublicBaseUri);
        Assert.Equal("http://192.0.2.10:9600/", persisted.Adapter.Configuration.GetProperty("PublicBaseUrl").GetString());
        var rebuilt = roundTrip.VirtualDevices.Single(item => item.Id == device.Id);
        Assert.Equal(listenerPort, rebuilt.ListenerPort);
        Assert.Equal(9600, rebuilt.PublishedPort);
        Assert.False(rebuilt.OverridePublicBaseUri);
    }

    /// <summary>Verifies a published-port override must be a valid TCP port.</summary>
    [Fact]
    public void TryBuild_DedicatedListener_InvalidPublishedPort_ReturnsValidationError()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var driver = new LineupLinkVirtualTunerDriver();
        var device = editor.AddVirtualDevice(model, driver);
        device.UseDedicatedListener = true;
        device.LinkRequireEncryption = false;
        device.ListenerPort = 8600;
        device.PublishedPort = 65_536;
        device.PublicBaseUri = "http://lineup.example:8600/";

        // Act
        var errors = editor.TryBuild(
            model,
            out _,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);

        // Assert
        Assert.Contains(errors, error => error.Contains("published port must be between 1 and 65535", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies add/remove operations preserve the built-in player and clean selected subscriptions.
    /// </summary>
    [Fact]
    public void AddAndRemoveDevices_SelectedSubscription_RemovesStalePhysicalIdentity()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        editor.AddVirtualDevice(model, new HdHomeRunVirtualTunerDriver());
        editor.AddGuideSource(model, new TestGuideSourceDriver());
        var physicalId = model.PhysicalDevices[0].Id;
        var virtualDevice = model.VirtualDevices.Single(device => !device.IsWebPlayer);
        virtualDevice.SubscriptionMode = PhysicalDeviceSubscriptionMode.Selected;
        virtualDevice.SelectedDeviceIds.Add(physicalId);
        var guideSource = Assert.Single(model.GuideSources);
        guideSource.SubscriptionMode = PhysicalDeviceSubscriptionMode.Selected;
        guideSource.SelectedDeviceIds.Add(physicalId);

        // Act
        editor.RemovePhysicalDevice(model, physicalId);
        editor.RemoveVirtualDevice(model, virtualDevice.Id);
        editor.RemoveVirtualDevice(model, model.VirtualDevices.Single().Id);

        // Assert
        Assert.Empty(model.PhysicalDevices);
        Assert.Single(model.VirtualDevices);
        Assert.True(model.VirtualDevices[0].IsWebPlayer);
        Assert.Empty(virtualDevice.SelectedDeviceIds);
        Assert.Empty(guideSource.SelectedDeviceIds);
    }

    /// <summary>Verifies legacy Web configurations without an advertisement flag opt into discovery and persist the selection.</summary>
    [Fact]
    public void TryBuild_HdHomeRunWithoutAdvertisementFlag_EnablesAndPersistsAdvertisement()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var driver = new HdHomeRunVirtualTunerDriver();
        var virtualDeviceId = VirtualDeviceId.New();
        var configuration = TopologySettingsEditor.CreateDefault() with
        {
            VirtualDevices =
            [
                .. TopologySettingsEditor.CreateDefault().VirtualDevices,
                new VirtualDeviceDefinition
                {
                    Id = virtualDeviceId,
                    Name = "HDHomeRun",
                    Adapter = new AdapterConfiguration(
                        driver.Kind,
                        JsonSerializer.SerializeToElement(new
                        {
                            DeviceId = HdHomeRunProxyIdentity.CreateDeviceId(virtualDeviceId.Value.ToString("N")),
                            AdvertisedBaseUrl = "http://lineup.example:8080/"
                        }))
                }
            ]
        };
        var model = editor.Edit(configuration);
        var device = Assert.Single(model.VirtualDevices, candidate => candidate.Id == virtualDeviceId);

        // Act
        var errors = editor.TryBuild(
            model,
            out var rebuilt,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);

        // Assert
        Assert.Empty(errors);
        Assert.True(device.EnableNetworkAdvertisement);
        var persisted = Assert.Single(rebuilt.VirtualDevices, candidate => candidate.Id == virtualDeviceId);
        Assert.True(persisted.Adapter.Configuration.GetProperty("EnableNetworkAdvertisement").GetBoolean());
    }

    /// <summary>Verifies disabling one virtual HDHomeRun advertisement survives a settings round trip.</summary>
    [Fact]
    public void TryBuild_HdHomeRunAdvertisementDisabled_PreservesSelection()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var driver = new HdHomeRunVirtualTunerDriver();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var device = editor.AddVirtualDevice(model, driver);
        device.EnableNetworkAdvertisement = false;

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            virtualTunerDrivers: [new WebPlayerVirtualTunerDriver(), driver]);
        var roundTrip = editor.Edit(configuration);

        // Assert
        Assert.Empty(errors);
        var persisted = Assert.Single(configuration.VirtualDevices, candidate => candidate.Id == device.Id);
        Assert.False(persisted.Adapter.Configuration.GetProperty("EnableNetworkAdvertisement").GetBoolean());
        Assert.False(Assert.Single(roundTrip.VirtualDevices, candidate => candidate.Id == device.Id).EnableNetworkAdvertisement);
    }

    /// <summary>
    /// Verifies guide sources round-trip independently with stable names and subscription scopes.
    /// </summary>
    [Fact]
    public void GuideSource_RoundTrip_PreservesIdentityAndSelectedDevices()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        editor.AddGuideSource(model, new TestGuideSourceDriver());
        var source = Assert.Single(model.GuideSources);
        source.Name = "Metro Guide";
        source.SubscriptionMode = PhysicalDeviceSubscriptionMode.Selected;
        source.SelectedDeviceIds.Add(model.PhysicalDevices[0].Id);

        // Act
        var errors = editor.TryBuild(model, out var configuration, [new TestGuideSourceDriver()]);
        var roundTrip = editor.Edit(configuration);

        // Assert
        Assert.Empty(errors);
        var rebuilt = Assert.Single(roundTrip.GuideSources);
        Assert.Equal(source.Id, rebuilt.Id);
        Assert.Equal("Metro Guide", rebuilt.Name);
        Assert.Equal(PhysicalDeviceSubscriptionMode.Selected, rebuilt.SubscriptionMode);
        Assert.Equal(model.PhysicalDevices[0].Id, Assert.Single(rebuilt.SelectedDeviceIds));
    }

    /// <summary>
    /// Verifies physical and virtual sparse overrides retain inherited effective values.
    /// </summary>
    [Fact]
    public void Edit_SparseOverrides_DisplaysEffectiveInheritedDefaults()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        model.KeepWarmPeriodSeconds = 15;
        model.PhysicalDefaults.MaximumVideoBitRateMbps = 18;
        model.VirtualDefaults.Audio = OutputAudioPolicy.Eac3;
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        model.PhysicalDevices[0].Address = "tuner.local";

        // Act
        var errors = editor.TryBuild(model, out var configuration);
        var roundTrip = editor.Edit(configuration);

        // Assert
        Assert.Empty(errors);
        Assert.Equal(15, roundTrip.KeepWarmPeriodSeconds);
        Assert.False(roundTrip.PhysicalDevices[0].OverrideMaximumVideoBitRate);
        Assert.Equal(18, roundTrip.PhysicalDevices[0].MaximumVideoBitRateMbps);
        Assert.False(roundTrip.VirtualDevices[0].OverrideAudio);
        Assert.Equal(OutputAudioPolicy.Eac3, roundTrip.VirtualDevices[0].Audio);
    }

    /// <summary>
    /// Verifies a new physical device begins with the current global values while retaining sparse inheritance.
    /// </summary>
    [Fact]
    public void AddPhysicalDevice_GlobalSettings_InitializesInheritedValues()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        model.PhysicalDefaults.ConvertHevcToH264 = true;
        model.PhysicalDefaults.Audio = OutputAudioPolicy.Ac3;
        model.PhysicalDefaults.Ac4Audio = OutputAudioPolicy.Eac3;
        model.PhysicalDefaults.Deinterlace = SourceDeinterlacePolicy.SourceFieldRate;
        model.PhysicalDefaults.VideoPreset = VideoEncoderPreset.Fast;
        model.PhysicalDefaults.VideoQuality = 18;
        model.PhysicalDefaults.MaximumVideoBitRateMbps = 24;

        // Act
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());

        // Assert
        var device = Assert.Single(model.PhysicalDevices);
        Assert.False(device.OverrideConvertHevcToH264);
        Assert.True(device.ConvertHevcToH264);
        Assert.False(device.OverrideDeinterlace);
        Assert.Equal(SourceDeinterlacePolicy.SourceFieldRate, device.Deinterlace);
        Assert.False(device.OverrideAudio);
        Assert.Equal(OutputAudioPolicy.Ac3, device.Audio);
        Assert.False(device.OverrideAc4Audio);
        Assert.Equal(OutputAudioPolicy.Eac3, device.Ac4Audio);
        Assert.False(device.OverrideVideoPreset);
        Assert.Equal(VideoEncoderPreset.Fast, device.VideoPreset);
        Assert.False(device.OverrideVideoQuality);
        Assert.Equal(18, device.VideoQuality);
        Assert.False(device.OverrideMaximumVideoBitRate);
        Assert.Equal(24, device.MaximumVideoBitRateMbps);
    }

    /// <summary>
    /// Verifies the local HDHomeRun hostname is assigned only when another physical device does not already use it.
    /// </summary>
    [Fact]
    public void AddPhysicalDevice_DefaultAddress_AssignsLocalHostnameOnlyOnce()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());

        // Act
        var driver = new TestPhysicalTunerDriver();
        editor.AddPhysicalDevice(model, driver);
        editor.AddPhysicalDevice(model, driver);

        // Assert
        Assert.Equal("hdhomerun.local", model.PhysicalDevices[0].Address);
        Assert.Equal(string.Empty, model.PhysicalDevices[1].Address);
    }

    /// <summary>
    /// Verifies physical adapter configuration used for probing matches the configuration persisted by Save.
    /// </summary>
    [Fact]
    public void BuildPhysicalAdapter_CurrentFormValues_MatchesPersistedConfiguration()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var device = editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        device.Address = "unsaved-device.local";

        // Act
        var probeConfiguration = editor.BuildPhysicalAdapter(device);
        var errors = editor.TryBuild(model, out var persisted);
        var savedConfiguration = Assert.Single(persisted.PhysicalDevices).Adapter;

        // Assert
        Assert.Empty(errors);
        Assert.Equal(savedConfiguration.Kind, probeConfiguration.Kind);
        Assert.Equal(savedConfiguration.Configuration.GetRawText(), probeConfiguration.Configuration.GetRawText());
        Assert.Equal("unsaved-device.local", probeConfiguration.Configuration.GetProperty("Address").GetString());
    }

    /// <summary>
    /// Verifies adapter-owned settings survive editing for future virtual tuner types.
    /// </summary>
    [Fact]
    public void TryBuild_ExtensibleVirtualDevice_PreservesAdapterConfiguration()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var m3uId = VirtualDeviceId.New();
        var configuration = TopologySettingsEditor.CreateDefault() with
        {
            VirtualDevices =
            [
                .. TopologySettingsEditor.CreateDefault().VirtualDevices,
                new VirtualDeviceDefinition
                {
                    Id = m3uId,
                    Name = "M3U Provider 1",
                    Adapter = new AdapterConfiguration("m3u", JsonSerializer.SerializeToElement(new { PlaylistName = "Lineup" }))
                }
            ]
        };
        var model = editor.Edit(configuration);

        // Act
        var errors = editor.TryBuild(model, out var rebuilt);

        // Assert
        Assert.Empty(errors);
        var device = Assert.Single(rebuilt.VirtualDevices, candidate => candidate.Id == m3uId);
        Assert.Equal("m3u", device.Adapter.Kind);
        Assert.Equal("Lineup", device.Adapter.Configuration.GetProperty("PlaylistName").GetString());
    }

    /// <summary>
    /// Verifies adapter-owned settings survive editing for future physical tuner types.
    /// </summary>
    [Fact]
    public void TryBuild_ExtensiblePhysicalDevice_PreservesAdapterConfiguration()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var m3uId = PhysicalDeviceId.New();
        var configuration = TopologySettingsEditor.CreateDefault() with
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = m3uId,
                    Name = "M3U Source 1",
                    Adapter = new AdapterConfiguration("m3u", JsonSerializer.SerializeToElement(new { PlaylistUrl = "https://example.test/lineup.m3u" }))
                }
            ]
        };
        var model = editor.Edit(configuration);

        // Act
        var errors = editor.TryBuild(model, out var rebuilt);

        // Assert
        Assert.Empty(errors);
        var device = Assert.Single(rebuilt.PhysicalDevices);
        Assert.Equal("m3u", device.Adapter.Kind);
        Assert.Equal("https://example.test/lineup.m3u", device.Adapter.Configuration.GetProperty("PlaylistUrl").GetString());
    }

    /// <summary>Verifies portable settings for a non-XMLTV Guide Output are applied through its plugin.</summary>
    [Fact]
    public void TryBuild_JsonGuideOutput_AppliesPortableSettings()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var driver = new JsonGuideOutputDriver();
        var model = editor.Edit(
            TopologySettingsEditor.CreateDefault(),
            guideOutputDrivers: [new XmltvGuideOutputDriver(), driver]);
        var output = editor.AddGuideOutput(model, driver, Path.GetFullPath("guide-outputs"));
        var setting = Assert.Single(output.AdapterSettings);
        var changedDirectory = Path.GetFullPath("changed-guide-outputs");
        setting.Value = JsonSerializer.SerializeToElement(changedDirectory);
        setting.HasValue = true;
        setting.Action = AdapterSettingEditAction.Set;

        // Act
        var errors = editor.TryBuild(
            model,
            out var configuration,
            guideOutputDrivers: [new XmltvGuideOutputDriver(), driver]);

        // Assert
        Assert.Empty(errors);
        var persisted = configuration.GuideOutputs.Single(candidate => candidate.Id == output.Id);
        Assert.Equal(JsonGuideOutputDriver.AdapterKind, persisted.Adapter.Kind);
        Assert.Equal(
            Path.Combine(changedDirectory, "lineup-json_1.json"),
            driver.ResolveDestination(persisted.Adapter).Path);
    }

    /// <summary>
    /// Verifies invalid updates and unknown selected physical devices are rejected together.
    /// </summary>
    [Fact]
    public void TryBuild_InvalidDeviceAndSubscription_ReturnsAllValidationErrors()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        editor.AddPhysicalDevice(model, new TestPhysicalTunerDriver());
        model.PhysicalDevices[0].Name = "";
        model.PhysicalDevices[0].Address = "";
        var player = model.VirtualDevices[0];
        player.SubscriptionMode = PhysicalDeviceSubscriptionMode.Selected;
        player.SelectedDeviceIds.Add(PhysicalDeviceId.New());

        // Act
        var errors = editor.TryBuild(model, out _);

        // Assert
        Assert.Contains(errors, error => error.Contains("requires a name", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("requires an address", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown physical device", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies dedicated output listeners cannot reuse a management UI listener port.
    /// </summary>
    [Fact]
    public void TryBuild_ManagementListenerPortConflict_ReturnsValidationError()
    {
        // Arrange
        var editor = new TopologySettingsEditor();
        var model = editor.Edit(TopologySettingsEditor.CreateDefault());
        var player = Assert.Single(model.VirtualDevices);
        player.UseDedicatedListener = true;
        player.ListenerPort = 8080;

        // Act
        var errors = editor.TryBuild(model, out _, managementListenerPorts: [8080, 8443]);

        // Assert
        Assert.Contains(errors, error => error.Contains("port 8080 conflicts with a Lineup management UI listener", StringComparison.Ordinal));
    }

}
