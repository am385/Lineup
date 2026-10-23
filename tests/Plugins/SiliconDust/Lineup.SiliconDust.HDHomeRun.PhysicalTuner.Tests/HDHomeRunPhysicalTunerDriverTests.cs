using System.Net;
using System.Text;
using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.SiliconDust.HDHomeRun.Device;
using Lineup.SiliconDust.HDHomeRun.Device.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lineup.SiliconDust.HDHomeRun.PhysicalTuner.Tests;

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
        Assert.Equal(new Uri(new Uri(expectedBaseAddress), "discover.json"), Assert.Single(requests));
        Assert.Equal(address, driver.GetSettings(configuration).Values.Single().Value?.GetString());
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
        Assert.Equal(2, discoveryRequestCount);
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
        var firstLease = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await device.OpenStreamAsync(request, cancellation.Token));
        await firstLease.DisposeAsync();
        await using var replacementLease = await device.OpenStreamAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("video/mp2t", firstLease.ContentType);
        Assert.Equal(2, streamRequestCount);
        Assert.Equal(1, replacementLease.Stream.ReadByte());
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
    public async Task OpenStreamAsync_TunerDiagnostic_ThrowsStructuredFailure(
        string diagnostic,
        bool useResponseBody,
        PhysicalStreamFailureCategory expectedCategory,
        string expectedCode,
        bool expectedRetryable)
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
        var driver = CreateDriver(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The canceled request unexpectedly completed.");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await driver.CreateAsync(PhysicalDeviceId.New(), "Canceled", Configuration("device"), cancellation.Token));

        // Assert
        Assert.Equal(cancellation.Token, exception.CancellationToken);
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

    private static string DiscoverJson(int tunerCount, string firmwareVersion = "20260901")
        => $$"""
             {
               "FriendlyName":"HDHomeRun FLEX",
               "ModelNumber":"HDFX",
               "FirmwareName":"hdhomerun",
               "FirmwareVersion":"{{firmwareVersion}}",
               "DeviceID":"ABCDEF01",
               "DeviceAuth":"secret",
               "BaseURL":"http://device",
               "LineupURL":"http://device/lineup.json",
               "TunerCount":{{tunerCount}}
             }
             """;

    private sealed class RecordingDeviceService(IHDHomeRunHttpControlFactory factory) : HDHomeRunService(NullLoggerFactory.Instance, factory)
    {
        public List<(string Address, CancellationToken Token)> Lookups { get; } = [];

        public CancellationTokenSource? CancelLookup { get; set; }

        public override Task<HDHomeRunDevice?> GetDeviceByIpAsync(string addressOrHostname, CancellationToken cancellationToken = default)
        {
            Lookups.Add((addressOrHostname, cancellationToken));
            CancelLookup?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<HDHomeRunDevice?>(null);
        }
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responseFactory(request, cancellationToken);
    }
}
