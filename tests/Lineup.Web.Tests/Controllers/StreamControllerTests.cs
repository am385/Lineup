using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.HDHomeRun.Device;
using Lineup.HDHomeRun.Device.Models;
using Lineup.Web.Controllers;
using Lineup.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Controllers;

/// <summary>
/// Verifies security-sensitive stream controller behavior.
/// </summary>
public class StreamControllerTests
{
    /// <summary>
    /// Verifies disabled MPEG-TS requests return before acquiring tuner capacity.
    /// </summary>
    [Fact]
    public async Task Stream_DisabledChannel_ReturnsForbiddenWithoutTuner()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.ReturnError, capacity, Substitute.For<IProtectedContentSlateService>());

        // Act
        var result = await controller.Stream("9.1");

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Empty(capacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies disabled channels cannot be reached through the diagnostic stream endpoint.
    /// </summary>
    [Fact]
    public async Task TestTranscode_DisabledChannel_ReturnsForbidden()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var controller = CreateDisabledChannelController(
            store,
            DisabledChannelMode.ReturnError,
            Substitute.For<ITunerCapacityLeaseRegistry>(),
            Substitute.For<IProtectedContentSlateService>());

        // Act
        var result = await controller.TestTranscode("9.1");

        // Assert
        var forbidden = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    /// <summary>
    /// Verifies disabled Watch and HLS requests return before acquiring tuner capacity.
    /// </summary>
    [Fact]
    public async Task WatchStreams_DisabledChannel_ReturnForbiddenWithoutTuner()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var fmp4Capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var fmp4Controller = CreateDisabledChannelController(store, DisabledChannelMode.ReturnError, fmp4Capacity, Substitute.For<IProtectedContentSlateService>());
        var hlsCapacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var hlsController = CreateDisabledChannelController(store, DisabledChannelMode.ReturnError, hlsCapacity, Substitute.For<IProtectedContentSlateService>());

        // Act
        await fmp4Controller.StreamFmp4("9.1");
        var hlsResult = await hlsController.StartHlsStream("9.1");

        // Assert
        Assert.Equal(StatusCodes.Status403Forbidden, fmp4Controller.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(hlsResult).StatusCode);
        Assert.Empty(fmp4Capacity.ReceivedCalls());
        Assert.Empty(hlsCapacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies slate mode serves disabled MPEG-TS without acquiring tuner capacity.
    /// </summary>
    [Fact]
    public async Task Stream_DisabledChannelSlate_UsesSyntheticMediaWithoutTuner()
    {
        // Arrange
        var store = await CreateDisabledChannelStoreAsync("9.1");
        var capacity = Substitute.For<ITunerCapacityLeaseRegistry>();
        var slate = Substitute.For<IProtectedContentSlateService>();
        var controller = CreateDisabledChannelController(store, DisabledChannelMode.StreamSlate, capacity, slate);

        // Act
        var result = await controller.Stream("9.1");

        // Assert
        Assert.IsType<EmptyResult>(result);
        await slate.Received(1).StreamAsync(HostedStreamFormat.MpegTs, "9.1", Arg.Any<Stream>(), Arg.Any<CancellationToken>(), ChannelSlateReason.DisabledChannel);
        Assert.Empty(capacity.ReceivedCalls());
    }

    /// <summary>
    /// Verifies that IPv4 and IPv6 client endpoints are formatted unambiguously for diagnostics.
    /// </summary>
    [Theory]
    [InlineData("192.0.2.10", 54321, "192.0.2.10:54321")]
    [InlineData("2001:db8::10", 54321, "[2001:db8::10]:54321")]
    public void FormatClientAddress_KnownEndpoint_ReturnsAddressAndPort(string address, int port, string expected)
    {
        // Arrange
        var ipAddress = IPAddress.Parse(address);

        // Act
        var formatted = StreamController.FormatClientAddress(ipAddress, port);

        // Assert
        Assert.Equal(expected, formatted);
    }

    /// <summary>
    /// Verifies that a missing remote address remains unknown.
    /// </summary>
    [Fact]
    public void FormatClientAddress_MissingAddress_ReturnsNull()
    {
        // Arrange
        IPAddress? address = null;

        // Act
        var formatted = StreamController.FormatClientAddress(address, 0);

        // Assert
        Assert.Null(formatted);
    }

    /// <summary>
    /// Verifies that generated playlist and segment basenames resolve beneath the HLS session directory.
    /// </summary>
    [Theory]
    [InlineData("stream.m3u8")]
    [InlineData("stream0.ts")]
    [InlineData("stream1726358400.ts")]
    public void TryResolveHlsFilePath_GeneratedBasename_ReturnsContainedCanonicalPath(string filename)
    {
        // Arrange
        var hlsDirectory = Path.Combine(Environment.CurrentDirectory, "hls-session");
        var expectedPath = Path.GetFullPath(Path.Combine(hlsDirectory, filename));

        // Act
        var resolved = StreamController.TryResolveHlsFilePath(hlsDirectory, filename, out var filePath);

        // Assert
        Assert.True(resolved);
        Assert.Equal(expectedPath, filePath);
    }

    /// <summary>
    /// Verifies that traversal, rooted, separator, encoded Windows separator, and invalid HLS names are rejected.
    /// </summary>
    [Theory]
    [InlineData("../stream.m3u8")]
    [InlineData(@"..\stream.m3u8")]
    [InlineData(@"\stream.m3u8")]
    [InlineData(@"C:\stream.m3u8")]
    [InlineData("nested/stream.m3u8")]
    [InlineData(@"nested\stream.m3u8")]
    [InlineData("..%2fstream.m3u8")]
    [InlineData("..%2Fstream.m3u8")]
    [InlineData("..%5cstream.m3u8")]
    [InlineData("..%5Cstream.m3u8")]
    [InlineData("other.m3u8")]
    [InlineData("stream.ts")]
    [InlineData("stream1.mp4")]
    public void TryResolveHlsFilePath_TraversalOrInvalidName_ReturnsFalse(string filename)
    {
        // Arrange
        var hlsDirectory = Path.Combine(Environment.CurrentDirectory, "hls-session");

        // Act
        var resolved = StreamController.TryResolveHlsFilePath(hlsDirectory, filename, out var filePath);

        // Assert
        Assert.False(resolved);
        Assert.Equal(string.Empty, filePath);
    }

    /// <summary>
    /// Verifies malformed and stale Watch subtitle client identifiers do not expose files.
    /// </summary>
    [Theory]
    [InlineData("../client", 400)]
    [InlineData("missingclient", 404)]
    public void GetFmp4ClientSubtitles_InvalidOrStaleClient_ReturnsExplicitStatus(string clientId, int statusCode)
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>());

        // Act
        var result = controller.GetFmp4ClientSubtitles(clientId);

        // Assert
        var status = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(statusCode, status.StatusCode);
    }

    /// <summary>
    /// Verifies that an fMP4 protected-content slate does not retain physical tuner capacity while its client remains connected.
    /// </summary>
    [Fact]
    public async Task WriteFmp4StartupErrorAsync_ConnectedSlate_ReleasesPhysicalTunerCapacity()
    {
        // Arrange
        var profileUri = new Uri("http://tuner.local/");
        var registry = new TunerCapacityLeaseRegistry();
        var physicalLease = await registry.TryAcquireAsync(profileUri, new Uri("http://tuner.local:5004/auto/v20.1"), 1, TestContext.Current.CancellationToken);
        Assert.NotNull(physicalLease);
        var slateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slateService = Substitute.For<IProtectedContentSlateService>();
        slateService.StreamAsync(HostedStreamFormat.FragmentedMp4, "20.1", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                slateStarted.SetResult();
                await releaseSlate.Task.WaitAsync(call.ArgAt<CancellationToken>(3));
            });
        var controller = CreateProtectedSlateController(registry, slateService);
        var errorMethod = typeof(StreamController).GetMethod("WriteFmp4StartupErrorAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(errorMethod);

        // Act
        var slateTask = Assert.IsAssignableFrom<Task>(errorMethod.Invoke(controller, ["20.1", "http://tuner.local:5004/auto/v20.1", "session", DateTime.UtcNow, physicalLease]));
        await slateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var nextLease = await registry.TryAcquireAsync(profileUri, new Uri("http://tuner.local:5004/auto/v21.1"), 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(nextLease);
        Assert.False(slateTask.IsCompleted);
        releaseSlate.SetResult();
        await slateTask;
        await physicalLease.DisposeAsync();
        await nextLease!.DisposeAsync();
        Assert.Empty(registry.GetDiagnostics());
    }

    /// <summary>
    /// Verifies that an abandoned HLS session expires and releases all owned resources.
    /// </summary>
    [Fact]
    public async Task HlsSession_Inactive_ExpiresAndCleansResources()
    {
        // Arrange
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var capacityLease = Substitute.For<ITunerCapacityLease>();
        var controller = CreateLifecycleController(activeStreams, hlsInactivityTimeout: TimeSpan.FromMilliseconds(30));
        var session = CreateHlsSession(controller, capacityLease, out var sessionId, out var directory);

        // Act
        await WaitUntilAsync(() => !Directory.Exists(directory));

        // Assert
        Assert.False(GetHlsSessions().Contains(sessionId));
        activeStreams.Received().Unregister(sessionId);
        capacityLease.Received().Dispose();
        GC.KeepAlive(session);
    }

    /// <summary>
    /// Verifies that valid HLS file access refreshes inactivity and prevents premature expiration.
    /// </summary>
    [Fact]
    public async Task GetHlsFile_ValidAccess_RefreshesInactivity()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>(), hlsInactivityTimeout: TimeSpan.FromMilliseconds(150));
        CreateHlsSession(controller, Substitute.For<ITunerCapacityLease>(), out var sessionId, out var directory);
        await Task.Delay(90, TestContext.Current.CancellationToken);

        // Act
        var result = controller.GetHlsFile(sessionId, "stream.m3u8");
        await Task.Delay(90, TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<PhysicalFileResult>(result);
        Assert.True(Directory.Exists(directory));
        Assert.True(GetHlsSessions().Contains(sessionId));
        Assert.IsType<OkObjectResult>(controller.StopHls(sessionId));
    }

    /// <summary>
    /// Verifies that application shutdown terminates and removes every registered HLS session.
    /// </summary>
    [Fact]
    public async Task ApplicationStopping_LiveHlsSession_CleansResources()
    {
        // Arrange
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        var activeStreams = Substitute.For<IActiveStreamRegistry>();
        var capacityLease = Substitute.For<ITunerCapacityLease>();
        var controller = CreateLifecycleController(activeStreams, lifetime);
        CreateHlsSession(controller, capacityLease, out var sessionId, out var directory);

        // Act
        stopping.Cancel();
        await WaitUntilAsync(() => !Directory.Exists(directory));

        // Assert
        Assert.False(GetHlsSessions().Contains(sessionId));
        activeStreams.Received().Unregister(sessionId);
        capacityLease.Received().Dispose();
    }

    /// <summary>
    /// Verifies that same-channel HLS viewers retain independent sessions and can stop independently.
    /// </summary>
    [Fact]
    public void HlsSession_SameChannelViewers_CoexistAndStopIndependently()
    {
        // Arrange
        var controller = CreateLifecycleController(Substitute.For<IActiveStreamRegistry>());
        CreateHlsSession(controller, Substitute.For<ITunerCapacityLease>(), out var firstSessionId, out var firstDirectory);

        // Act
        CreateHlsSession(controller, Substitute.For<ITunerCapacityLease>(), out var secondSessionId, out var secondDirectory);
        var firstStop = controller.StopHls(firstSessionId);

        // Assert
        Assert.IsType<OkObjectResult>(firstStop);
        Assert.False(Directory.Exists(firstDirectory));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.True(GetHlsSessions().Contains(secondSessionId));
        Assert.IsType<OkObjectResult>(controller.StopHls(secondSessionId));
        Assert.False(Directory.Exists(secondDirectory));
    }

    private static StreamController CreateProtectedSlateController(TunerCapacityLeaseRegistry registry, IProtectedContentSlateService slateService)
    {
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("StreamProxy").Returns(new HttpClient(new ProtectedContentResponseHandler()));
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings
        {
            ProtectedContentMode = ProtectedContentMode.StreamSlate
        });
        return new StreamController(
            httpClientFactory,
            Substitute.For<IEpgRepository>(),
            settingsService,
            CreateChannelLineupStore(),
            Substitute.For<IDeviceStateService>(),
            Substitute.For<IHdHomeRunProxyProfileProvider>(),
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            Substitute.For<IActiveStreamRegistry>(),
            slateService,
            Substitute.For<ITunerStreamMultiplexer>(),
            registry,
            NullLogger<StreamController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static StreamController CreateLifecycleController(IActiveStreamRegistry activeStreams, IHostApplicationLifetime? lifetime = null, TimeSpan? hlsInactivityTimeout = null)
    {
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        return new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IEpgRepository>(),
            settingsService,
            CreateChannelLineupStore(),
            Substitute.For<IDeviceStateService>(),
            Substitute.For<IHdHomeRunProxyProfileProvider>(),
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            activeStreams,
            Substitute.For<IProtectedContentSlateService>(),
            Substitute.For<ITunerStreamMultiplexer>(),
            Substitute.For<ITunerCapacityLeaseRegistry>(),
            NullLogger<StreamController>.Instance,
            lifetime,
            TimeProvider.System,
            hlsInactivityTimeout)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
    }

    private static object CreateHlsSession(StreamController controller, ITunerCapacityLease capacityLease, out string sessionId, out string directory)
    {
        sessionId = Guid.NewGuid().ToString("N");
        directory = Path.Combine(Environment.CurrentDirectory, $".hls-test-{sessionId}");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "stream.m3u8"), "#EXTM3U");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "--version",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        process.WaitForExit();
        var sessionType = typeof(StreamController).GetNestedType("HlsSession", BindingFlags.NonPublic);
        Assert.NotNull(sessionType);
        var session = Activator.CreateInstance(sessionType);
        Assert.NotNull(session);
        SetProperty(sessionType, session, "SessionId", sessionId);
        SetProperty(sessionType, session, "Channel", "20.1");
        SetProperty(sessionType, session, "Process", process);
        SetProperty(sessionType, session, "HlsDirectory", directory);
        SetProperty(sessionType, session, "PlaylistPath", Path.Combine(directory, "stream.m3u8"));
        SetProperty(sessionType, session, "StartTime", DateTime.UtcNow);
        SetProperty(sessionType, session, "CapacityLease", capacityLease);
        var register = typeof(StreamController).GetMethod("RegisterHlsSession", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(register);
        register.Invoke(controller, [session]);
        return session;
    }

    private static ChannelLineupStore CreateChannelLineupStore()
    {
        return new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-stream-{Guid.NewGuid():N}.json"));
    }

    private static async Task<ChannelLineupStore> CreateDisabledChannelStoreAsync(string guideNumber)
    {
        var store = CreateChannelLineupStore();
        await store.StoreAsync(
            [new Lineup.HDHomeRun.Device.Models.HDHomeRunChannel { GuideNumber = guideNumber, GuideName = "Disabled", URL = $"http://tuner.local/auto/v{guideNumber}" }],
            TestContext.Current.CancellationToken);
        await store.SetChannelEnabledAsync(guideNumber, enabled: false, TestContext.Current.CancellationToken);
        return store;
    }

    private static StreamController CreateDisabledChannelController(ChannelLineupStore store, DisabledChannelMode mode, ITunerCapacityLeaseRegistry capacity, IProtectedContentSlateService slate)
    {
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings { DeviceAddress = "tuner.local", DisabledChannelMode = mode });
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profiles.GetPrimaryProfileAsync(Arg.Any<CancellationToken>()).Returns(new HdHomeRunProxyProfileSnapshot
        {
            Settings = new HdHomeRunProxyProfileSettings(),
            PhysicalDevice = new HDHomeRunDeviceInfo
            {
                FriendlyName = "Tuner",
                ModelNumber = "HDHR",
                FirmwareName = "hdhomerun",
                FirmwareVersion = "1",
                DeviceID = "12345678",
                DeviceAuth = "auth",
                BaseURL = "http://tuner.local",
                LineupURL = "http://tuner.local/lineup.json",
                TunerCount = 2
            },
            IsPrimary = true,
            DeviceId = 0x12345678,
            DeviceAuth = "virtual",
            TunerCount = 2,
            FriendlyName = "Lineup",
            PhysicalBaseUri = new Uri("http://tuner.local/")
        });
        return new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IEpgRepository>(),
            settings,
            store,
            Substitute.For<IDeviceStateService>(),
            profiles,
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            Substitute.For<IActiveStreamRegistry>(),
            slate,
            Substitute.For<ITunerStreamMultiplexer>(),
            capacity,
            NullLogger<StreamController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Response = { Body = new MemoryStream() }
                }
            }
        };
    }

    private static IDictionary GetHlsSessions()
    {
        var sessions = typeof(StreamController).GetField("_hlsSessions", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(sessions);
        return Assert.IsAssignableFrom<IDictionary>(sessions.GetValue(null));
    }

    private static void SetProperty(Type type, object instance, string name, object value)
    {
        var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(property);
        property.SetValue(instance, value);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(predicate());
    }

    private sealed class ProtectedContentResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("X-HDHomeRun-Error", "811 DRM Content");
            return Task.FromResult(response);
        }
    }
}
