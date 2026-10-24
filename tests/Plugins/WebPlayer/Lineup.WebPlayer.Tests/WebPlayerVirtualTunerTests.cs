using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.WebPlayer.Tests;

/// <summary>Verifies built-in Web Player configuration, planning, sharing, and cleanup.</summary>
public sealed class WebPlayerVirtualTunerTests
{
    /// <summary>Verifies the built-in Web Player exposes a valid empty settings schema.</summary>
    [Fact]
    public void Settings_EmptyDescriptor_AcceptsOnlyPreserveEdits()
    {
        // Arrange
        var driver = new WebPlayerVirtualTunerDriver();
        var configuration = driver.CreateDefaultConfiguration(VirtualDeviceId.New());

        // Act
        var snapshot = driver.GetSettings(configuration);
        var preserved = driver.ApplySettings(configuration, []);
        var rejected = driver.ApplySettings(
            configuration,
            [
                new AdapterSettingEdit
                {
                    Key = "unknown",
                    Action = AdapterSettingEditAction.Set,
                    Value = JsonSerializer.SerializeToElement("value")
                }
            ]);

        // Assert
        Assert.Empty(AdapterSettingsValidator.ValidateDescriptor(driver.SettingsDescriptor));
        Assert.Empty(AdapterSettingsValidator.ValidateSnapshot(driver.SettingsDescriptor, snapshot));
        Assert.True(preserved.Succeeded);
        Assert.False(rejected.Succeeded);
    }

    /// <summary>Verifies initialization does not rewrite an existing Web Player definition.</summary>
    [Fact]
    public void EnsureBuiltIn_ExistingDefinition_DoesNotRewriteSettings()
    {
        // Arrange
        var player = WebPlayerVirtualTunerDriver.CreateBuiltIn() with
        {
            Name = "Legacy Player Name",
            Enabled = false,
            Subscription = new PhysicalDeviceSubscription { Mode = PhysicalDeviceSubscriptionMode.Selected }
        };
        var configuration = new StreamingConfiguration { VirtualDevices = [player] };

        // Act
        var normalized = WebPlayerVirtualTunerDriver.EnsureBuiltIn(configuration);

        // Assert
        var normalizedPlayer = Assert.Single(normalized.VirtualDevices);
        Assert.Equal(player.Name, normalizedPlayer.Name);
        Assert.Equal(player.Id, normalizedPlayer.Id);
        Assert.False(normalizedPlayer.Enabled);
        Assert.Equal(PhysicalDeviceSubscriptionMode.Selected, normalizedPlayer.Subscription.Mode);
    }

    /// <summary>Verifies sparse device settings inherit unspecified global values.</summary>
    [Fact]
    public void Create_SparseOverrides_UsesEffectiveInheritedSettings()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var (configuration, player) = Configuration(
            physicalId,
            new VirtualTranscodeProfile { ConvertHevcToH264 = true, Audio = OutputAudioPolicy.Eac3, MaximumVideoBitRateMbps = 18 },
            new VirtualTranscodeOverrides { Audio = OutputAudioPolicy.Ac3 });

        // Act
        var plan = WebPlayerPlaybackPlanner.Create(configuration, player.Id, physicalId, Tracks("hevc", "aac"), new WebPlayerPlaybackRequest());

        // Assert
        Assert.True(plan.EffectiveProfile.ConvertHevcToH264);
        Assert.Equal(OutputAudioPolicy.Ac3, plan.EffectiveProfile.Audio);
        Assert.Equal(18, plan.EffectiveProfile.MaximumVideoBitRateMbps);
        Assert.True(plan.EncodeVideoAsH264);
        Assert.Equal("ac3", plan.AudioCodec);
    }

    /// <summary>Verifies selected physical subscriptions are enforced by the adapter planner.</summary>
    [Fact]
    public void Create_UnsubscribedPhysicalDevice_RejectsPlayback()
    {
        // Arrange
        var selected = PhysicalDeviceId.New();
        var other = PhysicalDeviceId.New();
        var (configuration, player) = Configuration(selected, new VirtualTranscodeProfile(), new VirtualTranscodeOverrides());

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebPlayerPlaybackPlanner.Create(configuration, player.Id, other, Tracks("h264", "aac"), new WebPlayerPlaybackRequest()));

        // Assert
        Assert.Contains("not subscribed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies browser compatibility and subtitle delivery produce required transforms.</summary>
    [Fact]
    public void Create_IncompatibleCodecsAndBitmapSubtitle_PlansTransforms()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var (configuration, player) = Configuration(physicalId, new VirtualTranscodeProfile(), new VirtualTranscodeOverrides());
        var tracks = Tracks("hevc", "ac4")
            .Append(new WebPlayerMediaTrack { Index = 2, Type = WebPlayerMediaType.Subtitle, Codec = "pgs", SubtitleMode = WebPlayerSubtitleMode.BurnIn })
            .ToArray();
        var request = new WebPlayerPlaybackRequest
        {
            SubtitleTrackIndex = 2,
            Compatibility = new WebPlayerCompatibility
            {
                VideoCodecs = new HashSet<string>(["h264"], StringComparer.OrdinalIgnoreCase),
                AudioCodecs = new HashSet<string>(["aac"], StringComparer.OrdinalIgnoreCase)
            }
        };

        // Act
        var plan = WebPlayerPlaybackPlanner.Create(configuration, player.Id, physicalId, tracks, request);

        // Assert
        Assert.True(plan.EncodeVideoAsH264);
        Assert.True(plan.BurnInSubtitle);
        Assert.Equal("aac", plan.AudioCodec);
    }

    /// <summary>Verifies transform keys share identical outputs and separate materially different requests.</summary>
    [Fact]
    public void Create_TransformKey_ReflectsEveryOutputRequirement()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var (configuration, player) = Configuration(physicalId, new VirtualTranscodeProfile(), new VirtualTranscodeOverrides());
        var tracks = Tracks("h264", "aac");

        // Act
        var first = WebPlayerPlaybackPlanner.Create(configuration, player.Id, physicalId, tracks, new WebPlayerPlaybackRequest());
        var identical = WebPlayerPlaybackPlanner.Create(configuration, player.Id, physicalId, tracks, new WebPlayerPlaybackRequest());
        var scaled = WebPlayerPlaybackPlanner.Create(configuration, player.Id, physicalId, tracks, new WebPlayerPlaybackRequest { MaximumHeight = 720 });

        // Assert
        Assert.Equal(first.TransformKey, identical.TransformKey);
        Assert.NotEqual(first.TransformKey, scaled.TransformKey);
    }

    /// <summary>Verifies equivalent viewers share an output until the final session releases it.</summary>
    [Fact]
    public async Task Coordinator_FinalRelease_CleansSharedOutput()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var key = new StreamTransformKey("web-player-cmaf", "same");
        var owner = new TestOwner();
        var starts = 0;

        // Act
        var first = await coordinator.AcquireAsync(key, _ =>
        {
            starts++;
            return ValueTask.FromResult<IAsyncDisposable>(owner);
        }, TestContext.Current.CancellationToken);
        var second = await coordinator.AcquireAsync(
            key,
            (Func<CancellationToken, ValueTask<IAsyncDisposable>>)(_ => throw new InvalidOperationException("Must share.")),
            TestContext.Current.CancellationToken);
        await first.DisposeAsync();
        var activeAfterFirst = coordinator.ActiveOutputCount;
        await second.DisposeAsync();

        // Assert
        Assert.Equal(first.OutputId, second.OutputId);
        Assert.NotEqual(first.ViewerSessionId, second.ViewerSessionId);
        Assert.Equal(1, starts);
        Assert.Equal(1, activeAfterFirst);
        Assert.Equal(0, coordinator.ActiveOutputCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    /// <summary>Verifies a warm output can be reacquired before its delayed disposal.</summary>
    [Fact]
    public async Task Coordinator_KeepWarmPeriod_ReusesOutputUntilExpiry()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var key = new StreamTransformKey("web-player-cmaf", "warm");
        var owner = new TestOwner();
        var firstViewer = StreamSessionId.New();
        var first = await coordinator.AcquireAsync(
            key,
            firstViewer,
            _ => ValueTask.FromResult(new CmafOutputRegistration(owner, new object())),
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);

        // Act
        await first.DisposeAsync();
        var warmDiagnostics = Assert.Single(coordinator.GetDiagnostics());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var second = await coordinator.AcquireAsync(
            key,
            StreamSessionId.New(),
            _ => throw new InvalidOperationException("The warm output must be reused."),
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken);
        await second.DisposeAsync();
        await Task.Delay(250, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(warmDiagnostics.IsWarm);
        Assert.Equal(0, warmDiagnostics.ViewerCount);
        Assert.Equal(key, warmDiagnostics.TransformKey);
        Assert.Equal(first.OutputId, second.OutputId);
        Assert.Equal(0, coordinator.ActiveOutputCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    /// <summary>Verifies explicit output completion bypasses a configured warm period.</summary>
    [Fact]
    public async Task Coordinator_ReleaseOutput_WithKeepWarmPeriod_DisposesImmediately()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var owner = new TestOwner();
        var lease = await coordinator.AcquireAsync(
            new StreamTransformKey("web-player-cmaf", "completed"),
            StreamSessionId.New(),
            _ => ValueTask.FromResult(new CmafOutputRegistration(owner, new object())),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);

        // Act
        await coordinator.ReleaseOutputAsync(lease.OutputId);

        // Assert
        Assert.Equal(0, coordinator.ActiveOutputCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    /// <summary>Verifies shutdown bypasses a configured warm period.</summary>
    [Fact]
    public async Task Coordinator_Stop_WithKeepWarmPeriod_DisposesImmediately()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var owner = new TestOwner();
        var lease = await coordinator.AcquireAsync(
            new StreamTransformKey("web-player-cmaf", "shutdown"),
            StreamSessionId.New(),
            _ => ValueTask.FromResult(new CmafOutputRegistration(owner, new object())),
            TimeSpan.FromMinutes(5),
            TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        // Act
        await coordinator.StopAsync();

        // Assert
        Assert.Equal(0, coordinator.ActiveOutputCount);
        Assert.Equal(1, owner.DisposeCount);
    }

    /// <summary>Verifies an active viewer touch revalidates expiry and keeps the output alive.</summary>
    [Fact]
    public async Task Coordinator_TryGetViewerTouch_DefeatsPendingExpiry()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator(inactivityTimeout: TimeSpan.FromMilliseconds(200));
        var owner = new TestOwner();
        var lease = await coordinator.AcquireAsync(
            new StreamTransformKey("web-player-cmaf", "touch"),
            _ => ValueTask.FromResult<IAsyncDisposable>(owner),
            TestContext.Current.CancellationToken);

        // Act
        await Task.Delay(150, TestContext.Current.CancellationToken);
        var found = coordinator.TryGetViewer(lease.ViewerSessionId, out var viewer);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(found);
        Assert.NotNull(viewer);
        Assert.Equal(1, coordinator.ActiveOutputCount);
        Assert.Equal(0, owner.DisposeCount);
        await lease.DisposeAsync();
        Assert.Equal(1, owner.DisposeCount);
    }

    /// <summary>Verifies a released startup output disposes its owner before transport state is completed.</summary>
    [Fact]
    public async Task Coordinator_PendingStartupRelease_CleansOwner()
    {
        // Arrange
        await using var coordinator = new CmafOutputCoordinator();
        var owner = new TestOwner();
        var pendingState = new object();
        var lease = await coordinator.AcquireAsync(
            new StreamTransformKey("web-player-cmaf", "starting"),
            _ => ValueTask.FromResult(new CmafOutputRegistration(owner, pendingState)),
            TestContext.Current.CancellationToken);

        // Act
        var released = await coordinator.ReleaseViewerAsync(lease.ViewerSessionId);

        // Assert
        Assert.True(released);
        Assert.Equal(1, owner.DisposeCount);
        Assert.Equal(0, coordinator.ActiveOutputCount);
    }

    /// <summary>Verifies built-in validation rejects missing, duplicate, and non-empty adapter configuration.</summary>
    [Fact]
    public void ValidateBuiltIn_InvalidCardinalityAndConfiguration_ReturnsErrors()
    {
        // Arrange
        var missing = new StreamingConfiguration();
        var invalid = WebPlayerVirtualTunerDriver.CreateBuiltIn() with
        {
            Adapter = new AdapterConfiguration(WebPlayerVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new { unsupported = true }))
        };
        var duplicate = new StreamingConfiguration
        {
            VirtualDevices = [invalid, WebPlayerVirtualTunerDriver.CreateBuiltIn()]
        };

        // Act
        var missingErrors = WebPlayerVirtualTunerDriver.ValidateBuiltIn(missing);
        var duplicateErrors = WebPlayerVirtualTunerDriver.ValidateBuiltIn(duplicate);

        // Assert
        Assert.Contains(missingErrors, error => error.Contains("Exactly one", StringComparison.Ordinal));
        Assert.Contains(duplicateErrors, error => error.Contains("Exactly one", StringComparison.Ordinal));
        Assert.Contains(duplicateErrors, error => error.Contains("does not accept", StringComparison.Ordinal));
    }

    private static (StreamingConfiguration Configuration, VirtualDeviceDefinition Player) Configuration(
        PhysicalDeviceId physicalId,
        VirtualTranscodeProfile defaults,
        VirtualTranscodeOverrides overrides)
    {
        var player = WebPlayerVirtualTunerDriver.CreateBuiltIn() with
        {
            Subscription = new PhysicalDeviceSubscription
            {
                Mode = PhysicalDeviceSubscriptionMode.Selected,
                SelectedDeviceIds = [physicalId]
            },
            TranscodeOverrides = overrides
        };
        return (new StreamingConfiguration { VirtualDefaults = defaults, VirtualDevices = [player] }, player);
    }

    private static IReadOnlyList<WebPlayerMediaTrack> Tracks(string videoCodec, string audioCodec) =>
    [
        new WebPlayerMediaTrack { Index = 0, Type = WebPlayerMediaType.Video, Codec = videoCodec },
        new WebPlayerMediaTrack { Index = 1, Type = WebPlayerMediaType.Audio, Codec = audioCodec, IsDefault = true }
    ];

    private sealed class TestOwner : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
