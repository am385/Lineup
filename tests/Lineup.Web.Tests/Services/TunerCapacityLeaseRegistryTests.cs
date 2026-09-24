using Lineup.Web.Services;
using Lineup.Core;
using Lineup.Core.Storage;
using Lineup.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies atomic physical tuner capacity leasing and compatibility errors.
/// </summary>
public class TunerCapacityLeaseRegistryTests
{
    private static readonly TransientDataStore TestTransientData =
        new(Path.Combine(Path.GetTempPath(), $"lineup-tuner-capacity-tests-{Environment.ProcessId}"));

    private static readonly Uri Profile = new("http://tuner.local/");

    /// <summary>
    /// Verifies that concurrent different-source acquisition cannot exceed physical capacity.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_ConcurrentDifferentSources_GrantsOnlyCapacity()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        var attempts = await Task.WhenAll(Enumerable.Range(1, 32).Select(index =>
            registry.TryAcquireAsync(Profile, Source(index), 1, cancellationToken).AsTask()));

        // Assert
        Assert.Single(attempts, lease => lease != null);
        Assert.Single(registry.GetDiagnostics());
        foreach (var lease in attempts.OfType<ITunerCapacityLease>())
        {
            await lease.DisposeAsync();
        }
    }

    /// <summary>
    /// Verifies that receivers for one normalized source share a single tuner slot.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_SameSource_SharesOneSlot()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Act
        await using var first = await registry.TryAcquireAsync(new Uri("HTTP://TUNER.local/"), new Uri("HTTP://TUNER.local:5004/auto/v5.1"), 1, cancellationToken);
        await using var second = await registry.TryAcquireAsync(Profile, Source("5.1"), 1, cancellationToken);
        // Assert
        var diagnostics = Assert.Single(registry.GetDiagnostics());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, diagnostics.ActiveSourceCount);
        Assert.Equal(2, diagnostics.ReceiverCount);
        Assert.Equal(2, Assert.Single(diagnostics.Sources).ReceiverCount);
    }

    /// <summary>
    /// Verifies that a different source is rejected after all tuner slots are occupied.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_DifferentSourceAtCapacity_RejectsLease()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        await using var occupied = await registry.TryAcquireAsync(Profile, Source("2.1"), 1, TestContext.Current.CancellationToken);

        // Act
        var rejected = await registry.TryAcquireAsync(Profile, Source("3.1"), 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(occupied);
        Assert.Null(rejected);
    }

    /// <summary>
    /// Verifies that only the final shared receiver release frees the tuner slot.
    /// </summary>
    [Fact]
    public async Task Dispose_FinalSharedLease_FreesSlot()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        // Act
        var first = await registry.TryAcquireAsync(Profile, Source("7.1"), 1, TestContext.Current.CancellationToken);
        var second = await registry.TryAcquireAsync(Profile, Source("7.1"), 1, TestContext.Current.CancellationToken);

        await first!.DisposeAsync();
        var rejected = await registry.TryAcquireAsync(Profile, Source("8.1"), 1, TestContext.Current.CancellationToken);
        await second!.DisposeAsync();
        var acquired = await registry.TryAcquireAsync(Profile, Source("8.1"), 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(rejected);
        Assert.NotNull(acquired);
        await acquired!.DisposeAsync();
        Assert.Empty(registry.GetDiagnostics());
    }

    /// <summary>
    /// Verifies that independent physical profiles have independent tuner capacity.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_DifferentProfiles_IsolatesCapacity()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        // Act
        await using var first = await registry.TryAcquireAsync(Profile, Source("10.1"), 1, TestContext.Current.CancellationToken);

        await using var second = await registry.TryAcquireAsync(new Uri("http://second-tuner.local/"), Source("11.1"), 1, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, registry.GetDiagnostics().Count);
    }

    /// <summary>
    /// Verifies that cancellation before acquisition cannot create registry state.
    /// </summary>
    [Fact]
    public async Task TryAcquireAsync_Canceled_DoesNotLeakLease()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await registry.TryAcquireAsync(Profile, Source("12.1"), 1, cancellation.Token));

        // Assert
        Assert.IsType<OperationCanceledException>(exception);
        Assert.Empty(registry.GetDiagnostics());
    }

    /// <summary>
    /// Verifies that disposing during failure cleanup releases acquired capacity.
    /// </summary>
    [Fact]
    public async Task Dispose_AfterConsumerFailure_DoesNotLeakLease()
    {
        // Arrange
        // Act
        var registry = new TunerCapacityLeaseRegistry();

        // Assert
        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var lease = await registry.TryAcquireAsync(Profile, Source("13.1"), 1, TestContext.Current.CancellationToken);
            Assert.NotNull(lease);
            throw new InvalidOperationException("Simulated upstream failure.");
        });

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Empty(registry.GetDiagnostics());
    }

    /// <summary>
    /// Verifies HDHomeRun-compatible status and error headers for exhausted capacity.
    /// </summary>
    [Fact]
    public void CreateNoTunerAvailableResult_MapsToHdHomeRun503()
    {
        // Arrange
        var context = new DefaultHttpContext();

        // Act
        var result = HdHomeRunStreamError.CreateNoTunerAvailableResult(context.Response);

        // Assert
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Equal(HdHomeRunStreamError.NoTunerAvailable, context.Response.Headers["X-HDHomeRun-Error"]);
        Assert.IsType<ObjectResult>(result);
    }

    /// <summary>
    /// Verifies that the HDHomeRun-compatible stream action rejects a different channel when profile capacity is occupied.
    /// </summary>
    [Fact]
    public async Task Stream_ProfileCapacityOccupied_ReturnsHdHomeRun503()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        var profile = CreateProfile(tunerCount: 1);
        await using var occupied = await registry.TryAcquireAsync(profile.PhysicalBaseUri, Source("20.1"), profile.TunerCount, TestContext.Current.CancellationToken);
        var profileProvider = Substitute.For<IHdHomeRunProxyProfileProvider>();
        profileProvider.GetPrimaryProfileAsync(Arg.Any<CancellationToken>()).Returns(profile);
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings());
        var controller = new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IEpgRepository>(),
            settingsService,
            CreateChannelLineupStore(),
            Substitute.For<IDeviceStateService>(),
            profileProvider,
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            Substitute.For<IActiveStreamRegistry>(),
            Substitute.For<IProtectedContentSlateService>(),
            Substitute.For<ITunerStreamMultiplexer>(),
            registry,
            NullLogger<StreamController>.Instance,
            transientData: TestTransientData)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // Act
        var result = await controller.Stream("21.1");

        // Assert
        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(HdHomeRunStreamError.NoTunerAvailable, controller.Response.Headers["X-HDHomeRun-Error"]);
    }

    /// <summary>
    /// Verifies that an fMP4 browser stream observes capacity occupied by a different MPEG-TS source.
    /// </summary>
    [Fact]
    public async Task StreamFmp4_MpegTsSourceOccupiesCapacity_ReturnsHdHomeRun503()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        var profile = CreateProfile(tunerCount: 1);
        await using var occupied = await registry.TryAcquireAsync(profile.PhysicalBaseUri, Source("20.1"), profile.TunerCount, TestContext.Current.CancellationToken);
        var controller = CreateController(registry, profile, out var profileProvider);

        // Act
        await controller.StreamFmp4("21.1");

        // Assert
        Assert.NotNull(occupied);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, controller.Response.StatusCode);
        Assert.Equal(HdHomeRunStreamError.NoTunerAvailable, controller.Response.Headers["X-HDHomeRun-Error"]);
        await profileProvider.DidNotReceive().GetPrimaryProfileAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that an HLS browser stream observes capacity occupied by a different MPEG-TS source.
    /// </summary>
    [Fact]
    public async Task StartHlsStream_MpegTsSourceOccupiesCapacity_ReturnsHdHomeRun503()
    {
        // Arrange
        var registry = new TunerCapacityLeaseRegistry();
        var profile = CreateProfile(tunerCount: 1);
        await using var occupied = await registry.TryAcquireAsync(profile.PhysicalBaseUri, Source("20.1"), profile.TunerCount, TestContext.Current.CancellationToken);
        var controller = CreateController(registry, profile, out var profileProvider);

        // Act
        var result = await controller.StartHlsStream("21.1");

        // Assert
        Assert.NotNull(occupied);
        var unavailable = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
        Assert.Equal(HdHomeRunStreamError.NoTunerAvailable, controller.Response.Headers["X-HDHomeRun-Error"]);
        await profileProvider.DidNotReceive().GetPrimaryProfileAsync(Arg.Any<CancellationToken>());
    }

    private static Uri Source(int channel)
    {
        return Source($"{channel}.1");
    }

    private static Uri Source(string channel)
    {
        return new Uri($"http://tuner.local:5004/auto/v{channel}");
    }

    private static HdHomeRunProxyProfileSnapshot CreateProfile(int tunerCount)
    {
        return HdHomeRunProxyProfileResolver.CreateSnapshot(
            new HdHomeRunProxyProfileSettings
            {
                PhysicalAddress = "tuner.local",
                VirtualDeviceId = HdHomeRunProxyIdentity.CreateDeviceId("capacity-controller-test")
            },
            new Lineup.HDHomeRun.Device.Models.HDHomeRunDeviceInfo
            {
                FriendlyName = "Tuner",
                ModelNumber = "HDHR",
                FirmwareName = "hdhomerun",
                FirmwareVersion = "1",
                DeviceID = "12345678",
                DeviceAuth = "physical-auth",
                BaseURL = "http://tuner.local",
                LineupURL = "http://tuner.local/lineup.json",
                TunerCount = tunerCount
            },
            true);
    }

    private static StreamController CreateController(TunerCapacityLeaseRegistry registry, HdHomeRunProxyProfileSnapshot profile, out IHdHomeRunProxyProfileProvider profileProvider)
    {
        profileProvider = Substitute.For<IHdHomeRunProxyProfileProvider>();
        var settingsService = Substitute.For<IAppSettingsService>();
        settingsService.Settings.Returns(new AppSettings { DeviceAddress = profile.PhysicalBaseUri.Host, EnableHdHomeRunProxy = false });
        var deviceState = Substitute.For<IDeviceStateService>();
        deviceState.DeviceInfo.Returns(profile.PhysicalDevice);
        return new StreamController(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IEpgRepository>(),
            settingsService,
            CreateChannelLineupStore(),
            deviceState,
            profileProvider,
            Substitute.For<IMpegTsTranscodeService>(),
            Substitute.For<IMediaProbeService>(),
            Substitute.For<IActiveStreamRegistry>(),
            Substitute.For<IProtectedContentSlateService>(),
            Substitute.For<ITunerStreamMultiplexer>(),
            registry,
            NullLogger<StreamController>.Instance,
            transientData: TestTransientData)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static ChannelLineupStore CreateChannelLineupStore()
    {
        return new ChannelLineupStore(Path.Combine(Path.GetTempPath(), $"lineup-capacity-{Guid.NewGuid():N}.db"));
    }
}
