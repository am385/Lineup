using System.Net;
using System.Text;
using System.Text.Json;
using Lineup.Core.Common.Configuration;
using Lineup.Core.Extensibility.Configuration;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Extensibility.Streaming;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Models;
using Lineup.Plugin.SiliconDust.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.Plugin.SiliconDust.HDHomeRun.PhysicalTuner.Tests;

/// <summary>
/// Tests the physical HDHomeRun adapter.
/// </summary>
public sealed class HDHomeRunPhysicalTunerDriverTests
{
    /// <summary>Gets accepted address forms and operations that initialize native control.</summary>
    public static TheoryData<string, string, string, string> NativeControlAddresses
    {
        get
        {
            var addresses = new (string Address, string Host, string BaseAddress)[]
            {
                ("device.local", "device.local", "http://device.local/"),
                ("127.0.0.1", "127.0.0.1", "http://127.0.0.1/"),
                ("  device.local  ", "device.local", "http://device.local/"),
                ("device.local:8080", "device.local", "http://device.local:8080/"),
                ("http://DEVICE.LOCAL:8080/", "device.local", "http://device.local:8080/"),
                ("  https://127.0.0.1:8443/  ", "127.0.0.1", "https://127.0.0.1:8443/"),
                ("http://[2001:db8::10]:8080/", "2001:db8::10", "http://[2001:db8::10]:8080/"),
                ("[2001:db8::10]", "2001:db8::10", "http://[2001:db8::10]/")
            };
            var data = new TheoryData<string, string, string, string>();
            foreach (var address in addresses)
            {
                foreach (var operation in new[] { "status", "stop", "restart" })
                {
                    data.Add(address.Address, address.Host, address.BaseAddress, operation);
                }
            }
            return data;
        }
    }

    /// <summary>Verifies native initialization receives only the validated host while HTTP retains the configured endpoint.</summary>
    [Theory]
    [MemberData(nameof(NativeControlAddresses))]
    public async Task NativeControl_UsesHostWithoutChangingHttpEndpointOrSettings(string address, string expectedHost, string expectedBaseAddress, string operation)
    {
        // Arrange
        var requests = new List<Uri>();
        using var handler = new DelegateHttpMessageHandler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(JsonResponse(DiscoverJson(tunerCount: 2)));
        });
        var clients = new StaticHttpClientFactory(handler);
        var controlFactory = new HDHomeRunHttpControlFactory(clients, NullLogger<HDHomeRunHttpControl>.Instance);
        using var service = new RecordingDeviceService(controlFactory);
        var driver = new HDHomeRunPhysicalTunerDriver(clients, NullLoggerFactory.Instance, service);
        var configuration = Configuration(address);
        var deviceId = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(deviceId, "Living room", configuration, TestContext.Current.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeNativeControlAsync(device, operation, TestContext.Current.CancellationToken));

        // Assert
        var lookup = Assert.Single(service.Lookups);
        Assert.Equal(expectedHost, lookup.Address);
        Assert.Equal(TestContext.Current.CancellationToken, lookup.Token);
        Assert.Same(Assert.IsType<HDHomeRunPhysicalTunerDevice>(device).DeviceInfo, lookup.Info);
        Assert.Equal(new Uri(new Uri(expectedBaseAddress), "discover.json"), Assert.Single(requests));
        Assert.Equal(address, driver.GetSettings(configuration).Values.Single(value => value.Key == "address").Value?.GetString());
        Assert.Equal($"HDHomeRun device {deviceId} is unavailable through the native control protocol.", exception.Message);
    }

    /// <summary>Verifies native lookup cancellation propagates and releases initialization for later attempts.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("restart")]
    public async Task NativeControl_CanceledLookup_PreservesCancellationAndAllowsRetry(string operation)
    {
        // Arrange
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new DelegateHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(DiscoverJson(tunerCount: 2))));
        var clients = new StaticHttpClientFactory(handler);
        var controlFactory = new HDHomeRunHttpControlFactory(clients, NullLogger<HDHomeRunHttpControl>.Instance);
        using var service = new RecordingDeviceService(controlFactory) { CancelLookup = cancellation };
        var driver = new HDHomeRunPhysicalTunerDriver(clients, NullLoggerFactory.Instance, service);
        var device = await driver.CreateAsync(PhysicalDeviceId.New(), "Living room", Configuration("device.local"), TestContext.Current.CancellationToken);

        // Act
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeNativeControlAsync(device, operation, cancellation.Token));
        service.CancelLookup = null;
        var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeNativeControlAsync(device, operation, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.EndsWith("is unavailable through the native control protocol.", unavailable.Message);
        Assert.Equal(2, service.Lookups.Count);
        Assert.All(service.Lookups, lookup => Assert.Equal("device.local", lookup.Address));
        Assert.Equal(cancellation.Token, service.Lookups[0].Token);
        Assert.Equal(TestContext.Current.CancellationToken, service.Lookups[1].Token);
    }

    /// <summary>Verifies the portable settings descriptor and address edit round trip.</summary>
    [Fact]
    public void Settings_AddressEdit_RoundTripsValidConfiguration()
    {
        // Arrange
        var driver = CreateDriver(_ => throw new InvalidOperationException("HTTP should not be called."));
        var configuration = driver.CreateDefaultConfiguration(PhysicalDeviceId.New());
        var edits = new[]
        {
            new AdapterSettingEdit
            {
                Key = "address",
                Action = AdapterSettingEditAction.Set,
                Value = JsonSerializer.SerializeToElement("  http://device.local:8080/  ")
            }
        };

        // Act
        var descriptorErrors = AdapterSettingsValidator.ValidateDescriptor(driver.SettingsDescriptor);
        var initialSnapshotErrors = AdapterSettingsValidator.ValidateSnapshot(driver.SettingsDescriptor, driver.GetSettings(configuration));
        var result = driver.ApplySettings(configuration, edits);
        var updated = result.Configuration?.Configuration.Deserialize<HDHomeRunPhysicalTunerConfiguration>();

        // Assert
        Assert.Empty(descriptorErrors);
        Assert.Empty(initialSnapshotErrors);
        Assert.True(result.Succeeded);
        Assert.Equal("http://device.local:8080/", updated?.Address);
    }

    /// <summary>Verifies each documented profile round trips and survives independent address edits.</summary>
    [Theory]
    [InlineData("default")]
    [InlineData("heavy")]
    [InlineData("mobile")]
    [InlineData("internet540")]
    [InlineData("internet480")]
    [InlineData("internet360")]
    [InlineData("internet240")]
    public void Settings_Profile_RoundTripsAndPreservesAddress(string profile)
    {
        // Arrange
        var driver = CreateDriver(_ => throw new InvalidOperationException("No HTTP expected."));
        var configuration = Configuration("device");
        var edit = new AdapterSettingEdit { Key = "transcodeProfile", Action = AdapterSettingEditAction.Set, Value = JsonSerializer.SerializeToElement(profile) };

        // Act
        var result = driver.ApplySettings(configuration, [edit]);
        var addressResult = driver.ApplySettings(result.Configuration!,
            [new AdapterSettingEdit { Key = "address", Action = AdapterSettingEditAction.Set, Value = JsonSerializer.SerializeToElement("other-device") }]);
        var snapshot = driver.GetSettings(addressResult.Configuration!);

        // Assert
        Assert.True(result.Succeeded);
        Assert.True(addressResult.Succeeded);
        Assert.Empty(AdapterSettingsValidator.ValidateSnapshot(driver.SettingsDescriptor, snapshot));
        Assert.Equal(profile, snapshot.Values.Single(value => value.Key == "transcodeProfile").Value?.GetString());
        Assert.Equal("other-device", snapshot.Values.Single(value => value.Key == "address").Value?.GetString());
        var field = driver.SettingsDescriptor.Fields.Single(value => value.Key == "transcodeProfile");
        Assert.Equal(AdapterSettingKind.Choice, field.Kind);
        Assert.Contains(field.Choices, choice => choice.Value == profile);
    }

    /// <summary>Verifies arbitrary HTTP parameters and undocumented profiles cannot enter the configuration.</summary>
    [Theory]
    [InlineData("native")]
    [InlineData("")]
    [InlineData("mobile&duration=1")]
    [InlineData("MOBILE")]
    [InlineData(null)]
    public void Settings_InvalidProfile_RejectsEditsAndPersistedConfiguration(string? profile)
    {
        // Arrange
        var driver = CreateDriver(_ => throw new InvalidOperationException("No HTTP expected."));
        var configuration = Configuration("device");
        var invalid = Configuration(HDHomeRunPhysicalTunerDriver.DriverKind, new HDHomeRunPhysicalTunerConfiguration { Address = "device", TranscodeProfile = profile! });

        // Act
        var result = driver.ApplySettings(configuration,
            [new AdapterSettingEdit { Key = "transcodeProfile", Action = AdapterSettingEditAction.Set, Value = JsonSerializer.SerializeToElement(profile) }]);
        var errors = driver.Validate(invalid);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Contains("transcodeProfile", result.Errors.Keys);
        Assert.Contains(errors, error => error.Contains("TranscodeProfile", StringComparison.Ordinal));
    }

    /// <summary>Verifies omitted profile configuration retains device default behavior.</summary>
    [Fact]
    public void Settings_LegacyConfiguration_PreservesDeviceDefaultAndRejectsClear()
    {
        // Arrange
        var driver = CreateDriver(_ => throw new InvalidOperationException("No HTTP expected."));
        var legacy = new AdapterConfiguration(driver.Kind, JsonSerializer.SerializeToElement(new { Address = "device" }));

        // Act
        var preserved = driver.ApplySettings(legacy, [new AdapterSettingEdit { Key = "transcodeProfile", Action = AdapterSettingEditAction.Preserve }]);
        var cleared = driver.ApplySettings(legacy, [new AdapterSettingEdit { Key = "transcodeProfile", Action = AdapterSettingEditAction.Clear }]);

        // Assert
        Assert.Empty(driver.Validate(legacy));
        Assert.True(preserved.Succeeded);
        Assert.Equal("default", driver.GetSettings(preserved.Configuration!).Values.Single(value => value.Key == "transcodeProfile").Value?.GetString());
        Assert.False(cleared.Succeeded);
    }

    /// <summary>Verifies support follows the documented model allowlist, not the friendly name.</summary>
    [Theory]
    [InlineData("HDTC-2US", true)]
    [InlineData("HDTC-2US-M", true)]
    [InlineData("HDTC-2US-L", true)]
    [InlineData("hdtc-2us-m", true)]
    [InlineData("HDFX-4K", false)]
    [InlineData("HDHR3-CC", false)]
    [InlineData("HDTC-2US-UNKNOWN", false)]
    [InlineData("", false)]
    public async Task Discovery_ModelControlsCapabilityAndExplicitProfileAdmission(string model, bool supported)
    {
        // Arrange
        var discovery = DiscoverJson(1).Replace("\"HDFX\"", JsonSerializer.Serialize(model), StringComparison.Ordinal)
            .Replace("HDHomeRun FLEX", "HDHomeRun EXTEND", StringComparison.Ordinal);
        var driver = CreateDriver(_ => JsonResponse(discovery));
        var configured = Configuration(driver.Kind, new HDHomeRunPhysicalTunerConfiguration { Address = "device", TranscodeProfile = "mobile" });

        // Act
        var device = Assert.IsType<HDHomeRunPhysicalTunerDevice>(
            await driver.CreateAsync(PhysicalDeviceId.New(), "Device", Configuration("device"), TestContext.Current.CancellationToken));
        var information = await device.RefreshDeviceInformationAsync(TestContext.Current.CancellationToken);
        var probe = await driver.ProbeAsync(Configuration("device"), TestContext.Current.CancellationToken);
        var exception = await Record.ExceptionAsync(async () =>
            await driver.CreateAsync(PhysicalDeviceId.New(), "Device", configured, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(supported, device.SupportsHardwareTranscoding);
        Assert.Equal(supported.ToString(), information.Details["supportsHardwareTranscoding"]);
        Assert.Contains(probe.Details, detail => detail.Label == "EXTEND video transcoding" &&
            detail.Value == (supported ? "Supported (audio unchanged)" : "Not supported"));
        Assert.False(driver.Capabilities.SupportsHardwareTranscoding);
        if (supported)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Contains("Select Device default", Assert.IsType<InvalidOperationException>(exception).Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies default requests remain unchanged and explicit profiles safely replace only the transcode query parameter.</summary>
    [Theory]
    [InlineData("default", "?duration=120&token=a%2Bb&transcode=old&%74ranscode=duplicate&x=1&x=2")]
    [InlineData("heavy", "?duration=120&token=a%2Bb&x=1&x=2&transcode=heavy")]
    [InlineData("mobile", "?duration=120&token=a%2Bb&x=1&x=2&transcode=mobile")]
    [InlineData("internet540", "?duration=120&token=a%2Bb&x=1&x=2&transcode=internet540")]
    [InlineData("internet480", "?duration=120&token=a%2Bb&x=1&x=2&transcode=internet480")]
    [InlineData("internet360", "?duration=120&token=a%2Bb&x=1&x=2&transcode=internet360")]
    [InlineData("internet240", "?duration=120&token=a%2Bb&x=1&x=2&transcode=internet240")]
    public async Task OpenStreamAsync_ProfileMergesQueryAndMatchesDiagnostic(string profile, string query)
    {
        // Arrange
        Uri? acquiredUri = null;
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => JsonResponse(DiscoverJson(1).Replace("\"HDFX\"", "\"HDTC-2US-M\"", StringComparison.Ordinal)),
            "/lineup.json" => JsonResponse(
                """[{"GuideNumber":"5.1","GuideName":"NEWS","VideoCodec":"MPEG2","AudioCodec":"AC3","URL":"http://device:5004/auto/v5.1?duration=120&token=a%2Bb&transcode=old&%74ranscode=duplicate&x=1&x=2"}]"""),
            _ => CaptureStream(request.RequestUri)
        });
        HttpResponseMessage CaptureStream(Uri uri)
        {
            acquiredUri = uri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        }
        var configuration = Configuration(driver.Kind, new HDHomeRunPhysicalTunerConfiguration { Address = "device", TranscodeProfile = profile });
        var id = PhysicalDeviceId.New();
        var device = Assert.IsType<HDHomeRunPhysicalTunerDevice>(await driver.CreateAsync(id, "Device", configuration, TestContext.Current.CancellationToken));

        // Act
        var channels = await device.RefreshLineupAsync(TestContext.Current.CancellationToken);
        var diagnostic = device.CreateDiagnosticUri("5.1");
        await using var lease = await device.OpenStreamAsync(new PhysicalStreamRequest { ChannelId = new SourceChannelId(id, "5.1") }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new Uri("http://device:5004/auto/v5.1" + query), acquiredUri);
        Assert.Equal(acquiredUri, diagnostic);
        Assert.Equal("MPEG2", Assert.Single(channels).Metadata["videoCodec"]);
        Assert.Equal("AC3", Assert.Single(channels).Metadata["audioCodec"]);
    }

    /// <summary>Verifies explicit profiles work without an existing query, including the diagnostic fallback before lineup refresh.</summary>
    [Fact]
    public async Task OpenStreamAsync_ProfileAddsQueryToUnmodifiedLineupUrl()
    {
        // Arrange
        Uri? acquiredUri = null;
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                return JsonResponse(DiscoverJson(1).Replace("\"HDFX\"", "\"HDTC-2US\"", StringComparison.Ordinal));
            }
            if (request.RequestUri.AbsolutePath == "/lineup.json")
            {
                return JsonResponse("""[{"GuideNumber":"5.1","GuideName":"NEWS","URL":"http://device/auto/v5.1"}]""");
            }
            acquiredUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        });
        var id = PhysicalDeviceId.New();
        var configuration = Configuration(driver.Kind, new HDHomeRunPhysicalTunerConfiguration { Address = "device", TranscodeProfile = "mobile" });
        var device = Assert.IsType<HDHomeRunPhysicalTunerDevice>(await driver.CreateAsync(id, "Device", configuration, TestContext.Current.CancellationToken));

        // Act
        var diagnostic = device.CreateDiagnosticUri("5.1");
        await using var lease = await device.OpenStreamAsync(new PhysicalStreamRequest { ChannelId = new SourceChannelId(id, "5.1") }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new Uri("http://device/auto/v5.1?transcode=mobile"), acquiredUri);
        Assert.Equal(acquiredUri, diagnostic);
    }

    /// <summary>Verifies refreshed discovery can withdraw profile support without sending an unsupported stream request.</summary>
    [Fact]
    public async Task RefreshDeviceInformationAsync_ModelChanged_WithdrawsTranscodingSupport()
    {
        // Arrange
        var discoveries = 0;
        var streamRequests = 0;
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                return JsonResponse(++discoveries == 1
                    ? DiscoverJson(1).Replace("\"HDFX\"", "\"HDTC-2US\"", StringComparison.Ordinal)
                    : DiscoverJson(1));
            }
            if (request.RequestUri.AbsolutePath == "/lineup.json")
            {
                return JsonResponse("""[{"GuideNumber":"5.1","GuideName":"NEWS","URL":"http://device/auto/v5.1"}]""");
            }
            streamRequests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        });
        var id = PhysicalDeviceId.New();
        var configuration = Configuration(driver.Kind, new HDHomeRunPhysicalTunerConfiguration { Address = "device", TranscodeProfile = "mobile" });
        var device = Assert.IsType<HDHomeRunPhysicalTunerDevice>(await driver.CreateAsync(id, "Device", configuration, TestContext.Current.CancellationToken));

        // Act
        var information = await device.RefreshDeviceInformationAsync(TestContext.Current.CancellationToken);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await device.OpenStreamAsync(new PhysicalStreamRequest { ChannelId = new SourceChannelId(id, "5.1") }, TestContext.Current.CancellationToken));

        // Assert
        Assert.False(device.SupportsHardwareTranscoding);
        Assert.Equal("False", information.Details["supportsHardwareTranscoding"]);
        Assert.Contains("Select Device default", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, streamRequests);
    }

    /// <summary>
    /// Verifies driver identity, capabilities, and typed configuration validation.
    /// </summary>
    [Fact]
    public void Validate_ReportsInvalidKindAndAddress()
    {
        // Arrange
        var driver = CreateDriver(_ => throw new InvalidOperationException("HTTP should not be called."));
        var configuration = Configuration("other", new HDHomeRunPhysicalTunerConfiguration { Address = "ftp://device/path" });

        // Act
        var errors = driver.Validate(configuration);
        var defaultConfiguration = driver.CreateDefaultConfiguration(PhysicalDeviceId.New());

        // Assert
        Assert.Equal(HDHomeRunPhysicalTunerDriver.DriverKind, driver.Kind);
        Assert.Equal("HDHomeRun", driver.DisplayName);
        Assert.True(driver.SupportsUserCreation);
        Assert.Equal(HDHomeRunPhysicalTunerDriver.DriverKind, defaultConfiguration.Kind);
        Assert.Equal(HDHomeRunPhysicalTunerDriver.DefaultAddress, defaultConfiguration.Configuration.GetProperty("Address").GetString());
        Assert.True(driver.Capabilities.HasFiniteCapacity);
        Assert.True(driver.Capabilities.SupportsTunerStatus);
        Assert.True(driver.Capabilities.SupportsTunerControl);
        Assert.False(driver.Capabilities.SupportsHardwareTranscoding);
        Assert.Equal(2, errors.Count);
    }

    /// <summary>
    /// Verifies discovery populates runtime metadata and lineup channels use source-scoped stable keys.
    /// </summary>
    [Fact]
    public async Task CreateAndRefreshLineup_TranslatesDiscoveryAndChannels()
    {
        // Arrange
        var physicalDeviceId = PhysicalDeviceId.New();
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => JsonResponse(DiscoverJson(tunerCount: 2)),
            "/lineup.json" => JsonResponse(
                """
                [{"GuideNumber":"7.1","GuideName":"WXYZ-HD","VideoCodec":"HEVC","AudioCodec":"AC4","HD":1,"Favorite":1,"URL":"http://device/auto/v7.1"}]
                """),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });

        // Act
        var device = await driver.CreateAsync(physicalDeviceId, "Living room", Configuration("device.local"), TestContext.Current.CancellationToken);
        var channels = await device.RefreshLineupAsync(TestContext.Current.CancellationToken);

        // Assert
        var concreteDevice = Assert.IsType<HDHomeRunPhysicalTunerDevice>(device);
        Assert.Equal("ABCDEF01", concreteDevice.DeviceInfo.DeviceID);
        Assert.Equal(2, device.Snapshot.Capacity.TunerCount);
        Assert.Equal("Living room", device.Snapshot.Name);
        var channel = Assert.Single(channels);
        Assert.Equal(new SourceChannelId(physicalDeviceId, "7.1"), channel.Id);
        Assert.Equal("7.1", channel.ProviderNumber);
        Assert.Equal("WXYZ-HD", channel.ProviderName);
        Assert.Equal("HEVC", channel.Metadata["videoCodec"]);
        Assert.Equal("True", channel.Metadata["hd"]);
    }

    /// <summary>
    /// Verifies a connection probe returns safe discovery identity without refreshing the lineup.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_DiscoveryResponse_ReturnsIdentityAndSuggestedName()
    {
        // Arrange
        var lineupRequests = 0;
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => JsonResponse(DiscoverJson(tunerCount: 4)),
            "/lineup.json" => CountedJsonResponse(ref lineupRequests, "[]"),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });

        // Act
        var result = await ((IPhysicalDeviceProbe)driver).ProbeAsync(Configuration("device.local"), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("HDHomeRun FLEX (ABCDEF01)", result.SuggestedName);
        Assert.Contains(result.Details, detail => detail.Label == "Model" && detail.Value == "HDFX");
        Assert.Contains(result.Details, detail => detail.Label == "Device ID" && detail.Value == "ABCDEF01");
        Assert.Contains(result.Details, detail => detail.Label == "Tuners" && detail.Value == "4");
        Assert.Equal(0, lineupRequests);
    }

    /// <summary>Receiver observations retain the actual provider channel identity for signal association, independently of tuner index.</summary>
    [Fact]
    public async Task RefreshStatusAsync_ActiveReceiver_IdentifiesExactSourceChannel()
    {
        // Arrange
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => JsonResponse(DiscoverJson(tunerCount: 4)),
            "/status.json" => JsonResponse("""
                [{"Resource":"tuner3","VctNumber":"5.1","VctName":"Example","Frequency":177000000,"TargetIP":"192.0.2.1","SignalStrengthPercent":99}]
                """),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });
        var id = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(id, "Source", Configuration("127.0.0.1"), TestContext.Current.CancellationToken);

        // Act
        var status = await Assert.IsAssignableFrom<IPhysicalDeviceStatusCapability>(device).RefreshStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new SourceChannelId(id, "5.1"), status.Receivers[3].SourceChannelId);
        Assert.Equal(99, status.Receivers[3].SignalStrength);
        Assert.All(status.Receivers.Take(3), receiver => Assert.Null(receiver.SourceChannelId));
    }

    /// <summary>
    /// Verifies recurring tuner-status refreshes reuse creation metadata and the initialized protocol device.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://127.0.0.1:8443/")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("  127.0.0.1  ")]
    public async Task RefreshStatusAsync_RecurringRefresh_DoesNotRediscoverDevice(string address)
    {
        // Arrange
        var discoveryRequestCount = 0;
        var statusRequestCount = 0;
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => CountedJsonResponse(ref discoveryRequestCount, DiscoverJson(tunerCount: 2)),
            "/status.json" => CountedJsonResponse(ref statusRequestCount, "[]"),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });
        var device = await driver.CreateAsync(PhysicalDeviceId.New(), "Living room", Configuration(address), TestContext.Current.CancellationToken);
        var statusCapability = Assert.IsAssignableFrom<IPhysicalDeviceStatusCapability>(device);

        // Act
        var first = await statusCapability.RefreshStatusAsync(TestContext.Current.CancellationToken);
        var discoveryRequestsAfterFirstRefresh = discoveryRequestCount;
        var second = await statusCapability.RefreshStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(discoveryRequestsAfterFirstRefresh, discoveryRequestCount);
        Assert.Equal(1, discoveryRequestCount);
        Assert.Equal(4, statusRequestCount);
        Assert.Equal(2, first.Receivers.Count);
        Assert.Equal(2, second.Receivers.Count);
        Assert.Equal("ABCDEF01", second.Details["deviceId"]);
    }

    /// <summary>
    /// Verifies device information refresh explicitly re-reads discovery metadata without polling tuner status.
    /// </summary>
    [Fact]
    public async Task RefreshDeviceInformationAsync_RefreshesDiscoveryMetadataOnly()
    {
        // Arrange
        var discoveryRequestCount = 0;
        var statusRequestCount = 0;
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => CountedJsonResponse(
                ref discoveryRequestCount,
                DiscoverJson(tunerCount: 2, firmwareVersion: discoveryRequestCount == 0 ? "20260901" : "20261001")),
            "/status.json" => CountedJsonResponse(ref statusRequestCount, "[]"),
            _ => throw new InvalidOperationException($"Unexpected request {request.RequestUri}.")
        });
        var device = await driver.CreateAsync(PhysicalDeviceId.New(), "Living room", Configuration("127.0.0.1"), TestContext.Current.CancellationToken);
        var informationCapability = Assert.IsAssignableFrom<IPhysicalDeviceInformationCapability>(device);

        // Act
        var refreshed = await informationCapability.RefreshDeviceInformationAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, discoveryRequestCount);
        Assert.Equal(0, statusRequestCount);
        Assert.Equal("20261001", refreshed.Details["firmwareVersion"]);
        Assert.Equal("20261001", Assert.IsType<HDHomeRunPhysicalTunerDevice>(device).DeviceInfo.FirmwareVersion);
    }

    /// <summary>Verifies explicit endpoint refresh rebuilds control from the refreshed metadata without another discovery request.</summary>
    [Fact]
    public async Task RefreshStatusAsync_InformationEndpointChanged_ReusesRefreshedDiscovery()
    {
        // Arrange
        var discoveryRequestCount = 0;
        var statusRequests = new List<Uri>();
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                var baseUrl = discoveryRequestCount == 0 ? "https://original-device:8443" : "http://updated-device:8080";
                return CountedJsonResponse(ref discoveryRequestCount, DiscoverJson(tunerCount: 2, baseUrl: baseUrl));
            }
            Assert.Equal("/status.json", request.RequestUri.AbsolutePath);
            statusRequests.Add(request.RequestUri);
            return JsonResponse("[]");
        });
        var device = await driver.CreateAsync(PhysicalDeviceId.New(), "Living room", Configuration("127.0.0.1"), TestContext.Current.CancellationToken);
        var status = Assert.IsAssignableFrom<IPhysicalDeviceStatusCapability>(device);
        var information = Assert.IsAssignableFrom<IPhysicalDeviceInformationCapability>(device);

        // Act
        await status.RefreshStatusAsync(TestContext.Current.CancellationToken);
        await information.RefreshDeviceInformationAsync(TestContext.Current.CancellationToken);
        await status.RefreshStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, discoveryRequestCount);
        Assert.Equal(
            [new Uri("https://original-device:8443/status.json"), new Uri("https://original-device:8443/status.json"),
             new Uri("http://updated-device:8080/status.json"), new Uri("http://updated-device:8080/status.json")],
            statusRequests);
    }

    /// <summary>
    /// Verifies finite tuner capacity is released when a stream lease is disposed.
    /// </summary>
    [Fact]
    public async Task OpenStreamAsync_EnforcesCapacityAndReleasesDisposedLease()
    {
        // Arrange
        var streamRequestCount = 0;
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                return JsonResponse(DiscoverJson(tunerCount: 1));
            }

            if (request.RequestUri.AbsolutePath == "/lineup.json")
            {
                return JsonResponse("""[{"GuideNumber":"5.1","GuideName":"NEWS","URL":"http://device/auto/v5.1"}]""");
            }

            Interlocked.Increment(ref streamRequestCount);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
                {
                    Headers = { ContentType = new("video/mp2t") }
                }
            };
        });
        var deviceId = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(deviceId, "Office", Configuration("device"), TestContext.Current.CancellationToken);
        var request = new PhysicalStreamRequest { ChannelId = new SourceChannelId(deviceId, "5.1") };
        await using var firstLease = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var waiting = device.OpenStreamAsync(request, cancellation.Token).AsTask();
        var observed = Record.ExceptionAsync(async () => await waiting).AsTask();
        try
        {
            Assert.False(waiting.IsCompleted);
            Assert.Equal(1, streamRequestCount);
            cancellation.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>(await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        await firstLease.DisposeAsync();
        await using var replacementLease = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("video/mp2t", firstLease.ContentType);
        Assert.Equal(2, streamRequestCount);
        Assert.Equal(1, replacementLease.Stream.ReadByte());
    }

    /// <summary>Verifies response closure precedes replacement admission even when stream disposal fails.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_ClosesResponseBeforeReleasingCapacity(bool throwOnDispose)
    {
        // Arrange
        var responseClosed = false;
        var requests = 0;
        var stream = new DisposalStream(throwOnDispose);
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                return JsonResponse(DiscoverJson(1));
            }
            if (request.RequestUri.AbsolutePath == "/lineup.json")
            {
                return JsonResponse("""[{"GuideNumber":"5.1","GuideName":"NEWS","URL":"http://device/auto/v5.1"}]""");
            }
            if (++requests == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ControlledContent(_ => Task.FromResult<Stream>(stream), () => responseClosed = true)
                };
            }
            Assert.True(responseClosed);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) };
        });
        var id = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(id, "Device", Configuration("device"), TestContext.Current.CancellationToken);
        var request = new PhysicalStreamRequest { ChannelId = new SourceChannelId(id, "5.1") };
        var lease = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);
        var waiting = device.OpenStreamAsync(request, TestContext.Current.CancellationToken).AsTask();

        // Act
        Assert.False(waiting.IsCompleted);
        var exception = await Record.ExceptionAsync(async () => await lease.DisposeAsync());
        await using var replacement = await waiting.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        // Assert
        Assert.True(responseClosed);
        Assert.True(stream.Disposed);
        Assert.Equal(2, requests);
        Assert.Equal(throwOnDispose, exception is IOException);
    }

    /// <summary>Verifies failed or canceled response-stream startup closes its response before readmitting a request.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenStreamAsync_StreamStartupFailureClosesResponseAndReleasesCapacity(bool cancel)
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var responseClosed = false;
        var requests = 0;
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                return JsonResponse(DiscoverJson(1));
            }
            if (request.RequestUri.AbsolutePath == "/lineup.json")
            {
                return JsonResponse("""[{"GuideNumber":"5.1","GuideName":"NEWS","URL":"http://device/auto/v5.1"}]""");
            }
            if (++requests == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ControlledContent(async token =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(token);
                        throw new IOException("Controlled stream startup failure.");
                    }, () => responseClosed = true)
                };
            }
            Assert.True(responseClosed);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([2]) };
        });
        var id = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(id, "Device", Configuration("device"), TestContext.Current.CancellationToken);
        var request = new PhysicalStreamRequest { ChannelId = new SourceChannelId(id, "5.1") };
        var opening = Record.ExceptionAsync(async () => await device.OpenStreamAsync(request, cancellation.Token)).AsTask();

        // Act
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (cancel)
            {
                cancellation.Cancel();
            }
            else
            {
                release.TrySetResult();
            }
            var exception = await opening.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await using var replacement = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);

            // Assert
            Assert.True(responseClosed);
            Assert.Equal(2, requests);
            if (cancel)
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
            }
            else
            {
                Assert.IsType<IOException>(exception);
            }
        }
        finally
        {
            cancellation.Cancel();
            release.TrySetResult();
            await opening.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies a failed stream request returns its tuner slot.
    /// </summary>
    [Fact]
    public async Task OpenStreamAsync_ReleasesCapacityAfterHttpFailure()
    {
        // Arrange
        var streamRequestCount = 0;
        var driver = CreateDriver(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/discover.json")
            {
                return JsonResponse(DiscoverJson(tunerCount: 1));
            }

            if (request.RequestUri.AbsolutePath == "/lineup.json")
            {
                return JsonResponse("""[{"GuideNumber":"9.1","GuideName":"PBS","URL":"http://device/auto/v9.1"}]""");
            }

            return Interlocked.Increment(ref streamRequestCount) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([9]) };
        });
        var deviceId = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(deviceId, "Bedroom", Configuration("http://device:8080"), TestContext.Current.CancellationToken);
        var request = new PhysicalStreamRequest { ChannelId = new SourceChannelId(deviceId, "9.1") };

        // Act
        var exception = await Assert.ThrowsAsync<PhysicalStreamAcquisitionException>(
            async () => await device.OpenStreamAsync(request, TestContext.Current.CancellationToken));
        await using var lease = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(PhysicalStreamFailureCategory.Unavailable, exception.Category);
        Assert.Equal("source_unavailable", exception.Code);
        Assert.True(exception.Retryable);
        Assert.Equal(2, streamRequestCount);
        Assert.Equal(9, lease.Stream.ReadByte());
    }

    /// <summary>Verifies HDHomeRun tuner diagnostics become safe provider-neutral acquisition failures.</summary>
    [Theory]
    [InlineData("811 Content Protection Required", false, PhysicalStreamFailureCategory.ProtectedContent, "protected_content", false)]
    [InlineData("807 No Video Data", false, PhysicalStreamFailureCategory.NoData, "source_no_video_data", true)]
    [InlineData("806 No Signal", true, PhysicalStreamFailureCategory.NoSignal, "source_no_signal", true)]
    [InlineData("805 All Tuners In Use", false, PhysicalStreamFailureCategory.Capacity, "source_capacity_exceeded", true)]
    [InlineData("802 Unknown Transcode Profile", false, PhysicalStreamFailureCategory.InvalidInput, "source_transcode_profile_unsupported", false)]
    [InlineData("802", true, PhysicalStreamFailureCategory.InvalidInput, "source_transcode_profile_unsupported", false)]
    public async Task OpenStreamAsync_TunerDiagnostic_ThrowsStructuredFailure(string diagnostic, bool useResponseBody, PhysicalStreamFailureCategory expectedCategory, string expectedCode, bool expectedRetryable)
    {
        // Arrange
        var driver = CreateDriver(request => request.RequestUri!.AbsolutePath switch
        {
            "/discover.json" => JsonResponse(DiscoverJson(tunerCount: 1)),
            "/lineup.json" => JsonResponse("""[{"GuideNumber":"9.1","GuideName":"PBS","URL":"http://device/auto/v9.1"}]"""),
            _ => ErrorResponse(diagnostic, useResponseBody)
        });
        var deviceId = PhysicalDeviceId.New();
        var device = await driver.CreateAsync(deviceId, "Bedroom", Configuration("device"), TestContext.Current.CancellationToken);
        var request = new PhysicalStreamRequest { ChannelId = new SourceChannelId(deviceId, "9.1") };

        // Act
        var exception = await Assert.ThrowsAsync<PhysicalStreamAcquisitionException>(
            async () => await device.OpenStreamAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(expectedCategory, exception.Category);
        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedRetryable, exception.Retryable);
        Assert.Equal(deviceId, exception.DeviceId);
        Assert.Equal(request.ChannelId, exception.ChannelId);
        var inner = Assert.IsType<HttpRequestException>(exception.InnerException);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, inner.StatusCode);
        Assert.Contains(diagnostic, inner.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies cancellation from device discovery is preserved.
    /// </summary>
    [Fact]
    public async Task CreateAsync_PreservesDiscoveryCancellation()
    {
        // Arrange
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var driver = CreateDriver(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled request unexpectedly completed.");
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        // Act
        var observed = Record.ExceptionAsync(async () => await driver.CreateAsync(PhysicalDeviceId.New(), "Canceled", Configuration("device"), cancellation.Token)).AsTask();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.False(observed.IsCompleted);
            cancellation.Cancel();

            // Assert
            var exception = Assert.IsAssignableFrom<OperationCanceledException>(await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            Assert.Equal(cancellation.Token, exception.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            await observed.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Verifies dependency injection exposes the driver through the provider-neutral contract.
    /// </summary>
    [Fact]
    public void AddHDHomeRunPhysicalTuner_RegistersDriver()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddHDHomeRunPhysicalTuner();
        using var provider = services.BuildServiceProvider();
        var driver = provider.GetRequiredService<IPhysicalTunerDriver>();

        // Assert
        Assert.IsType<HDHomeRunPhysicalTunerDriver>(driver);
    }

    private static Task InvokeNativeControlAsync(IPhysicalTunerDevice device, string operation, CancellationToken cancellationToken) =>
        operation switch
        {
            "status" => ((IPhysicalDeviceStatusCapability)device).RefreshStatusAsync(cancellationToken),
            "stop" => ((IPhysicalReceiverControlCapability)device).StopReceiverAsync(0, cancellationToken),
            "restart" => ((IPhysicalDeviceRestartCapability)device).RestartAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static HDHomeRunPhysicalTunerDriver CreateDriver(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        => CreateDriver((request, _) => Task.FromResult(responseFactory(request)));

    private static HDHomeRunPhysicalTunerDriver CreateDriver(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
    {
        var clients = new StaticHttpClientFactory(new DelegateHttpMessageHandler(responseFactory));
        var controlFactory = new HDHomeRunHttpControlFactory(clients, NullLogger<HDHomeRunHttpControl>.Instance);
        var deviceService = new HDHomeRunService(NullLoggerFactory.Instance, controlFactory);
        return new HDHomeRunPhysicalTunerDriver(clients, NullLoggerFactory.Instance, deviceService);
    }

    private static AdapterConfiguration Configuration(string address)
        => Configuration(HDHomeRunPhysicalTunerDriver.DriverKind, new HDHomeRunPhysicalTunerConfiguration { Address = address });

    private static AdapterConfiguration Configuration(string kind, HDHomeRunPhysicalTunerConfiguration configuration)
        => new(kind, JsonSerializer.SerializeToElement(configuration));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage ErrorResponse(string diagnostic, bool useResponseBody)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent(useResponseBody ? diagnostic : "Body must not replace the HDHomeRun error header.", Encoding.UTF8, "text/plain")
        };
        if (!useResponseBody)
        {
            response.Headers.TryAddWithoutValidation("X-HDHomeRun-Error", diagnostic);
        }
        return response;
    }

    private static HttpResponseMessage CountedJsonResponse(ref int requestCount, string json)
    {
        Interlocked.Increment(ref requestCount);
        return JsonResponse(json);
    }

    private static string DiscoverJson(int tunerCount, string firmwareVersion = "20260901", string baseUrl = "http://device")
        => $$"""
             {
               "FriendlyName":"HDHomeRun FLEX",
               "ModelNumber":"HDFX",
               "FirmwareName":"hdhomerun",
               "FirmwareVersion":"{{firmwareVersion}}",
               "DeviceID":"ABCDEF01",
               "DeviceAuth":"secret",
               "BaseURL":"{{baseUrl}}",
               "LineupURL":"{{baseUrl}}/lineup.json",
               "TunerCount":{{tunerCount}}
             }
             """;

    private sealed class RecordingDeviceService(IHDHomeRunHttpControlFactory factory) : HDHomeRunService(NullLoggerFactory.Instance, factory)
    {
        /// <summary>Gets the lookups.</summary>
        public List<(string Address, CancellationToken Token, HDHomeRunDeviceInfo Info)> Lookups { get; } = [];

        /// <summary>Gets or sets the cancel lookup.</summary>
        public CancellationTokenSource? CancelLookup { get; set; }

        /// <inheritdoc />
        public override Task<HDHomeRunDevice?> GetDeviceFromDiscoveryAsync(string addressOrHostname, HDHomeRunDeviceInfo deviceInfo, CancellationToken cancellationToken = default)
        {
            Lookups.Add((addressOrHostname, cancellationToken, deviceInfo));
            CancelLookup?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<HDHomeRunDevice?>(null);
        }
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        /// <summary>Creates client.</summary>
        /// <param name="name">Name used by the operation.</param>
        /// <returns>The operation's result.</returns>
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory(request, cancellationToken);
    }

    private sealed class ControlledContent(Func<CancellationToken, Task<Stream>> createStream, Action onDispose) : HttpContent
    {
        /// <inheritdoc />
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => createStream(cancellationToken);

        /// <inheritdoc />
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();

        /// <inheritdoc />
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                onDispose();
            }
        }
    }

    private sealed class DisposalStream(bool throwOnDispose) : MemoryStream
    {
        /// <summary>Gets whether the response stream was closed.</summary>
        public bool Disposed { get; private set; }

        /// <inheritdoc />
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            base.Dispose();
            return throwOnDispose ? ValueTask.FromException(new IOException("Controlled stream disposal failure.")) : ValueTask.CompletedTask;
        }
    }
}
