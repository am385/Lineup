using Bunit;
using Lineup.App.Web.Components.Pages;
using Lineup.Core.VirtualOutputs.Runtime;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Reflection;
using Xunit;

namespace Lineup.App.Web.Tests.Components.Pages;

/// <summary>Exercises recovery against the real Watch lifecycle with controlled time and browser results.</summary>
public partial class WatchTests
{
    /// <summary>Recovery makes a fresh owned request and preserves selected tracks, codec overrides, and browser preferences.</summary>
    [Fact]
    public async Task Reconnect_SourceLoss_StartsFreshSessionAndPreservesPreferences()
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        var preferences = new Watch.CmafPlayerPreferences(0.35, true, "French", "fra", "English", "eng");
        context.JSInterop.Setup<Watch.CmafPlayerPreferences?>("getCmafPlayerPreferences", _ => true).SetResult(preferences);
        context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true).SetResult(new(new("replacement", "/new/master.m3u8", "/new/manifest.mpd"), null));
        var component = RenderPlaying(context);
        SetWatchField(component, "_audioTrack", 5);
        SetWatchField(component, "_subtitleTrack", 7);
        SetWatchField(component, "_subtitlePresentation", SubtitlePresentation.BurnIn);
        SetWatchField(component, "_runtimeVideoOverride", CmafPreferredVideo.Fallback);

        // Act
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.NotNull(component.Find("#cmafReconnecting")), timeout: LineupWebBunitContext.WaitTimeout);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(1));
        await recovering;

        // Assert
        var start = Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession");
        var request = Assert.IsType<CmafStreamRequest>(start.Arguments[1]);
        Assert.Equal(5, request.AudioTrack);
        Assert.Equal(7, request.SubtitleTrack);
        Assert.Equal(SubtitlePresentation.BurnIn, request.SubtitlePresentation);
        Assert.Equal(CmafPreferredVideo.Fallback, request.PreferredVideo);
        Assert.Equal("replacement", GetWatchSession(component).SessionId);
        Assert.Empty(component.FindAll("#cmafReconnecting"));
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "restoreCmafPlayerPreferences" &&
            Equals(invocation.Arguments[1], "replacement") && Equals(invocation.Arguments[2], preferences));
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
    }

    /// <summary>Stop during backoff cancels every automatic request and keeps the UI stopped.</summary>
    [Fact]
    public async Task Reconnect_StopDuringBackoff_CancelsWithoutStarting()
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.Find("button.btn-danger").ClickAsync(new());
        clock.Advance(TimeSpan.FromMinutes(2));
        await recovering;

        // Assert
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession");
        Assert.Empty(component.FindAll("#cmafReconnecting"));
        Assert.Empty(component.FindAll("#cmafVideoPlayer"));
    }

    /// <summary>A matching operator stop still cancels recovery after the failed browser alias has been released.</summary>
    [Fact]
    public async Task Reconnect_OperatorStopDuringBackoff_RemainsTerminal()
    {
        // Arrange
        var registry = Substitute.For<IActiveStreamRegistry>();
        registry.GetActiveStreams().Returns([]);
        await using var context = CreateContext(registry: registry);
        var clock = ConfigureReconnect(context);
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
        var stopped = new ActiveStreamSnapshot("broker-owner", "42.1", HostedStreamFormat.Cmaf, DateTime.UtcNow, null, []) { WorkerMediaSessionId = "session-1" };

        // Act
        registry.StopRequested += Raise.Event<Action<ActiveStreamSnapshot>>(stopped);
        await recovering;
        clock.Advance(TimeSpan.FromMinutes(2));

        // Assert
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession");
        Assert.Empty(component.FindAll("#cmafReconnecting"));
    }

    /// <summary>A pending startup cannot exceed the episode deadline or accept a late session.</summary>
    [Fact]
    public async Task Reconnect_DeadlineDuringStartup_CancelsAndStopsLateOwnedSession()
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        var pending = context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true);
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(1));
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession"), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        clock.Advance(TimeSpan.FromSeconds(59));
        component.WaitForAssertion(() => Assert.Contains("60-second deadline", component.Markup, StringComparison.Ordinal), timeout: LineupWebBunitContext.WaitTimeout);
        pending.SetResult(new(new("late", "/late/master.m3u8", "/late/manifest.mpd"), null));
        await recovering;

        // Assert
        Assert.Empty(component.FindAll("#cmafVideoPlayer"));
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "cancelCmafStart");
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "late") &&
            Equals(invocation.Arguments[1], "/api/stream/cmaf/stop/late"));
    }

    /// <summary>The same deadline covers manifest initialization, not just the start HTTP request.</summary>
    [Fact]
    public async Task Reconnect_DeadlineDuringPlayerInitialization_RejectsLateInitialization()
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true).SetResult(new(new("replacement", "/new/master.m3u8", "/new/manifest.mpd"), null));
        var pending = context.JSInterop.Setup<Watch.CmafPlayerResult>("initCmafPlayer", invocation => invocation.Arguments.Any(argument => Equals(argument, "replacement")));
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(1));
        component.WaitForAssertion(() => Assert.Contains(context.JSInterop.Invocations,
            invocation => invocation.Identifier == "initCmafPlayer" && invocation.Arguments.Any(argument => Equals(argument, "replacement"))), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        clock.Advance(TimeSpan.FromSeconds(59));
        component.WaitForAssertion(() => Assert.Contains("60-second deadline", component.Markup, StringComparison.Ordinal), timeout: LineupWebBunitContext.WaitTimeout);
        pending.SetResult(new(true, null, "/new/manifest.mpd"));
        await recovering;

        // Assert
        Assert.Empty(component.FindAll("#cmafVideoPlayer"));
        Assert.Empty(component.FindAll("#cmafReconnecting"));
        Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "replacement"));
    }

    /// <summary>A newer route cancels recovery startup, releases its late session, and wins over stale completion.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_NewerRouteSupersedesPendingStartup(bool lateFailure)
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        var pending = context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true);
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
        clock.Advance(TimeSpan.FromSeconds(1));
        component.WaitForAssertion(() => Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession"), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        component.Render(parameters => parameters.Add(page => page.ChannelNumber, "43.1"));
        pending.SetResult(lateFailure ? new(null, new("Stale permanent failure.", "protected_content", false)) : new(new("late", "/late/master.m3u8", "/late/manifest.mpd"), null));
        await recovering;

        // Assert
        component.WaitForAssertion(() =>
        {
            Assert.Equal(2, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "startCmafSession"));
            Assert.Equal("/api/stream/cmaf/start/43.1", context.JSInterop.Invocations.Last(invocation => invocation.Identifier == "startCmafSession").Arguments[0]);
            Assert.Equal("session-1", GetWatchSession(component).SessionId);
            Assert.Empty(component.FindAll("#cmafReconnecting"));
        }, timeout: LineupWebBunitContext.WaitTimeout);
        Assert.DoesNotContain("Stale permanent failure.", component.Markup, StringComparison.Ordinal);
        if (!lateFailure) Assert.Contains(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "late"));
    }

    /// <summary>Repeated classified startup failures use all three delays and terminate without a fourth request.</summary>
    [Fact]
    public async Task Reconnect_ThreeTransientFailures_ExhaustsBudget()
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true).SetResult(new(null, new("Source offline.", "source_unavailable", true)));
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);

        // Act
        for (var attempt = 0; attempt < 3; attempt++)
        {
            component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
            clock.Advance(TimeSpan.FromSeconds(1 << attempt));
            var expected = attempt + 1;
            component.WaitForAssertion(() => Assert.Equal(expected, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "tryStartCmafSession")), timeout: LineupWebBunitContext.WaitTimeout);
        }
        await recovering;
        clock.Advance(TimeSpan.FromMinutes(2));

        // Assert
        Assert.Equal(3, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "tryStartCmafSession"));
        Assert.Contains("Automatic reconnect stopped", component.Markup, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("#cmafReconnecting"));
        Assert.Empty(component.FindAll("#cmafVideoPlayer"));
    }

    /// <summary>A permanent failure ends recovery after one request, regardless of an erroneous retry flag.</summary>
    [Theory]
    [InlineData("protected_content")]
    [InlineData("source_invalid")]
    [InlineData("source_not_found")]
    public async Task Reconnect_PermanentStartupFailure_DoesNotRetry(string code)
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true).SetResult(new(null, new("Permanent failure.", code, true)));
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        clock.Advance(TimeSpan.FromSeconds(1));
        await recovering;
        clock.Advance(TimeSpan.FromMinutes(2));

        // Assert
        Assert.Single(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession");
        Assert.Contains("Permanent failure.", component.Markup, StringComparison.Ordinal);
        Assert.Empty(component.FindAll("#cmafReconnecting"));
    }

    /// <summary>Disposal cancels the owned backoff and never restarts media after the component leaves.</summary>
    [Fact]
    public async Task Reconnect_DisposeDuringBackoff_CancelsRecovery()
    {
        // Arrange
        await using var context = CreateContext();
        var clock = ConfigureReconnect(context);
        var component = RenderPlaying(context);
        var recovering = BeginRecoveryAsync(component);
        component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);

        // Act
        await component.InvokeAsync(() => component.Instance.DisposeAsync().AsTask());
        await recovering;
        clock.Advance(TimeSpan.FromMinutes(2));

        // Assert
        Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "tryStartCmafSession");
        Assert.Equal(0, clock.PendingTimers);
    }

    /// <summary>Only a known replacement generation recovers worker loss; quarantine remains terminal.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_WorkerGenerationLoss_DoesNotBypassQuarantine(bool quarantined)
    {
        // Arrange
        var configuration = CreateHostedWebPlayerSettings();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(configuration);
        await using var context = CreateContext(settingsService: settings);
        var clock = ConfigureReconnect(context, management: false);
        var runtime = new VirtualOutputRuntimeStatus();
        var worker = new VirtualOutputRuntimeSnapshot { VirtualDeviceId = configuration.Streaming.VirtualDevices[0].Id, Generation = 1, State = VirtualOutputRuntimeState.Healthy };
        runtime.Set(worker);
        context.Services.AddSingleton<IVirtualOutputRuntimeStatus>(runtime);
        context.JSInterop.Setup<Watch.CmafStartAttempt>("tryStartCmafSession", _ => true).SetResult(new(new("replacement", "/new/master.m3u8", "/new/manifest.mpd"), null));
        var component = RenderPlaying(context);
        runtime.Set(worker with { Generation = quarantined ? 1 : 2, State = quarantined ? VirtualOutputRuntimeState.Failed : VirtualOutputRuntimeState.Healthy });

        // Act
        var failure = component.InvokeAsync(() => component.Instance.OnCmafPlayerEvent(new("error", "Worker connection lost.", null, "session-1")));
        if (!quarantined)
        {
            component.WaitForAssertion(() => Assert.Equal(2, clock.PendingTimers), timeout: LineupWebBunitContext.WaitTimeout);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await failure;

        // Assert
        Assert.Equal(quarantined ? 0 : 1, context.JSInterop.Invocations.Count(invocation => invocation.Identifier == "tryStartCmafSession"));
        Assert.Empty(component.FindAll("#cmafReconnecting"));
        if (quarantined) Assert.Contains("Worker connection lost.", component.Markup, StringComparison.Ordinal);
        else
        {
            Assert.Equal("replacement", GetWatchSession(component).SessionId);
            Assert.DoesNotContain(context.JSInterop.Invocations, invocation => invocation.Identifier == "stopCmafSession" && Equals(invocation.Arguments[0], "session-1"));
        }
    }

    private static ReconnectClock ConfigureReconnect(BunitContext context, bool management = true)
    {
        var clock = new ReconnectClock();
        context.Services.AddSingleton<TimeProvider>(clock);
        if (management)
        {
            var settings = Substitute.For<ILineupSettingsService>();
            settings.Settings.Returns(CreateHostedWebPlayerSettings(dedicated: false));
            context.Services.AddSingleton(settings);
        }
        ConfigureSuccessfulSession(context);
        context.JSInterop.Setup<Watch.CmafPlayerPreferences?>("getCmafPlayerPreferences", _ => true).SetResult(null);
        context.JSInterop.SetupVoid("restoreCmafPlayerPreferences", _ => true).SetVoidResult();
        context.JSInterop.SetupVoid("stopCmafSession", _ => true).SetVoidResult();
        return clock;
    }

    private static IRenderedComponent<Watch> RenderPlaying(BunitContext context)
    {
        var component = context.Render<Watch>(parameters => parameters.Add(page => page.ChannelNumber, "42.1"));
        component.WaitForAssertion(() => Assert.True((bool)typeof(Watch).GetField("_hasStartedPlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Instance)!),
            timeout: LineupWebBunitContext.WaitTimeout);
        return component;
    }

    private static Watch.CmafStartResponse GetWatchSession(IRenderedComponent<Watch> component) =>
        Assert.IsType<Watch.CmafStartResponse>(typeof(Watch).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component.Instance));

    private static void SetWatchField(IRenderedComponent<Watch> component, string field, object value) =>
        typeof(Watch).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component.Instance, value);

    private static Task BeginRecoveryAsync(IRenderedComponent<Watch> component) =>
        component.InvokeAsync(async () => await (Task<bool>)typeof(Watch).GetMethod("TryReconnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(component.Instance, [GetWatchSession(component), new CmafPlaybackStatus(false, "source_eof", true)])!);

    private sealed class ReconnectClock : TimeProvider
    {
        private readonly List<OneShotTimer> _timers = [];
        private long _timestamp;
        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        /// <inheritdoc />
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        /// <summary>Gets the pending timers.</summary>
        internal int PendingTimers { get { lock (_timers) return _timers.Count; } }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            var timer = new OneShotTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>Executes advance.</summary>
        /// <param name="duration">The duration value.</param>
        internal void Advance(TimeSpan duration)
        {
            OneShotTimer[] due;
            lock (_timers)
            {
                _timestamp += duration.Ticks;
                due = _timers.Where(timer => timer.Due <= _timestamp).ToArray();
                foreach (var timer in due) _timers.Remove(timer);
            }
            foreach (var timer in due) timer.Fire();
        }

        private sealed class OneShotTimer(ReconnectClock owner, TimerCallback callback, object? state) : ITimer
        {
            /// <summary>Gets or sets the due.</summary>
            internal long Due { get; private set; }
            /// <summary>Executes fire.</summary>
            internal void Fire() => callback(state);
            /// <inheritdoc />
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._timers)
                {
                    owner._timers.Remove(this);
                    if (dueTime != Timeout.InfiniteTimeSpan)
                    {
                        Due = owner._timestamp + dueTime.Ticks;
                        owner._timers.Add(this);
                    }
                }
                return true;
            }
            /// <inheritdoc />
            public void Dispose() { lock (owner._timers) owner._timers.Remove(this); }
            /// <inheritdoc />
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
