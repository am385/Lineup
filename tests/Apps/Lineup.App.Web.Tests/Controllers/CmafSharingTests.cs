using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Lineup.App.Web.Controllers;
using Lineup.App.Web.Services.Playback;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Controllers;

/// <summary>Verifies typed management playback and its HTTP adapter with real coordinator lifetimes, storage, and encoder processes.</summary>
public sealed class CmafSharingTests
{
    /// <summary>Real input EOF/failure reaches passive viewer status without reviving an explicitly stopped peer.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_EstablishedSourceLoss_ClassifiesSurvivingViewer(bool failed)
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginServiceStart("stopped");
        var stopped = Assert.IsType<ManagementPlaybackPresentation>((await first.Start).Presentation);
        var second = fixture.BeginServiceStart("survivor");
        var survivor = Assert.IsType<ManagementPlaybackPresentation>((await second.Start).Presentation);
        Assert.True(first.Service.Stop(stopped.SessionId));

        // Act
        fixture.EndSource(failed ? new IOException("Synthetic source read failure.") : null);
        await WaitUntilAsync(() => !second.Service.GetPlaybackStatus(survivor.SessionId).IsActive);
        var status = second.Service.GetPlaybackStatus(survivor.SessionId);

        // Assert
        Assert.True(status.Retryable);
        Assert.Equal(failed ? "source_failure" : "source_eof", status.Code);
        Assert.False(first.Service.GetPlaybackStatus(stopped.SessionId).Retryable);
        await WaitUntilAsync(() => fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning);
    }

    /// <summary>Synthetic presentations also include their immutable quality/media plan in sharing identity.</summary>
    [Fact]
    public async Task Service_ProtectedSlates_DifferentPlansDoNotShare()
    {
        // Arrange
        await using var fixture = new Fixture { ProtectedContent = true };
        fixture.Settings.ProtectedContentMode = ProtectedContentMode.StreamSlate;
        var first = fixture.BeginServiceStart("first");
        await first.Start;

        // Act
        var second = fixture.BeginServiceStart("second", new CmafStreamRequest { Quality = WebPlayerQuality.Low });
        var result = await second.Start;

        // Assert
        Assert.NotNull(result.Presentation);
        Assert.Equal(2, fixture.EncoderCount);
        Assert.Equal(2, fixture.Outputs.ActiveOutputCount);
    }

    /// <summary>A pending or warm output from a replaced adapter generation must not accept viewers for the new runtime.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_RuntimeReplacement_DoesNotJoinPendingOrWarmProducer(bool pending)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = pending };
        fixture.Settings.Streaming = fixture.Settings.Streaming with { KeepWarmPeriodSeconds = 60 };
        var first = fixture.BeginServiceStart("old-generation");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (!pending)
        {
            var old = Assert.IsType<ManagementPlaybackPresentation>((await first.Start).Presentation);
            first.Service.Stop(old.SessionId);
        }

        // Act
        fixture.PhysicalRuntime = Substitute.For<IPhysicalTunerDevice>();
        var second = fixture.BeginServiceStart("new-generation");
        await WaitUntilAsync(() => fixture.EncoderCount == 2);
        fixture.PublishReadiness();
        var result = await second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await first.Start;

        // Assert
        Assert.NotNull(result.Presentation);
        Assert.Equal(2, fixture.EncoderCount);
        Assert.Equal(2, fixture.Outputs.ActiveOutputCount);
    }

    /// <summary>Verifies typed application startup shares producers and independently owns viewers without constructing an MVC controller.</summary>
    [Fact]
    public async Task Service_EquivalentViewers_ShareProducerAndStopIndependently()
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginServiceStart("first");
        var firstResult = await first.Start;
        var firstPresentation = Assert.IsType<ManagementPlaybackPresentation>(firstResult.Presentation);
        var second = fixture.BeginServiceStart("second");
        var secondPresentation = Assert.IsType<ManagementPlaybackPresentation>((await second.Start).Presentation);
        var producer = Assert.IsType<ManagementPlaybackService.PendingCmafOutput>(fixture.Outputs.GetViewers().First().State);

        // Act
        first.Cancellation.Cancel();
        Assert.True(first.Service.Stop(firstPresentation.SessionId));

        // Assert
        Assert.Same(first.Service, second.Service);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.NotEqual(firstPresentation.SessionId, secondPresentation.SessionId);
        Assert.False(first.Service.TryGetArtifacts(firstPresentation.SessionId, out _));
        Assert.True(second.Service.TryGetArtifacts(secondPresentation.SessionId, out _));
        Assert.Single(second.Service.GetViewers());
        Assert.True(fixture.IsEncoderRunning);
        Assert.False(producer.LifetimeToken.IsCancellationRequested);

        // Act
        Assert.True(second.Service.Stop(secondPresentation.SessionId));
        await producer.Stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(producer.StartupCompletion.IsCompletedSuccessfully);
        Assert.False(fixture.IsEncoderRunning);
        Assert.Equal(0, fixture.ActiveInputs);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies application readiness survives the initiating viewer's cancellation or explicit stop without an HTTP request.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_InitiatorLeavesBeforeReadiness_SurvivorReceivesPresentation(bool cancel)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginServiceStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginServiceStart("survivor");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        var producer = Assert.IsType<ManagementPlaybackService.PendingCmafOutput>(fixture.Outputs.GetViewers().First().State);

        // Act
        if (cancel)
        {
            first.Cancellation.Cancel();
        }
        else
        {
            Assert.Equal(1, fixture.ActiveStreams.RequestStopByClientId("owner"));
        }
        var departed = await first.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.PublishReadiness();
        var surviving = await second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(departed.IsCanceled);
        Assert.NotNull(surviving.Presentation);
        Assert.False(producer.LifetimeToken.IsCancellationRequested);
        Assert.Equal("survivor", Assert.Single(fixture.ActiveStreams.GetActiveStreams()).ClientId);
        Assert.Single(second.Service.GetViewers());
        Assert.Equal(1, fixture.EncoderCount);
        Assert.True(second.Service.Stop(surviving.Presentation.SessionId));
        Assert.True(producer.Stopped.IsCompletedSuccessfully);
        Assert.Equal(0, fixture.ActiveInputs);
    }

    /// <summary>Verifies application-owned inactivity expiry preserves a renewed viewer and releases every task after the final expiry.</summary>
    [Fact]
    public async Task Service_InactivityExpiry_ReleasesOnlyExpiredViewerThenProducer()
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginServiceStart("idle");
        var firstPresentation = Assert.IsType<ManagementPlaybackPresentation>((await first.Start).Presentation);
        var second = fixture.BeginServiceStart("active");
        var secondPresentation = Assert.IsType<ManagementPlaybackPresentation>((await second.Start).Presentation);
        var producer = Assert.IsType<ManagementPlaybackService.PendingCmafOutput>(fixture.Outputs.GetViewers().First().State);

        // Act
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(second.Service.TryGetArtifacts(secondPresentation.SessionId, out _));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 1);

        // Assert
        Assert.False(first.Service.TryGetArtifacts(firstPresentation.SessionId, out _));
        Assert.Equal(secondPresentation.SessionId, Assert.Single(second.Service.GetViewers()).SessionId);
        Assert.True(fixture.IsEncoderRunning);

        // Act
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
        await producer.Stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(second.Service.GetViewers());
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Equal(0, fixture.ActiveInputs);
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies application startup exposes typed sanitized failures and releases its media resources without an MVC response.</summary>
    [Theory]
    [InlineData("process", ManagementPlaybackFailureKind.MediaFailure, "media_process_failed")]
    [InlineData("input", ManagementPlaybackFailureKind.MediaFailure, "media_source_failed")]
    [InlineData("timeout", ManagementPlaybackFailureKind.Timeout, "cmaf_startup_timeout")]
    public async Task Service_StartupFailure_ReturnsTypedFailureAndCleansResources(string failure, ManagementPlaybackFailureKind kind, string code)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            HoldReadiness = true,
            ProcessFailure = failure == "process" ? new MediaProcessException("PRIVATE diagnostics") : null,
            InputFailure = failure == "input" ? new IOException("Upstream failed.") : null,
            StartupTimeout = failure == "timeout" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(30)
        };
        var started = fixture.BeginServiceStart("viewer");

        // Act
        var result = await started.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory && fixture.ActiveInputs == 0);

        // Assert
        var error = Assert.IsType<ManagementPlaybackFailure>(result.Failure);
        Assert.Equal(kind, error.Kind);
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("PRIVATE", error.Error, StringComparison.Ordinal);
        Assert.Null(result.Presentation);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies known protected playback is application-owned synthetic output and never acquires physical input.</summary>
    [Fact]
    public async Task Service_ProtectedSlate_UsesSyntheticProducerWithoutPhysicalInput()
    {
        // Arrange
        await using var fixture = new Fixture { ProtectedContent = true };
        fixture.Settings.ProtectedContentMode = ProtectedContentMode.StreamSlate;
        var started = fixture.BeginServiceStart("slate");

        // Act
        var result = await started.Start;
        var presentation = Assert.IsType<ManagementPlaybackPresentation>(result.Presentation);
        Assert.True(started.Service.Stop(presentation.SessionId));

        // Assert
        fixture.AssertNoPhysicalInput();
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Empty(fixture.Outputs.GetViewers());
    }

    /// <summary>Verifies live acquisition protection enters the synthetic startup path without hiding mixed failures or ReturnError.</summary>
    /// <param name="useSlate">Whether protected sources should produce a slate.</param>
    /// <param name="mixedFailure">Whether another candidate failed for a non-protection reason.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Service_LiveProtection_AppliesSlatePolicy(bool useSlate, bool mixedFailure)
    {
        // Arrange
        await using var fixture = new Fixture();
        fixture.Settings.ProtectedContentMode = useSlate ? ProtectedContentMode.StreamSlate : ProtectedContentMode.ReturnError;
        fixture.AcquisitionFailure = CreateAcquisitionFailure(
            fixture.PhysicalId, mixedFailure ? PhysicalStreamFailureCategory.Unavailable : PhysicalStreamFailureCategory.ProtectedContent);
        var started = fixture.BeginServiceStart("live-protection");

        // Act
        var result = await started.Start;

        // Assert
        if (useSlate && !mixedFailure)
        {
            Assert.Null(result.Failure);
            var presentation = Assert.IsType<ManagementPlaybackPresentation>(result.Presentation);
            Assert.Empty(presentation.Subtitles);
            var active = Assert.Single(fixture.ActiveStreams.GetActiveStreams());
            Assert.Equal("Protected content slate", active.Flow?.SyntheticSource);
            Assert.Null(active.Flow?.PhysicalDeviceId);
            fixture.AssertNoPhysicalInput(acquisitionAttempted: true);
            Assert.True(started.Service.Stop(presentation.SessionId));
        }
        else
        {
            Assert.Null(result.Presentation);
            var failure = Assert.IsType<ManagementPlaybackFailure>(result.Failure);
            Assert.Equal(ManagementPlaybackFailureKind.SourceAcquisition, failure.Kind);
            var acquisition = Assert.IsType<StreamAcquisitionException>(failure.Acquisition);
            Assert.Same(fixture.AcquisitionFailure, acquisition);
            Assert.Equal(mixedFailure ? "source_unavailable" : "protected_content", acquisition.Code);
            Assert.Equal(0, fixture.EncoderCount);
        }
        Assert.Equal(0, fixture.ActiveInputs);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies cancellation and slate generation failures cannot turn a protected acquisition into successful playback.</summary>
    /// <param name="cancel">Whether cancellation wins over the protected-content result.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_LiveProtection_CancellationAndSlateFailureRemainExplicit(bool cancel)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            CancelDuringAcquisition = cancel,
            ProcessFailure = cancel ? null : new MediaProcessException("PRIVATE slate generation failure.")
        };
        fixture.Settings.ProtectedContentMode = ProtectedContentMode.StreamSlate;
        fixture.AcquisitionFailure = CreateAcquisitionFailure(fixture.PhysicalId, PhysicalStreamFailureCategory.ProtectedContent);
        var started = fixture.BeginServiceStart("failed-slate");
        ManagementPlaybackStartResult? result = null;

        // Act
        var failure = await Record.ExceptionAsync(async () => result = await started.Start);

        // Assert
        if (cancel)
        {
            Assert.IsAssignableFrom<OperationCanceledException>(failure);
        }
        else
        {
            Assert.Null(failure);
            Assert.NotNull(result);
            Assert.Null(result.Presentation);
            var mediaFailure = Assert.IsType<ManagementPlaybackFailure>(result.Failure);
            Assert.Equal("media_process_failed", mediaFailure.Code);
            Assert.DoesNotContain("PRIVATE", mediaFailure.Error, StringComparison.Ordinal);
        }
        Assert.Equal(0, fixture.EncoderCount);
        Assert.Equal(0, fixture.ActiveInputs);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies disabled-channel policy remains a typed application decision without acquiring physical input.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_DisabledChannel_PreservesErrorOrSlatePolicy(bool useSlate)
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.DisableChannelAsync();
        fixture.Settings.DisabledChannelMode = useSlate ? DisabledChannelMode.StreamSlate : DisabledChannelMode.ReturnError;
        var started = fixture.BeginServiceStart("disabled");

        // Act
        var result = await started.Start;

        // Assert
        if (useSlate)
        {
            var presentation = Assert.IsType<ManagementPlaybackPresentation>(result.Presentation);
            Assert.True(started.Service.Stop(presentation.SessionId));
            fixture.AssertNoPhysicalInput(ChannelSlateReason.DisabledChannel);
        }
        else
        {
            Assert.Equal(ManagementPlaybackFailureKind.Disabled, Assert.IsType<ManagementPlaybackFailure>(result.Failure).Kind);
            Assert.Equal(0, fixture.EncoderCount);
            Assert.Equal(0, fixture.ActiveInputs);
        }
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies independently counted viewer admission still applies when equivalent plans share one producer.</summary>
    [Fact]
    public async Task Service_ViewerAdmissionLimit_RejectsBeforeCreatingAnotherProducer()
    {
        // Arrange
        await using var fixture = new Fixture();
        await fixture.BeginServiceStart("first").Start;
        await fixture.BeginServiceStart("second").Start;

        // Act
        var rejected = await fixture.BeginServiceStart("third").Start;

        // Assert
        Assert.Equal(ManagementPlaybackFailureKind.AdmissionLimit, Assert.IsType<ManagementPlaybackFailure>(rejected.Failure).Kind);
        Assert.Equal(2, fixture.Outputs.GetViewers().Count);
        Assert.Equal(2, fixture.ActiveStreams.GetActiveStreams().Count);
        Assert.Equal(1, fixture.EncoderCount);
    }

    /// <summary>Verifies both caption and media pumps are owned and terminal before the service reports producer cleanup complete.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_EmbeddedCaptions_StopObservesEveryOwnedTask(bool captionInputFails)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            CaptionInputFailure = captionInputFails ? new IOException("Caption input failed.") : null,
            ProbeOverride = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", null, null, null, 2, 48_000),
                new MediaTrackMetadata(2, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
                {
                    SubtitlePresentation = SubtitlePresentation.WebVtt,
                    IsEmbeddedClosedCaptions = true
                }
            ], null)
        };
        var started = fixture.BeginServiceStart("captions");
        var result = await started.Start;
        var presentation = Assert.IsType<ManagementPlaybackPresentation>(result.Presentation);
        var producer = Assert.IsType<ManagementPlaybackService.PendingCmafOutput>(Assert.Single(fixture.Outputs.GetViewers()).State);

        // Act
        Assert.True(started.Service.Stop(presentation.SessionId));
        await producer.Stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await producer.Tasks.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, fixture.EncoderCount);
        Assert.True(Assert.Single(presentation.Subtitles).IsEmbeddedClosedCaptions);
        Assert.False(fixture.IsEncoderRunning);
        Assert.Equal(0, fixture.ActiveInputs);
        Assert.False(fixture.HasEncoderDirectory);
    }

    /// <summary>Verifies known protected channels start a local slate without acquiring or probing physical input.</summary>
    [Fact]
    public async Task Start_KnownProtectedSlate_StartsSyntheticProducerWithoutPhysicalInput()
    {
        // Arrange
        await using var fixture = new Fixture { ProtectedContent = true };
        fixture.Settings.ProtectedContentMode = ProtectedContentMode.StreamSlate;
        var started = fixture.BeginStart("slate-viewer");

        // Act
        var response = await ResponseAsync(started.Start);
        var viewers = fixture.Outputs.GetViewers().ToArray();
        using (var fragment = Assert.IsType<FileStreamResult>(started.Controller.GetCmafFile(response.SessionId, "chunk-0-00001.m4s")).FileStream)
        {
            Assert.True(fragment.CanRead);
        }
        var stopped = started.Controller.StopCmaf(response.SessionId);

        // Assert
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(0, fixture.ActiveInputs);
        Assert.Single(viewers);
        fixture.AssertNoPhysicalInput();
        Assert.IsType<OkObjectResult>(stopped);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies actual management startup copies H.264 and already-normalized HEVC while preserving conversion and overrides.</summary>
    [Theory]
    [InlineData("h264", false, null, "copy")]
    [InlineData("hevc", false, null, "libx264")]
    [InlineData("HEVC", false, null, "libx264")]
    [InlineData("hevc", true, null, "copy")]
    [InlineData("hevc", false, CmafPreferredVideo.Source, "copy")]
    [InlineData("h264", false, CmafPreferredVideo.Fallback, "libx264")]
    public async Task Start_HevcDefault_UsesEffectiveCodecAndOverrides(string rawCodec, bool physicallyConverted, CmafPreferredVideo? videoOverride, string encoder)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            ProbeOverride = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, rawCodec, null, 1280, 720, null, null) { ScanType = VideoScanType.Progressive, Profile = "Main 10", Level = 123 },
                new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", null, null, null, 2, 48_000)
            ], null)
        };
        fixture.Settings.Streaming = fixture.Settings.Streaming with
        {
            PhysicalDefaults = new PhysicalTranscodeProfile { ConvertHevcToH264 = physicallyConverted },
            VirtualDefaults = new VirtualTranscodeProfile { ConvertHevcToH264 = true }
        };

        // Act
        var started = fixture.BeginStart("viewer", new CmafStreamRequest
        {
            PreferredVideo = CmafPreferredVideo.Source,
            Overrides = new CmafStreamOverrides { Enabled = videoOverride.HasValue, Video = videoOverride }
        });
        Assert.IsType<OkObjectResult>(await started.Start);

        // Assert
        var arguments = Assert.Single(fixture.EncoderRequests).Arguments;
        var index = arguments.ToList().IndexOf("-c:v:0");
        Assert.True(index >= 0);
        Assert.Equal(encoder, arguments[index + 1]);
        Assert.Equal(physicallyConverted ? "h264" : rawCodec,
            Assert.Single(fixture.ActiveStreams.GetActiveStreams()).PhysicalOutput!.Tracks.Single(track => track.Type == MediaTrackType.Video).Codec);
    }

    /// <summary>Verifies advertised-but-undiscovered audio is explicit while legitimate declared video-only startup remains supported.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_VideoOnlyProbe_DistinguishesAdvertisedAudio(bool expectsAudio)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            AudioHint = expectsAudio ? "AC4" : "none",
            ProbeOverride = new MediaProbeResult([new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, 1280, 720, null, null)], null)
        };
        var request = fixture.BeginStart("viewer");

        // Act
        var result = await request.Start;

        // Assert
        if (expectsAudio)
        {
            var failure = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status502BadGateway, failure.StatusCode);
            Assert.Contains("media_audio_missing", System.Text.Json.JsonSerializer.Serialize(failure.Value), StringComparison.Ordinal);
            Assert.Equal(0, fixture.EncoderCount);
        }
        else
        {
            Assert.IsType<OkObjectResult>(result);
        }
    }

    /// <summary>Verifies conversion failures produce a structured gateway error without returning private processor output.</summary>
    [Fact]
    public async Task Start_ProcessorFails_ReturnsSanitizedStructuredFailure()
    {
        // Arrange
        await using var fixture = new Fixture { ProcessFailure = new MediaProcessException("PRIVATE processor detail at http://internal.invalid/source") };
        var request = fixture.BeginStart("viewer", startupTimeout: TimeSpan.FromSeconds(30));

        // Act
        var result = Assert.IsType<ObjectResult>(await request.Start);

        // Assert
        Assert.Equal(StatusCodes.Status502BadGateway, result.StatusCode);
        var body = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("media_process_failed", body, StringComparison.Ordinal);
        Assert.Contains("process", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", body, StringComparison.Ordinal);
        Assert.DoesNotContain("internal.invalid", body, StringComparison.Ordinal);
    }

    /// <summary>Verifies a terminal upstream subscription failure is returned before the unrelated presentation-readiness deadline.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Start_UpstreamFails_ReturnsTerminalFailurePromptly(bool processFailure)
    {
        // Arrange
        await using var fixture = new Fixture
        {
            HoldReadiness = true,
            InputFailure = processFailure ? new MediaProcessException("Upstream FFmpeg exited with code 234.") : new IOException("Upstream stream failed.")
        };
        var first = fixture.BeginStart("owner", startupTimeout: TimeSpan.FromSeconds(30));

        // Act
        var result = await first.Start.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(StatusCodes.Status502BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
        Assert.Empty(fixture.Outputs.GetDiagnostics());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies distinct viewers share media but retain separate URLs, diagnostics, and stop ownership.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_DifferentViewers_ShareProducerAndStopIndependently(bool stopInitiator)
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginStart("viewer-a");
        var firstResponse = await ResponseAsync(first.Start);
        var second = fixture.BeginStart("viewer-b");
        var secondResponse = await ResponseAsync(second.Start);

        // Act
        first.Cancellation.Cancel();
        using var firstFragment = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(first.Controller.GetCmafFile(firstResponse.SessionId, "chunk-0-00001.m4s")).FileStream);
        using var secondFragment = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "chunk-0-00001.m4s")).FileStream);

        // Assert
        Assert.Equal(1, fixture.EncoderCount);
        var output = Assert.Single(fixture.Outputs.GetDiagnostics());
        Assert.Equal(2, output.ViewerCount);
        Assert.Equal(1, fixture.ActiveInputs);
        Assert.Equal(firstFragment.Name, secondFragment.Name);
        Assert.NotEqual(firstResponse.SessionId, secondResponse.SessionId);
        Assert.Equal($"/api/stream/cmaf/{firstResponse.SessionId}/manifest.mpd", firstResponse.DashManifestUrl);
        Assert.Equal($"/api/stream/cmaf/{secondResponse.SessionId}/master.m3u8", secondResponse.HlsManifestUrl);
        Assert.Equal($"/api/stream/cmaf/{secondResponse.SessionId}/captions-2.vtt", Assert.Single(secondResponse.Subtitles).Url);
        Assert.IsType<FileContentResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "captions-2.vtt"));
        var active = fixture.ActiveStreams.GetActiveStreams();
        Assert.Equal(["viewer-a", "viewer-b"], active.Select(stream => stream.ClientId).Order());
        Assert.Equal(2, active.Select(stream => stream.ClientAddress).Distinct().Count());
        Assert.All(active, stream => Assert.Equal(output.OutputId, stream.Flow!.ReusableOutputId));

        // Act
        var stopped = stopInitiator ? first : second;
        var survivor = stopInitiator ? second : first;
        var stoppedId = stopInitiator ? firstResponse.SessionId : secondResponse.SessionId;
        var survivingId = stopInitiator ? secondResponse.SessionId : firstResponse.SessionId;
        var stop = stopped.Controller.StopCmaf(stoppedId);

        // Assert
        Assert.IsType<OkObjectResult>(stop);
        Assert.IsType<NotFoundObjectResult>(stopped.Controller.GetCmafFile(stoppedId, "manifest.mpd"));
        Assert.IsType<ContentResult>(survivor.Controller.GetCmafFile(survivingId, "manifest.mpd"));
        Assert.Equal(1, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.Single(fixture.ActiveStreams.GetActiveStreams());
        Assert.True(fixture.IsEncoderRunning);
        Assert.True(File.Exists(firstFragment.Name));

        // Act
        Assert.IsType<OkObjectResult>(survivor.Controller.StopCmaf(survivingId));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0);

        // Assert
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Equal(0, fixture.Outputs.ActiveOutputCount);
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(File.Exists(firstFragment.Name));
    }

    /// <summary>Verifies expiry removes only the idle alias and the final expiry releases the producer.</summary>
    [Fact]
    public async Task Start_OneViewerExpires_ActiveViewerKeepsSharedProducer()
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginStart("idle");
        var firstResponse = await ResponseAsync(first.Start);
        var second = fixture.BeginStart("active");
        var secondResponse = await ResponseAsync(second.Start);
        Assert.Single(fixture.Outputs.GetDiagnostics());

        // Act
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.IsType<ContentResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "manifest.mpd"));
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 1);

        // Assert
        Assert.Equal(secondResponse.SessionId, Assert.Single(fixture.Outputs.GetViewers()).ViewerSessionId.ToString());
        Assert.IsType<NotFoundObjectResult>(first.Controller.GetCmafFile(firstResponse.SessionId, "manifest.mpd"));
        Assert.Single(fixture.ActiveStreams.GetActiveStreams());
        Assert.Equal(1, fixture.EncoderCount);
        Assert.True(fixture.IsEncoderRunning);
        Assert.Equal(1, fixture.ActiveInputs);

        // Act
        fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
        await WaitUntilAsync(() => fixture.Outputs.ActiveOutputCount == 0 && fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory);

        // Assert
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
        Assert.False(fixture.IsEncoderRunning);
    }

    /// <summary>Verifies concurrent starts attach to one pending output and receive independent viewer aliases.</summary>
    [Fact]
    public async Task Start_ConcurrentViewers_SharePendingProducer()
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("viewer-a");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Act
        var second = fixture.BeginStart("viewer-b");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        var pendingOutputs = fixture.Outputs.ActiveOutputCount;
        fixture.PublishReadiness();
        var responses = await Task.WhenAll(ResponseAsync(first.Start), ResponseAsync(second.Start));

        // Assert
        Assert.Equal(1, pendingOutputs);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(2, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.NotEqual(responses[0].SessionId, responses[1].SessionId);
        Assert.IsType<ContentResult>(first.Controller.GetCmafFile(responses[0].SessionId, "manifest.mpd"));
        Assert.IsType<ContentResult>(second.Controller.GetCmafFile(responses[1].SessionId, "manifest.mpd"));
    }

    /// <summary>Verifies canceling a joining viewer during startup releases only its alias and preserves the pending producer.</summary>
    [Fact]
    public async Task Start_JoiningViewerCanceled_PreservesPendingProducer()
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("joining");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);

        // Act
        second.Cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Start);
        fixture.PublishReadiness();
        var response = await ResponseAsync(first.Start);

        // Assert
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(1, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.Equal("owner", Assert.Single(fixture.ActiveStreams.GetActiveStreams()).ClientId);
        Assert.True(fixture.IsEncoderRunning);
        Assert.IsType<ContentResult>(first.Controller.GetCmafFile(response.SessionId, "manifest.mpd"));
    }

    /// <summary>Verifies a departing initiator cannot cancel or abandon readiness for another pending viewer.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_InitiatorLeavesBeforeReadiness_SurvivorReceivesReadyProducer(bool cancelRequest)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("survivor");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        var ownerId = fixture.ActiveStreams.GetActiveStreams().Single(stream => stream.ClientId == "owner").SessionId;

        // Act
        if (cancelRequest)
        {
            first.Cancellation.Cancel();
        }
        else
        {
            Assert.True(fixture.ActiveStreams.RequestStop(ownerId));
        }
        Assert.IsType<EmptyResult>(await first.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        fixture.PublishReadiness();
        var response = await ResponseAsync(second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // Assert
        Assert.False(second.Cancellation.IsCancellationRequested);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Equal(1, Assert.Single(fixture.Outputs.GetDiagnostics()).ViewerCount);
        Assert.Equal("survivor", Assert.Single(fixture.ActiveStreams.GetActiveStreams()).ClientId);
        Assert.True(fixture.IsEncoderRunning);
        Assert.IsType<ContentResult>(second.Controller.GetCmafFile(response.SessionId, "manifest.mpd"));
        Assert.IsType<NotFoundObjectResult>(first.Controller.GetCmafFile(ownerId, "manifest.mpd"));

        // Act
        Assert.IsType<OkObjectResult>(second.Controller.StopCmaf(response.SessionId));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0);

        // Assert
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
        Assert.False(fixture.IsEncoderRunning);
        Assert.False(fixture.HasEncoderDirectory);
    }

    /// <summary>Verifies releasing all pending viewers cancels producer work and cleans its input, process, and directory.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_AllViewersLeaveBeforeReadiness_CleansProducer(bool cancelRequests)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner");
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("joining");
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);

        // Act
        if (cancelRequests)
        {
            first.Cancellation.Cancel();
            second.Cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.Start);
        }
        else
        {
            Assert.Equal(1, fixture.ActiveStreams.RequestStopByClientId("owner"));
            Assert.Equal(1, fixture.ActiveStreams.RequestStopByClientId("joining"));
            Assert.IsType<EmptyResult>(await second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        Assert.IsType<EmptyResult>(await first.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory);

        // Assert
        Assert.Equal(0, fixture.Outputs.ActiveOutputCount);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies producer-owned failure or deadline still reaches a surviving viewer after the initiator leaves.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Start_ProducerFailsAfterInitiatorLeaves_SurvivorReceivesFailureAndCleanup(bool encoderExits)
    {
        // Arrange
        await using var fixture = new Fixture { HoldReadiness = true };
        var first = fixture.BeginStart("owner", startupTimeout: TimeSpan.FromSeconds(2));
        await fixture.EncoderStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = fixture.BeginStart("survivor", startupTimeout: TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => fixture.Outputs.GetViewers().Count == 2);
        first.Cancellation.Cancel();
        Assert.IsType<EmptyResult>(await first.Start);

        // Act
        if (encoderExits)
        {
            await fixture.TerminateEncoderAsync();
        }
        var result = Assert.IsType<ObjectResult>(await second.Start.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => fixture.ActiveInputs == 0 && !fixture.IsEncoderRunning && !fixture.HasEncoderDirectory);

        // Assert
        Assert.False(second.Cancellation.IsCancellationRequested);
        Assert.Equal(encoderExits ? StatusCodes.Status502BadGateway : StatusCodes.Status504GatewayTimeout, result.StatusCode);
        Assert.Equal(0, fixture.Outputs.ActiveOutputCount);
        Assert.Empty(fixture.Outputs.GetViewers());
        Assert.Empty(fixture.ActiveStreams.GetActiveStreams());
    }

    /// <summary>Verifies sharing does not bypass the independently counted viewer admission limit.</summary>
    [Fact]
    public async Task Start_ThirdViewer_StillEnforcesViewerLimit()
    {
        // Arrange
        await using var fixture = new Fixture();
        await ResponseAsync(fixture.BeginStart("viewer-a").Start);
        await ResponseAsync(fixture.BeginStart("viewer-b").Start);

        // Act
        var third = await fixture.BeginStart("viewer-c").Start;

        // Assert
        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(third).StatusCode);
        Assert.Equal(2, fixture.ActiveStreams.GetActiveStreams().Count);
        Assert.Equal(2, fixture.Outputs.GetViewers().Count);
        Assert.Equal(1, fixture.EncoderCount);
        Assert.Single(fixture.Outputs.GetDiagnostics());
    }

    /// <summary>Verifies media settings and exact source provenance continue to isolate producers even for the same client identity.</summary>
    [Theory]
    [InlineData("audio")]
    [InlineData("quality")]
    [InlineData("profile")]
    [InlineData("physical")]
    [InlineData("channel")]
    [InlineData("tune")]
    [InlineData("generation")]
    [InlineData("normalization")]
    public async Task Start_DifferentPlanOrSource_DoesNotShare(string change)
    {
        // Arrange
        await using var fixture = new Fixture();
        var first = fixture.BeginStart("same-viewer");
        var firstResponse = await ResponseAsync(first.Start);
        var request = new CmafStreamRequest();
        var channel = "7.1";
        switch (change)
        {
            case "audio": request = request with { PreferredAudio = CmafPreferredAudio.Fallback, FallbackAudio = CmafFallbackAudio.Ac3 }; break;
            case "quality": request = request with { Quality = WebPlayerQuality.Low }; break;
            case "profile": fixture.Settings.Streaming = fixture.Settings.Streaming with { VirtualDefaults = new VirtualTranscodeProfile { VideoQuality = 18 } }; break;
            case "physical": fixture.PhysicalId = PhysicalDeviceId.New(); break;
            case "channel": channel = "9.1"; break;
            case "tune": fixture.TuneKey = "replacement-frequency"; break;
            case "generation": fixture.PhysicalRuntime = Substitute.For<IPhysicalTunerDevice>(); break;
            case "normalization": fixture.Settings.Streaming = fixture.Settings.Streaming with { PhysicalDefaults = new PhysicalTranscodeProfile { VideoQuality = 18 } }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }

        // Act
        var second = fixture.BeginStart("same-viewer", request, channel);
        var secondResponse = await ResponseAsync(second.Start);

        // Assert
        Assert.Equal(2, fixture.EncoderCount);
        Assert.Equal(2, fixture.Outputs.ActiveOutputCount);
        using var firstFile = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(first.Controller.GetCmafFile(firstResponse.SessionId, "chunk-0-00001.m4s")).FileStream);
        using var secondFile = Assert.IsType<FileStream>(Assert.IsType<FileStreamResult>(second.Controller.GetCmafFile(secondResponse.SessionId, "chunk-0-00001.m4s")).FileStream);
        Assert.NotEqual(firstFile.Name, secondFile.Name);
        Assert.Equal(2, fixture.ActiveInputs);
    }

    private static async Task<CmafStreamResponse> ResponseAsync(Task<IActionResult> start) =>
        Assert.IsType<CmafStreamResponse>(Assert.IsType<OkObjectResult>(await start).Value);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5))
            {
                throw new TimeoutException("The expected CMAF sharing state did not become observable.");
            }
            await Task.Delay(10);
        }
    }

    private static StreamAcquisitionException CreateAcquisitionFailure(PhysicalDeviceId preferredId, PhysicalStreamFailureCategory fallbackCategory)
    {
        var failures = new[] { (DeviceId: preferredId, Category: PhysicalStreamFailureCategory.ProtectedContent), (DeviceId: PhysicalDeviceId.New(), Category: fallbackCategory) }
            .Select(candidate => new StreamAcquisitionCandidateFailure
            {
                DeviceId = candidate.DeviceId,
                ChannelId = new SourceChannelId(candidate.DeviceId, "7.1"),
                Category = candidate.Category,
                Code = candidate.Category == PhysicalStreamFailureCategory.ProtectedContent ? "protected_content" : "source_unavailable",
                Message = "Controlled physical source failure.",
                Retryable = candidate.Category != PhysicalStreamFailureCategory.ProtectedContent
            }).ToArray();
        return StreamAcquisitionException.Create(failures, failures.Select(failure => new IOException(failure.Message)).ToArray());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "cmaf-sharing-artifacts", Guid.NewGuid().ToString("N")));
        private readonly ConcurrentQueue<(Process Observer, string Directory)> _encoders = new();
        private readonly ConcurrentQueue<MediaProcessRequest> _encoderRequests = new();
        private readonly TaskCompletionSource _encoderStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<Exception?> _sourceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<CancellationTokenSource> _cancellations = [];
        private readonly List<Task<IActionResult>> _starts = [];
        private readonly List<Task<ManagementPlaybackStartResult>> _serviceStarts = [];
        private readonly ILineupSettingsService _settings = Substitute.For<ILineupSettingsService>();
        private readonly ITunerStreamMultiplexer _multiplexer = Substitute.For<ITunerStreamMultiplexer, IEffectiveTunerStreamMultiplexer>();
        private readonly IMediaProbeService _probe = Substitute.For<IMediaProbeService>();
        private readonly IMediaProcessFactory _processes = Substitute.For<IMediaProcessFactory>();
        private readonly IProtectedContentSlateService _slates = Substitute.For<IProtectedContentSlateService>();
        private readonly IStreamingRouteResolver _routes = Substitute.For<IStreamingRouteResolver>();
        private readonly HttpClient _diagnosticClient = new(new UnavailableDiagnostics());
        private readonly StreamingTopologyStore _topology;
        private readonly TransientDataStore _files;
        private int _activeInputs;
        private int _subscriptions;
        private ManagementPlaybackService? _playback;

        /// <summary>Gets or sets the captured physical generation used by resolved routes.</summary>
        internal IPhysicalTunerDevice PhysicalRuntime { get; set; } = Substitute.For<IPhysicalTunerDevice>();

        /// <summary>Gets or sets the exact physical tune identity.</summary>
        internal string TuneKey { get; set; } = "none";

        /// <summary>Creates isolated SQLite storage, deterministic source capabilities, and real process ownership.</summary>
        internal Fixture()
        {
            _files = new TransientDataStore(_root.FullName);
            _topology = new StreamingTopologyStore(new PooledDbContextFactory<EpgDbContext>(
                new DbContextOptionsBuilder<EpgDbContext>().UseSqlite($"Data Source={Path.Combine(_root.FullName, "topology.db")};Pooling=False").Options));
            _settings.Settings.Returns(Settings);
            Outputs = new CmafOutputCoordinator(Clock, TimeSpan.FromMilliseconds(200));
            _routes.ResolveVirtualAsync(Arg.Any<VirtualDeviceId>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => Route(call.Arg<string>()));
            _multiplexer.SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (AcquisitionFailure is not null)
                    {
                        if (CancelDuringAcquisition)
                        {
                            _cancellations[^1].Cancel();
                        }
                        throw AcquisitionFailure;
                    }
                    return new ResolvedTunerStream(call.Arg<ResolvedStreamingRoute>().Candidates[0], new BlockingInput());
                });
            _multiplexer.SubscribeAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                var subscription = Interlocked.Increment(ref _subscriptions);
                if (subscription == 2 && CaptionInputFailure is not null)
                {
                    throw CaptionInputFailure;
                }
                if (InputFailure is not null)
                {
                    throw InputFailure;
                }
                Interlocked.Increment(ref _activeInputs);
                return new BlockingInput(() => Interlocked.Decrement(ref _activeInputs), _sourceCompletion.Task);
            });
            var effective = (IEffectiveTunerStreamMultiplexer)_multiplexer;
            effective.GetEffectiveSource(Arg.Any<MediaProbeResult>(), Arg.Any<PhysicalTranscodeProfile>())
                .Returns(call => SourceDeinterlacePlanner.CreateEffectiveSource(call.Arg<MediaProbeResult>(), call.Arg<PhysicalTranscodeProfile>()));
            effective.SubscribeEffectiveAsync(
                Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(), Arg.Any<MediaProbeResult>(),
                Arg.Any<PhysicalTranscodeProfile>(), Arg.Any<LineupSettings>(), Arg.Any<CancellationToken>())
                .Returns(call => _multiplexer.SubscribeAsync(call.Arg<StreamAcquisitionDescriptor>(), call.Arg<StreamAcquisitionFactory>(), call.Arg<CancellationToken>()));
            _probe.ProbeAsync(
                Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
                .Returns(_ => ProbeOverride ?? new MediaProbeResult(
                [
                    new MediaTrackMetadata(0, MediaTrackType.Video, "h264", null, null, null, null, null),
                    new MediaTrackMetadata(1, MediaTrackType.Audio, "aac", null, null, null, 2, 48_000) { IsDefault = true },
                    new MediaTrackMetadata(2, MediaTrackType.Subtitle, "webvtt", null, null, null, null, null)
                    {
                        SubtitlePresentation = SubtitlePresentation.WebVtt,
                        Title = "English",
                        Language = "eng"
                    }
                ], null));
            _processes.Start(Arg.Any<MediaProcessRequest>()).Returns(call => StartEncoder(call.Arg<MediaProcessRequest>()));
            _slates.StartCmaf(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ChannelSlateReason>()).Returns(call => StartEncoder(new MediaProcessRequest
            {
                FileName = "synthetic-slate",
                Arguments = [],
                WorkingDirectory = Path.GetDirectoryName(call.ArgAt<string>(1))
            }));
        }

        /// <summary>Gets the controllable logical inactivity clock.</summary>
        internal LogicalClock Clock { get; } = new();

        /// <summary>Completes the retained input independently of viewer and process cancellation.</summary>
        internal void EndSource(Exception? failure) => _sourceCompletion.TrySetResult(failure);

        /// <summary>Gets the real shared-output coordinator.</summary>
        internal CmafOutputCoordinator Outputs { get; }

        /// <summary>Gets independently admitted viewer diagnostics.</summary>
        internal ActiveStreamRegistry ActiveStreams { get; } = new();

        /// <summary>Gets mutable trusted settings for plan-isolation checks.</summary>
        internal LineupSettings Settings { get; } = new()
        {
            MaximumConcurrentStreams = 2,
            Streaming = new StreamingConfiguration { VirtualDevices = [WebPlayerVirtualTunerDriver.CreateBuiltIn()], KeepWarmPeriodSeconds = 0 }
        };

        /// <summary>Gets or sets the exact physical source selected by subsequent requests.</summary>
        internal PhysicalDeviceId PhysicalId { get; set; } = PhysicalDeviceId.New();

        /// <summary>Gets or sets whether startup waits for explicitly published readiness artifacts.</summary>
        internal bool HoldReadiness { get; set; }

        /// <summary>Gets or sets a terminal failure from the upstream producer subscription.</summary>
        internal Exception? InputFailure { get; set; }

        /// <summary>Gets or sets the classified failure after equivalent source acquisition is exhausted.</summary>
        internal StreamAcquisitionException? AcquisitionFailure { get; set; }

        /// <summary>Gets or sets whether the viewer cancels immediately before acquisition reports protection.</summary>
        internal bool CancelDuringAcquisition { get; set; }

        /// <summary>Gets or sets the embedded-caption input failure independently of the media input.</summary>
        internal Exception? CaptionInputFailure { get; set; }

        /// <summary>Gets or sets the complete application startup deadline.</summary>
        internal TimeSpan? StartupTimeout { get; set; }

        /// <summary>Gets or sets a terminal failure while creating the output processor.</summary>
        internal MediaProcessException? ProcessFailure { get; set; }

        /// <summary>Gets or sets deterministic probe metadata for audio-discovery regressions.</summary>
        internal MediaProbeResult? ProbeOverride { get; set; }

        /// <summary>Gets or sets the source's normalized audio declaration.</summary>
        internal string? AudioHint { get; set; }

        /// <summary>Gets or sets whether route metadata marks the source as protected.</summary>
        internal bool ProtectedContent { get; set; }

        /// <summary>Gets notification that the real encoder process has started.</summary>
        internal Task EncoderStarted => _encoderStarted.Task;

        /// <summary>Gets the number of actual encoder processes started.</summary>
        internal int EncoderCount => _encoders.Count;

        /// <summary>Gets the exact FFmpeg requests issued by the management controller.</summary>
        internal IReadOnlyList<MediaProcessRequest> EncoderRequests => _encoderRequests.ToArray();

        /// <summary>Gets whether any owned encoder process remains alive.</summary>
        internal bool IsEncoderRunning => _encoders.Any(encoder => !encoder.Observer.HasExited);

        /// <summary>Gets whether any owned producer directory remains on disk.</summary>
        internal bool HasEncoderDirectory => _encoders.Any(encoder => Directory.Exists(encoder.Directory));

        /// <summary>Gets the number of live producer input subscriptions.</summary>
        internal int ActiveInputs => Volatile.Read(ref _activeInputs);

        /// <summary>Starts the application service directly, with no controller, HTTP context, or action result.</summary>
        internal (ManagementPlaybackService Service, CancellationTokenSource Cancellation, Task<ManagementPlaybackStartResult> Start) BeginServiceStart(string clientId, CmafStreamRequest? request = null)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            _cancellations.Add(cancellation);
            var httpClients = Substitute.For<IHttpClientFactory>();
            httpClients.CreateClient("StreamProxy").Returns(_diagnosticClient);
            _playback ??= new ManagementPlaybackService(
                httpClients, Substitute.For<ILineupGuideReader>(), _settings, _topology, _probe, ActiveStreams,
                _slates, _multiplexer, NullLogger<ManagementPlaybackService>.Instance,
                transientData: _files, mediaProcessFactory: _processes, streamRoutes: _routes, cmafOutputs: Outputs, cmafStartupTimeout: StartupTimeout);
            var start = _playback.StartAsync("7.1", (request ?? new CmafStreamRequest()) with { ClientId = clientId },
                $"127.0.0.1:{10_000 + _cancellations.Count}", cancellationToken: cancellation.Token);
            _serviceStarts.Add(start);
            return (_playback, cancellation, start);
        }

        /// <summary>Starts a real controller request with its own client metadata and cancellation lifetime.</summary>
        internal (StreamController Controller, CancellationTokenSource Cancellation, Task<IActionResult> Start) BeginStart(
            string clientId,
            CmafStreamRequest? request = null,
            string channel = "7.1",
            TimeSpan? startupTimeout = null)
        {
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            _cancellations.Add(cancellation);
            var httpClients = Substitute.For<IHttpClientFactory>();
            httpClients.CreateClient("StreamProxy").Returns(_diagnosticClient);
            var controller = new StreamController(
                httpClients, Substitute.For<ILineupGuideReader>(), _settings, _topology, _probe, ActiveStreams,
                _slates, _multiplexer, NullLogger<StreamController>.Instance,
                transientData: _files, mediaProcessFactory: _processes, streamRoutes: _routes, cmafOutputs: Outputs, cmafStartupTimeout: startupTimeout)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = cancellation.Token }
                }
            };
            controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            controller.HttpContext.Connection.RemotePort = 10_000 + _cancellations.Count;
            var start = controller.StartCmafStream(channel, (request ?? new CmafStreamRequest()) with { ClientId = clientId });
            _starts.Add(start);
            return (controller, cancellation, start);
        }

        /// <summary>Verifies synthetic playback did not probe or subscribe to physical input, optionally after a failed acquisition.</summary>
        internal void AssertNoPhysicalInput(ChannelSlateReason reason = ChannelSlateReason.ContentProtected, bool acquisitionAttempted = false)
        {
            if (acquisitionAttempted)
            {
                Assert.Single(_multiplexer.ReceivedCalls());
                _ = _multiplexer.Received(1).SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>());
            }
            else
            {
                Assert.Empty(_multiplexer.ReceivedCalls());
            }
            Assert.Empty(_probe.ReceivedCalls());
            _processes.DidNotReceive().Start(Arg.Any<MediaProcessRequest>());
            _slates.Received(1).StartCmaf(Arg.Any<string>(), Arg.Any<string>(), reason);
        }

        /// <summary>Disables the fixture's source channel in the real topology store.</summary>
        internal async Task DisableChannelAsync()
        {
            var source = new SourceChannelId(PhysicalId, "7.1");
            await _topology.ReconcileAsync(
                PhysicalId, [new PhysicalSourceChannel { Id = source, ProviderNumber = "7.1", ProviderName = "Disabled" }], TestContext.Current.CancellationToken);
            await _topology.SetEnabledAsync(source, enabled: false, TestContext.Current.CancellationToken);
        }

        /// <summary>Releases readiness for current and subsequent encoders using deterministic manifests, fragments, and captions.</summary>
        internal void PublishReadiness()
        {
            HoldReadiness = false;
            foreach (var encoder in _encoders)
            {
                PublishReadiness(encoder.Directory);
            }
        }

        /// <summary>Exits owned encoders before readiness without using external hardware or network requests.</summary>
        internal async Task TerminateEncoderAsync()
        {
            foreach (var encoder in _encoders)
            {
                encoder.Observer.Kill(entireProcessTree: true);
                await encoder.Observer.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }

        private static void PublishReadiness(string directory)
        {
            File.WriteAllText(Path.Combine(directory, "manifest.mpd"), """
                <MPD xmlns="urn:mpeg:dash:schema:mpd:2011"><Period>
                  <AdaptationSet contentType="video"><Representation id="0" codecs="avc1.64001f" /></AdaptationSet>
                  <AdaptationSet contentType="audio"><Representation id="1" codecs="mp4a.40.2" /></AdaptationSet>
                </Period></MPD>
                """);
            File.WriteAllText(Path.Combine(directory, "master.m3u8"), "#EXTM3U\n");
            File.WriteAllText(Path.Combine(directory, "captions-2.vtt"), "WEBVTT\n\n00:00:00.000 --> 00:00:02.000\nCaption\n");
            foreach (var representation in new[] { 0, 1 })
            {
                File.WriteAllBytes(Path.Combine(directory, $"init-{representation}.mp4"), [0]);
                File.WriteAllBytes(Path.Combine(directory, $"chunk-{representation}-00001.m4s"), [0]);
                File.WriteAllBytes(Path.Combine(directory, $"chunk-{representation}-00002.m4s"), [0]);
            }
        }

        private Process StartEncoder(MediaProcessRequest request)
        {
            if (ProcessFailure is not null)
            {
                throw ProcessFailure;
            }
            var directory = request.WorkingDirectory ?? _encoders.LastOrDefault().Directory
                ?? throw new InvalidOperationException("The CMAF encoder request is missing its owned output directory.");
            _encoderRequests.Enqueue(request);
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Name;
            var executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Lineup.TestSupport.FakeFFmpeg", configuration, "Lineup.TestSupport.FakeFFmpeg.dll"));
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Remove("LINEUP_FAKE_FFMPEG_FAIL");
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("hold");
            var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start the deterministic CMAF encoder.");
            _encoders.Enqueue((Process.GetProcessById(process.Id), directory));
            if (!HoldReadiness)
            {
                PublishReadiness(directory);
            }
            _encoderStarted.TrySetResult();
            return process;
        }

        private ResolvedStreamingRoute Route(string channel)
        {
            var physical = PhysicalRuntime;
            physical.Snapshot.Returns(new PhysicalDeviceSnapshot { Id = PhysicalId, Kind = "test", Name = "Source", Capacity = new PhysicalTunerCapacity(2) });
            var source = new SourceChannelId(PhysicalId, channel);
            var acquisition = new StreamAcquisitionDescriptor(PhysicalId, new PhysicalAcquisitionKey(source, TuneKey), new PhysicalTunerCapacity(2));
            typeof(StreamAcquisitionDescriptor).GetProperty("Runtime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(acquisition, physical);
            var candidate = new ResolvedStreamingCandidate
            {
                PhysicalDevice = physical,
                SourceChannelId = source,
                Acquisition = acquisition,
                PhysicalProfile = Settings.Streaming.PhysicalDefaults,
                SourceMetadata = ProtectedContent
                    ? new Dictionary<string, string> { ["drm"] = bool.TrueString }
                    : AudioHint is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["audioCodec"] = AudioHint },
                DiagnosticUri = new Uri($"http://source.invalid/auto/v{channel}")
            };
            return new ResolvedStreamingRoute
            {
                VirtualDevice = Assert.Single(Settings.Streaming.VirtualDevices),
                PhysicalDevice = physical,
                SourceChannelId = source,
                SourceCandidates = [source],
                Candidates = [candidate],
                Acquisition = acquisition,
                PhysicalProfile = candidate.PhysicalProfile,
                VirtualProfile = Settings.Streaming.VirtualDefaults,
                DiagnosticUri = candidate.DiagnosticUri
            };
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            foreach (var cancellation in _cancellations)
            {
                cancellation.Cancel();
            }
            try
            {
                try
                {
                    await Task.WhenAll(_starts).WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(_serviceStarts).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) when (_cancellations.All(cancellation => cancellation.IsCancellationRequested))
                {
                }
            }
            finally
            {
                await Outputs.DisposeAsync();
                if (_playback is not null)
                {
                    await _playback.DisposeAsync();
                }
                foreach (var encoder in _encoders)
                {
                    if (!encoder.Observer.HasExited)
                    {
                        encoder.Observer.Kill(entireProcessTree: true);
                    }
                    await encoder.Observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    encoder.Observer.Dispose();
                }
                await WaitUntilAsync(() => ActiveInputs == 0);
                foreach (var cancellation in _cancellations)
                {
                    cancellation.Dispose();
                }
                _diagnosticClient.Dispose();
                _root.Delete(recursive: true);
            }
        }
    }

    private sealed class UnavailableDiagnostics : HttpMessageHandler
    {
        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No live tuner diagnostics are used by this test."));
    }

    private sealed class LogicalClock : TimeProvider
    {
        private long _timestamp;

        /// <inheritdoc />
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <inheritdoc />
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        /// <summary>Advances logical elapsed time while retaining the standard timer scheduler.</summary>
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }

    private sealed class BlockingInput(Action? disposed = null, Task<Exception?>? completion = null) : Stream
    {
        private int _disposed;

        /// <inheritdoc />
        public override bool CanRead => true;
        /// <inheritdoc />
        public override bool CanSeek => false;
        /// <inheritdoc />
        public override bool CanWrite => false;
        /// <inheritdoc />
        public override long Length => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        /// <inheritdoc />
        public override void Flush() => throw new NotSupportedException();
        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void SetLength(long value) => throw new NotSupportedException();
        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (completion is null)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            else if (await completion.WaitAsync(cancellationToken) is { } failure)
            {
                throw failure;
            }
            return 0;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                disposed?.Invoke();
            }
            base.Dispose(disposing);
        }
    }
}
