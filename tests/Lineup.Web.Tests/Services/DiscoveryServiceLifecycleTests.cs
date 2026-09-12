using Lineup.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Web.Tests.Services;

/// <summary>
/// Verifies runtime lifecycle changes for network discovery listeners.
/// </summary>
public class DiscoveryServiceLifecycleTests
{
    /// <summary>
    /// Verifies that a hosted discovery service starts its listener when discovery is enabled after host startup.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChanged_EnableAfterStart_StartsListener(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: false);
        var service = CreateService(ssdp, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);

        // Act
        settings.Settings.EnableNetworkDiscovery = true;
        RaiseSettingsChanged(settings);
        await listener.WaitForStartsAsync(1);

        // Assert
        Assert.Equal(1, listener.StartCount);
        Assert.Equal(1, listener.ActiveCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that disabling discovery cancels and disposes an active listener promptly.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChanged_DisableAfterStart_StopsListener(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);

        // Act
        settings.Settings.EnableNetworkDiscovery = false;
        RaiseSettingsChanged(settings);
        await listener.WaitForStopsAsync(1);

        // Assert
        Assert.Equal(0, listener.ActiveCount);
        Assert.Equal(1, listener.StopCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that disabling the global proxy switch stops an active discovery listener.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChanged_DisableProxyAfterStart_StopsListener(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);

        // Act
        settings.Settings.EnableHdHomeRunProxy = false;
        RaiseSettingsChanged(settings);
        await listener.WaitForStopsAsync(1);

        // Assert
        Assert.Equal(0, listener.ActiveCount);
        Assert.Equal(1, listener.StopCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that changing an advertised URL replaces the active listener.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChanged_AdvertisedUrlChanged_RestartsListener(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);

        // Act
        settings.Settings.HdHomeRunProxyProfiles[0].AdvertisedBaseUrl = "http://lineup.example:8080";
        RaiseSettingsChanged(settings);
        await listener.WaitForStartsAsync(2);

        // Assert
        Assert.Equal(2, listener.StartCount);
        Assert.Equal(1, listener.StopCount);
        Assert.Equal(1, listener.MaximumActiveCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that unchanged configuration notifications do not create duplicate listeners.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsChanged_UnchangedConfiguration_DoesNotDuplicateListener(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);

        // Act
        RaiseSettingsChanged(settings);
        RaiseSettingsChanged(settings);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, listener.StartCount);
        Assert.Equal(1, listener.ActiveCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that an unexpectedly completed or failed listener restarts without a settings notification.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ListenerStopsUnexpectedly_RestartsWithoutSettingsChange(bool ssdp, bool fail)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);

        // Act
        if (fail)
        {
            listener.FailCurrent();
        }
        else
        {
            listener.CompleteCurrent();
        }
        await listener.WaitForStartsAsync(2);

        // Assert
        Assert.Equal(2, listener.StartCount);
        Assert.Equal(1, listener.ActiveCount);
        Assert.Equal(1, listener.MaximumActiveCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that disabling discovery interrupts recovery backoff without restarting the listener.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerStopsUnexpectedly_DisableDuringBackoff_DoesNotRestart(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener, TimeSpan.FromSeconds(5));
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);

        listener.CompleteCurrent();
        await listener.WaitForStopsAsync(1);
        // Act
        settings.Settings.EnableNetworkDiscovery = false;
        RaiseSettingsChanged(settings);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, listener.StartCount);
        Assert.Equal(0, listener.ActiveCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that host shutdown interrupts recovery backoff without restarting the listener.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerStopsUnexpectedly_ShutdownDuringBackoff_DoesNotRestart(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp, settings, out var listener, TimeSpan.FromSeconds(5));
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForStartsAsync(1);
        listener.CompleteCurrent();
        await listener.WaitForStopsAsync(1);

        // Act
        await service.StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, listener.StartCount);
        Assert.Equal(0, listener.ActiveCount);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that SSDP alive advertisements are renewed while the listener remains active.
    /// </summary>
    [Fact]
    public async Task SsdpAliveNotification_RenewalTick_SendsAnotherAdvertisement()
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp: true, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForNotificationsAsync(1);

        // Act
        listener.TriggerRenewal();
        await listener.WaitForNotificationsAsync(2);

        // Assert
        Assert.Equal(2, listener.NotificationCount);
        Assert.Equal(1, listener.StartCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that disabling SSDP stops its advertisement renewal loop.
    /// </summary>
    [Fact]
    public async Task SsdpAliveNotification_Disable_StopsRenewal()
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp: true, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForNotificationsAsync(1);

        // Act
        settings.Settings.EnableNetworkDiscovery = false;
        RaiseSettingsChanged(settings);
        await listener.WaitForStopsAsync(1);
        listener.TriggerRenewal();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, listener.NotificationCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that host shutdown stops SSDP advertisement renewal.
    /// </summary>
    [Fact]
    public async Task SsdpAliveNotification_Shutdown_StopsRenewal()
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp: true, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForNotificationsAsync(1);

        // Act
        await service.StopAsync(TestContext.Current.CancellationToken);
        listener.TriggerRenewal();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, listener.NotificationCount);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that an SSDP configuration change starts one renewal loop using the updated profile.
    /// </summary>
    [Fact]
    public async Task SsdpAliveNotification_ConfigurationChanged_UsesUpdatedProfile()
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp: true, settings, out var listener);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForNotificationsAsync(1);

        // Act
        settings.Settings.HdHomeRunProxyProfiles[0].AdvertisedBaseUrl = "http://updated.example";
        RaiseSettingsChanged(settings);
        await listener.WaitForStartsAsync(2);
        await listener.WaitForNotificationsAsync(2);
        listener.TriggerRenewal();
        await listener.WaitForNotificationsAsync(3);

        // Assert
        Assert.Equal(["http://lineup.example", "http://updated.example", "http://updated.example"], listener.AdvertisedUrls);
        Assert.Equal(1, listener.ActiveCount);
        Assert.Equal(1, listener.MaximumActiveCount);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that an unavailable SSDP port remains dormant until configuration changes and then retries successfully.
    /// </summary>
    [Fact]
    public async Task SsdpListener_NoSockets_WaitsForConfigurationChangeBeforeRetry()
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp: true, settings, out var listener);
        listener.MakeNextSessionUnavailable();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForSessionAttemptsAsync(1);

        await Task.Delay(100, TestContext.Current.CancellationToken);
        // Act
        settings.Settings.HdHomeRunProxyProfiles[0].AdvertisedBaseUrl = "http://updated.example";
        RaiseSettingsChanged(settings);
        await listener.WaitForStartsAsync(1);

        // Assert
        Assert.Equal(2, listener.SessionAttemptCount);
        Assert.Equal(1, listener.StartCount);
        Assert.Equal(["http://updated.example"], listener.AdvertisedUrls);
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that shutdown does not retry an SSDP session that could not create any sockets.
    /// </summary>
    [Fact]
    public async Task SsdpListener_NoSockets_ShutdownDoesNotRestart()
    {
        // Arrange
        var settings = CreateSettings(enabled: true);
        var service = CreateService(ssdp: true, settings, out var listener);
        listener.MakeNextSessionUnavailable();
        await service.StartAsync(TestContext.Current.CancellationToken);
        await listener.WaitForSessionAttemptsAsync(1);

        // Act
        await service.StopAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, listener.SessionAttemptCount);
        Assert.Equal(0, listener.StartCount);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that advertised-device snapshots are reused and invalidated by device-state changes.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdvertisedDevices_DeviceStateChanged_RebuildsCachedSnapshot(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: false);
        var service = CreateService(ssdp, settings, out var listener);
        listener.ProfileProvider.GetProfilesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<HdHomeRunProxyProfileSnapshot>());
        await service.StartAsync(TestContext.Current.CancellationToken);

        // Act
        await ResolveAdvertisedDevicesAsync(service, ssdp);
        await ResolveAdvertisedDevicesAsync(service, ssdp);
        listener.DeviceState.OnStateChanged += Raise.Event<Action>();
        await ResolveAdvertisedDevicesAsync(service, ssdp);

        // Assert
        await listener.ProfileProvider.Received(2).GetProfilesAsync(Arg.Any<CancellationToken>());
        await service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that each protocol limits bursts independently per source and permits traffic after the window.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponseRateLimit_SourceBurst_IsBoundedAndRecovers(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: false);
        var service = CreateService(ssdp, settings, out var listener);
        var source = System.Net.IPAddress.Parse("192.0.2.10");

        // Act
        var permits = Enumerable.Range(0, 11).Select(_ => AcquirePermit(service, ssdp, source)).ToArray();
        listener.AdvanceClock(TimeSpan.FromSeconds(1));
        var permitAfterWindow = AcquirePermit(service, ssdp, source);

        // Assert
        Assert.Equal(10, permits.Count(allowed => allowed));
        Assert.False(permits[^1]);
        Assert.True(permitAfterWindow);
        service.Dispose();
    }

    /// <summary>
    /// Verifies that malformed datagrams are rejected before either protocol resolves advertised profiles.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedDatagram_DoesNotResolveAdvertisedProfiles(bool ssdp)
    {
        // Arrange
        var settings = CreateSettings(enabled: false);
        var service = CreateService(ssdp, settings, out var listener);

        // Act
        var responseCount = await ProcessMalformedDatagramAsync(service, ssdp);

        // Assert
        Assert.Equal(0, responseCount);
        await listener.ProfileProvider.DidNotReceive().GetProfilesAsync(Arg.Any<CancellationToken>());
        service.Dispose();
    }

    private static Task<IReadOnlyList<HdHomeRunAdvertisedDevice>> ResolveAdvertisedDevicesAsync(BackgroundService service, bool ssdp)
    {
        return ssdp
            ? ((TestSsdpDiscoveryService)service).ResolveAdvertisedDevicesAsync()
            : ((TestHdHomeRunDiscoveryService)service).ResolveAdvertisedDevicesAsync();
    }

    private static bool AcquirePermit(BackgroundService service, bool ssdp, System.Net.IPAddress sourceAddress)
    {
        return ssdp
            ? ((TestSsdpDiscoveryService)service).AcquirePermit(sourceAddress)
            : ((TestHdHomeRunDiscoveryService)service).AcquirePermit(sourceAddress);
    }

    private static async Task<int> ProcessMalformedDatagramAsync(BackgroundService service, bool ssdp)
    {
        return ssdp
            ? await ((TestSsdpDiscoveryService)service).ProcessMalformedDatagramAsync()
            : await ((TestHdHomeRunDiscoveryService)service).ProcessMalformedDatagramAsync();
    }

    private static IAppSettingsService CreateSettings(bool enabled)
    {
        var settings = Substitute.For<IAppSettingsService>();
        settings.Settings.Returns(new AppSettings
        {
            EnableHdHomeRunProxy = true,
            EnableNetworkDiscovery = enabled,
            HdHomeRunProxyProfiles =
            [
                new HdHomeRunProxyProfileSettings
                {
                    Enabled = true,
                    PhysicalAddress = "tuner.local",
                    AdvertisedBaseUrl = "http://lineup.example"
                }
            ]
        });
        return settings;
    }

    private static BackgroundService CreateService(bool ssdp, IAppSettingsService settings, out ListenerState listener, TimeSpan? restartDelay = null)
    {
        listener = new ListenerState();
        var profiles = Substitute.For<IHdHomeRunProxyProfileProvider>();
        var deviceState = Substitute.For<IDeviceStateService>();
        listener.ProfileProvider = profiles;
        listener.DeviceState = deviceState;
        return ssdp
            ? new TestSsdpDiscoveryService(profiles, settings, deviceState, listener, restartDelay ?? TimeSpan.Zero)
            : new TestHdHomeRunDiscoveryService(profiles, settings, deviceState, listener, restartDelay ?? TimeSpan.Zero);
    }

    private static void RaiseSettingsChanged(IAppSettingsService settings)
    {
        settings.OnSettingsChanged += Raise.Event<Action>();
    }

    private sealed class TestHdHomeRunDiscoveryService(IHdHomeRunProxyProfileProvider profiles, IAppSettingsService settings, IDeviceStateService deviceState, ListenerState listener, TimeSpan restartDelay)
        : HdHomeRunDiscoveryService(profiles, settings, deviceState, NullLogger<HdHomeRunDiscoveryService>.Instance)
    {
        protected override TimeSpan ListenerRestartDelay => restartDelay;
        protected override DateTimeOffset UtcNow => listener.UtcNow;

        public Task<IReadOnlyList<HdHomeRunAdvertisedDevice>> ResolveAdvertisedDevicesAsync()
        {
            return GetAdvertisedDevicesAsync(TestContext.Current.CancellationToken);
        }

        public bool AcquirePermit(System.Net.IPAddress sourceAddress)
        {
            return TryAcquireResponsePermit(sourceAddress);
        }

        public async Task<int> ProcessMalformedDatagramAsync()
        {
            var replies = await CreateRepliesAsync(
                new byte[] { 1, 2, 3 },
                System.Net.IPAddress.Loopback,
                TestContext.Current.CancellationToken);
            return replies.Count;
        }

        protected override Task RunListenerSessionAsync(CancellationToken cancellationToken)
        {
            return listener.RunAsync(cancellationToken);
        }
    }

    private sealed class TestSsdpDiscoveryService : SsdpDiscoveryService
    {
        private readonly IAppSettingsService _settings;
        private readonly ListenerState _listener;
        private readonly TimeSpan _restartDelay;

        public TestSsdpDiscoveryService(IHdHomeRunProxyProfileProvider profiles, IAppSettingsService settings, IDeviceStateService deviceState, ListenerState listener, TimeSpan restartDelay)
            : base(profiles, settings, deviceState, NullLogger<SsdpDiscoveryService>.Instance)
        {
            _settings = settings;
            _listener = listener;
            _restartDelay = restartDelay;
        }

        protected override TimeSpan ListenerRestartDelay => _restartDelay;
        protected override DateTimeOffset UtcNow => _listener.UtcNow;

        public Task<IReadOnlyList<HdHomeRunAdvertisedDevice>> ResolveAdvertisedDevicesAsync()
        {
            return GetAdvertisedDevicesAsync(TestContext.Current.CancellationToken);
        }

        public bool AcquirePermit(System.Net.IPAddress sourceAddress)
        {
            return TryAcquireResponsePermit(sourceAddress);
        }

        public async Task<int> ProcessMalformedDatagramAsync()
        {
            var responses = await CreateSearchResponsesAsync("not-httpu", System.Net.IPAddress.Loopback, TestContext.Current.CancellationToken);
            return responses.Count;
        }

        protected override async Task<bool> RunListenerSessionAsync(CancellationToken cancellationToken)
        {
            if (_listener.BeginSessionAttempt())
            {
                return false;
            }

            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await SendAliveNotificationsAsync(sessionCancellation.Token);
            var listenerTask = _listener.RunAsync(sessionCancellation.Token);
            var renewalTask = RunAliveNotificationLoopAsync(sessionCancellation.Token);
            var completedTask = await Task.WhenAny(listenerTask, renewalTask);
            sessionCancellation.Cancel();
            try
            {
                await completedTask;
            }
            finally
            {
                await IgnoreCancellationAsync(listenerTask);
                await IgnoreCancellationAsync(renewalTask);
            }

            return true;
        }

        protected override Task SendAliveNotificationsAsync(CancellationToken cancellationToken)
        {
            _listener.RecordNotification(_settings.Settings.HdHomeRunProxyProfiles[0].AdvertisedBaseUrl);
            return Task.CompletedTask;
        }

        protected override ValueTask WaitForNextAliveNotificationAsync(CancellationToken cancellationToken)
        {
            return _listener.WaitForRenewalAsync(cancellationToken);
        }

        private static async Task IgnoreCancellationAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class ListenerState
    {
        private readonly SemaphoreSlim _starts = new(0);
        private readonly SemaphoreSlim _stops = new(0);
        private readonly SemaphoreSlim _notifications = new(0);
        private readonly SemaphoreSlim _renewals = new(0);
        private readonly SemaphoreSlim _sessionAttempts = new(0);
        private readonly List<string> _advertisedUrls = [];
        private readonly object _notificationGate = new();
        private int _activeCount;
        private int _maximumActiveCount;
        private int _startCount;
        private int _stopCount;
        private int _notificationCount;
        private int _sessionAttemptCount;
        private int _unavailableSessionCount;
        private TaskCompletionSource? _currentCompletion;
        private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public IHdHomeRunProxyProfileProvider ProfileProvider { get; set; } = default!;
        public IDeviceStateService DeviceState { get; set; } = default!;
        public DateTimeOffset UtcNow => _utcNow;

        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int MaximumActiveCount => Volatile.Read(ref _maximumActiveCount);
        public int StartCount => Volatile.Read(ref _startCount);
        public int StopCount => Volatile.Read(ref _stopCount);
        public int NotificationCount => Volatile.Read(ref _notificationCount);
        public int SessionAttemptCount => Volatile.Read(ref _sessionAttemptCount);
        public IReadOnlyList<string> AdvertisedUrls
        {
            get
            {
                lock (_notificationGate)
                {
                    return _advertisedUrls.ToArray();
                }
            }
        }

        public void CompleteCurrent()
        {
            Volatile.Read(ref _currentCompletion)?.TrySetResult();
        }

        public void FailCurrent()
        {
            Volatile.Read(ref _currentCompletion)?.TrySetException(new IOException("Simulated listener failure."));
        }

        public void TriggerRenewal()
        {
            _renewals.Release();
        }

        public void AdvanceClock(TimeSpan duration)
        {
            _utcNow += duration;
        }

        public void MakeNextSessionUnavailable()
        {
            Interlocked.Increment(ref _unavailableSessionCount);
        }

        public bool BeginSessionAttempt()
        {
            Interlocked.Increment(ref _sessionAttemptCount);
            _sessionAttempts.Release();
            return Interlocked.Exchange(ref _unavailableSessionCount, 0) > 0;
        }

        public ValueTask WaitForRenewalAsync(CancellationToken cancellationToken)
        {
            return new ValueTask(_renewals.WaitAsync(cancellationToken));
        }

        public void RecordNotification(string advertisedUrl)
        {
            lock (_notificationGate)
            {
                _advertisedUrls.Add(advertisedUrl);
            }

            Interlocked.Increment(ref _notificationCount);
            _notifications.Release();
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _currentCompletion, completion);
            var active = Interlocked.Increment(ref _activeCount);
            InterlockedExtensions.Max(ref _maximumActiveCount, active);
            Interlocked.Increment(ref _startCount);
            _starts.Release();
            try
            {
                await completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _activeCount);
                Interlocked.Increment(ref _stopCount);
                _stops.Release();
            }
        }

        public async Task WaitForStartsAsync(int count)
        {
            while (StartCount < count)
            {
                await _starts.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }

        public async Task WaitForStopsAsync(int count)
        {
            while (StopCount < count)
            {
                await _stops.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }

        public async Task WaitForNotificationsAsync(int count)
        {
            while (NotificationCount < count)
            {
                await _notifications.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }

        public async Task WaitForSessionAttemptsAsync(int count)
        {
            while (SessionAttemptCount < count)
            {
                await _sessionAttempts.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
