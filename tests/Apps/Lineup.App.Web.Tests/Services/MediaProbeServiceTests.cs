using System.Diagnostics;
using Lineup.Core.Streaming;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies delayed audio discovery and provisional cache handling without physical tuner hardware.</summary>
public sealed class MediaProbeServiceTests
{
    /// <summary>Verifies a first video-only observation is refreshed for unknown or advertised audio before selecting output tracks.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("AC4")]
    public async Task ProbeAsync_DelayedAudio_RefreshesIncompleteObservation(string? audioHint)
    {
        // Arrange
        var starts = 0;
        var service = CreateService(() => Interlocked.Increment(ref starts) == 1 ? VideoJson : AudioJson);
        var hints = audioHint is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["audioCodec"] = audioHint };

        // Act
        var result = await service.ProbeAsync(Descriptor(), UnusedAcquisition, "synthetic-source", hints, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, starts);
        Assert.Contains(result.Tracks, track => track.Type == MediaTrackType.Audio && track.Codec == "ac4");
        Assert.Equal(AudioDiscoveryStatus.Detected, result.AudioDiscovery);
    }

    /// <summary>Verifies an explicitly video-only source remains playable without requiring nonexistent audio.</summary>
    [Fact]
    public async Task ProbeAsync_DeclaredVideoOnly_DoesNotDemandAudio()
    {
        // Arrange
        var starts = 0;
        var service = CreateService(() => { Interlocked.Increment(ref starts); return VideoJson; });

        // Act
        var result = await service.ProbeAsync(Descriptor(), UnusedAcquisition, "synthetic-source",
            new Dictionary<string, string> { ["audioCodec"] = "none" }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, starts);
        Assert.Single(result.Tracks);
        Assert.False(MediaProbeResultPolicy.HasAudio(result));
        Assert.Equal(AudioDiscoveryStatus.DeclaredAbsent, result.AudioDiscovery);
    }

    /// <summary>Verifies bounded video-only observations distinguish advertised missing audio from genuinely unknown availability.</summary>
    [Theory]
    [InlineData(null, AudioDiscoveryStatus.Unknown)]
    [InlineData("AC4", AudioDiscoveryStatus.ExpectedMissing)]
    public async Task ProbeAsync_AudioStillMissing_RetainsCompletenessStatus(string? audioHint, AudioDiscoveryStatus expectedStatus)
    {
        // Arrange
        var starts = 0;
        var service = CreateService(() => { Interlocked.Increment(ref starts); return VideoJson; });
        var hints = audioHint is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["audioCodec"] = audioHint };

        // Act
        var result = await service.ProbeAsync(Descriptor(), UnusedAcquisition, "synthetic-source", hints, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, starts);
        Assert.False(MediaProbeResultPolicy.HasAudio(result));
        Assert.Equal(expectedStatus, result.AudioDiscovery);
    }

    /// <summary>Verifies an unknown video-only shared result is not permanently cached as proof that audio does not exist.</summary>
    [Fact]
    public async Task ProbeAsync_UnknownAudio_UsesRefreshableSharedMetadata()
    {
        // Arrange
        var graph = Substitute.For<IStreamingSessionGraph>();
        var videoOnly = MediaProbeParser.Parse(VideoJson);
        graph.GetOrCreateAcquisitionMetadataAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(),
            Arg.Any<Func<Stream, CancellationToken, Task<MediaProbeResult>>>(), Arg.Any<CancellationToken>()).Returns(videoOnly);
        graph.GetOrRefreshAcquisitionMetadataAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(),
            Arg.Any<Func<Stream, CancellationToken, Task<MediaProbeResult>>>(), Arg.Any<AcquisitionMetadataRefreshPolicy<MediaProbeResult>>(), Arg.Any<CancellationToken>()).Returns(videoOnly);
        var service = new MediaProbeService(NullLogger<MediaProbeService>.Instance, Substitute.For<ITunerStreamMultiplexer>(), Substitute.For<IMediaProcessFactory>(), graph);

        // Act
        var result = await service.ProbeAsync(Descriptor(), UnusedAcquisition, "synthetic-source", new Dictionary<string, string>(), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(MediaProbeResultPolicy.HasAudio(result));
        await graph.Received(1).GetOrRefreshAcquisitionMetadataAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<string>(),
            Arg.Any<Func<Stream, CancellationToken, Task<MediaProbeResult>>>(),
            Arg.Is<AcquisitionMetadataRefreshPolicy<MediaProbeResult>>(policy => !policy.IsReusable(videoOnly)), Arg.Any<CancellationToken>());
    }

    private const string VideoJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1280,"height":720}]}""";
    private const string AudioJson = """{"streams":[{"index":0,"codec_type":"video","codec_name":"h264"},{"index":1,"codec_type":"audio","codec_name":"ac4","channels":6,"sample_rate":"48000"}]}""";

    private static StreamAcquisitionDescriptor Descriptor()
    {
        var device = PhysicalDeviceId.New();
        return new StreamAcquisitionDescriptor(device, new PhysicalAcquisitionKey(new SourceChannelId(device, "140.1"), "none"), new PhysicalTunerCapacity(1));
    }

    private static ValueTask<IStreamSourceLease> UnusedAcquisition(CancellationToken cancellationToken) => throw new InvalidOperationException("The deterministic multiplexer owns test input.");

    private static MediaProbeService CreateService(Func<string> nextMetadata)
    {
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        multiplexer.SubscribeAsync(Arg.Any<StreamAcquisitionDescriptor>(), Arg.Any<StreamAcquisitionFactory>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream());
        var factory = Substitute.For<IMediaProcessFactory>();
        factory.Start(Arg.Any<MediaProcessRequest>()).Returns(_ =>
        {
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
            start.ArgumentList.Add(executable);
            start.ArgumentList.Add("probe-json");
            start.ArgumentList.Add(nextMetadata());
            return Process.Start(start) ?? throw new InvalidOperationException("Unable to start the owned metadata probe.");
        });
        return new MediaProbeService(NullLogger<MediaProbeService>.Instance, multiplexer, factory);
    }
}
