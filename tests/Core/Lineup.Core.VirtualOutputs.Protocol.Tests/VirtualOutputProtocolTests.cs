using Lineup.Core.Common.Streaming;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Protocol.Tests;

/// <summary>Verifies isolated virtual-output protocol negotiation and boundary validation.</summary>
public sealed class VirtualOutputProtocolTests
{
    /// <summary>New operation diagnostics are optional, round-trip through IPC JSON, and reject malformed nested media.</summary>
    [Theory]
    [InlineData("valid", 0)]
    [InlineData("transport", 1)]
    [InlineData("codec", 1)]
    [InlineData("null-tracks", 1)]
    [InlineData("oversized", 1)]
    public void Validate_ProcessingReport_RoundTripsAndValidatesNestedMetadata(string variant, int expectedErrors)
    {
        // Arrange
        ActiveStreamTrack track = new(MediaTrackType.Audio, "ac4", variant == "codec" ? "" : "copy", null, null, null, null, 12, 12, 46034)
        {
            SourceIndex = 7,
            Language = "eng",
            OutputTitle = "Original"
        };
        var processing = new StreamProcessingSnapshot(
            variant == "transport" ? (StreamTransportOperation)999 : StreamTransportOperation.Remux,
            variant == "null-tracks" ? null! : variant == "oversized" ? Enumerable.Repeat(track, VirtualOutputProtocol.MaximumStreamStatusTrackCount + 1).ToArray() : [track])
        {
            AudioKnown = true,
            VideoKnown = true
        };
        var report = new VirtualOutputStreamStatusReport
        {
            Session = new() { SessionId = VirtualOutputWorkerSessionId.New(), Generation = 1 },
            RequestId = VirtualOutputStreamRequestId.New(),
            StreamSessionId = StreamSessionId.New(),
            WorkerMediaSessionId = "viewer",
            Processing = processing
        };

        // Act
        var wire = System.Text.Json.JsonSerializer.Deserialize<VirtualOutputStreamStatusReport>(System.Text.Json.JsonSerializer.Serialize(report))!;
        var errors = VirtualOutputProtocolValidation.Validate(wire);

        // Assert
        Assert.Equal(expectedErrors, errors.Count);
        if (variant == "valid")
        {
            Assert.True(wire.Processing!.AudioKnown);
            Assert.True(wire.Processing.VideoKnown);
            Assert.Equal(StreamTransportOperation.Remux, wire.Processing.Transport);
            Assert.Equal(track, Assert.Single(wire.Processing.Tracks));
        }
        Assert.Empty(VirtualOutputProtocolValidation.Validate(report with { Processing = null }));
    }

    /// <summary>Verifies the unreleased Lineup 3.0 contract keeps its initial schema and single worker protocol baseline.</summary>
    [Fact]
    public void ReleaseContracts_LineupThree_UseInitialVersions()
    {
        // Arrange
        var configuration = new StreamingConfiguration { MaximumPluginConnections = 8 };

        // Act
        var negotiated = VirtualOutputProtocol.Negotiate(1, 1);

        // Assert
        Assert.Equal(1, StreamingConfiguration.CurrentSchemaVersion);
        Assert.Equal(1, configuration.SchemaVersion);
        Assert.Equal(8, configuration.MaximumPluginConnections);
        Assert.Equal(1, VirtualOutputProtocol.MinimumVersion);
        Assert.Equal(1, VirtualOutputProtocol.MaximumVersion);
        Assert.Equal(1, negotiated);
    }

    /// <summary>Verifies the single Lineup 3.0 worker contract retains its operation identifiers.</summary>
    [Fact]
    public void IpcOperations_CurrentContract_PreservesWireValues()
    {
        // Arrange
        int[] expected = [6, 7, 8, 9];

        // Act
        int[] actual =
        [
            (int)VirtualOutputIpcOperation.ReportHealth,
            (int)VirtualOutputIpcOperation.CloseStream,
            (int)VirtualOutputIpcOperation.ReportStreamStatus,
            (int)VirtualOutputIpcOperation.AdmitConnection
        ];

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies negotiation selects the newest mutually supported version.</summary>
    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, 2, 1)]
    [InlineData(2, 3, null)]
    [InlineData(3, 3, null)]
    [InlineData(4, 4, null)]
    [InlineData(2, 1, null)]
    public void Negotiate_PeerRange_ReturnsNewestCompatibleVersion(int minimum, int maximum, int? expected)
    {
        // Arrange
        var peerRange = (Minimum: minimum, Maximum: maximum);

        // Act
        var actual = VirtualOutputProtocol.Negotiate(peerRange.Minimum, peerRange.Maximum);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies invalid or incompatible worker registration is rejected before authentication.</summary>
    [Fact]
    public void Validate_InvalidRegistration_ReturnsAllBoundaryErrors()
    {
        // Arrange
        var request = new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = string.Empty,
            VirtualDeviceId = new VirtualDeviceId(Guid.Empty),
            Generation = 0,
            BootstrapToken = string.Empty,
            MinimumProtocolVersion = 2,
            MaximumProtocolVersion = 1
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(request);

        // Assert
        Assert.Equal(5, errors.Count);
    }

    /// <summary>Verifies stream requests require authenticated identities and a bounded channel key.</summary>
    [Fact]
    public void Validate_InvalidStreamRequest_ReturnsAllBoundaryErrors()
    {
        // Arrange
        var request = new VirtualOutputOpenStreamRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = new VirtualOutputWorkerSessionId(Guid.Empty),
                Generation = 0
            },
            RequestId = new VirtualOutputStreamRequestId(Guid.Empty),
            ChannelKey = new string('x', VirtualOutputProtocol.MaximumChannelKeyLength + 1),
            Stage = (VirtualOutputStreamStage)999,
            Client = new VirtualOutputClientContext
            {
                AuthenticatedClientId = new string('i', VirtualOutputProtocol.MaximumAuthenticatedClientIdLength + 1),
                AuthenticatedClientName = new string('n', VirtualOutputProtocol.MaximumAuthenticatedClientNameLength + 1)
            }
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(request);

        // Assert
        Assert.Equal(7, errors.Count);
    }

    /// <summary>Verifies close requests require the original worker, broker request, and active stream identities.</summary>
    [Fact]
    public void Validate_InvalidCloseStreamRequest_ReturnsAllBoundaryErrors()
    {
        // Arrange
        var request = new VirtualOutputCloseStreamRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = new VirtualOutputWorkerSessionId(Guid.Empty),
                Generation = 0
            },
            RequestId = new VirtualOutputStreamRequestId(Guid.Empty),
            StreamSessionId = new StreamSessionId(Guid.Empty)
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(request);

        // Assert
        Assert.Equal(4, errors.Count);
    }

    /// <summary>Verifies worker presentation reports require correlated bounded identities, state, failures, and media.</summary>
    [Fact]
    public void Validate_InvalidStreamStatusReport_ReturnsAllBoundaryErrors()
    {
        // Arrange
        var report = new VirtualOutputStreamStatusReport
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = new VirtualOutputWorkerSessionId(Guid.Empty),
                Generation = 0
            },
            RequestId = new VirtualOutputStreamRequestId(Guid.Empty),
            StreamSessionId = new StreamSessionId(Guid.Empty),
            WorkerMediaSessionId = new string('s', VirtualOutputProtocol.MaximumWorkerMediaSessionIdLength + 1),
            Format = (HostedStreamFormat)999,
            StartupStage = (VirtualOutputStreamStartupStage)999,
            LastFailure = new string('f', VirtualOutputProtocol.MaximumStreamFailureLength + 1),
            Tracks = Enumerable.Range(0, VirtualOutputProtocol.MaximumStreamStatusTrackCount + 1)
                .Select(index => new ActiveStreamTrack(MediaTrackType.Video, "mpeg2video", "h264", null, null, null, null, null, null, null)
                {
                    SourceIndex = index
                })
                .ToArray()
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(report);

        // Assert
        Assert.Equal(9, errors.Count);
    }

    /// <summary>Verifies worker encoder reports reject missing, oversized, or undefined encoder metadata at the IPC boundary.</summary>
    [Theory]
    [InlineData("", HardwareVideoEncoder.Amf, true)]
    [InlineData("h264_amf", (HardwareVideoEncoder)999, true)]
    [InlineData("oversized", HardwareVideoEncoder.Nvenc, true)]
    [InlineData("h264_amf", HardwareVideoEncoder.Amf, false)]
    public void Validate_ReportedVideoEncoder_RejectsMalformedOutcomes(string encoder, HardwareVideoEncoder requested, bool invalid)
    {
        // Arrange
        var report = new VirtualOutputStreamStatusReport
        {
            Session = new() { SessionId = VirtualOutputWorkerSessionId.New(), Generation = 1 },
            RequestId = VirtualOutputStreamRequestId.New(),
            StreamSessionId = StreamSessionId.New(),
            WorkerMediaSessionId = "viewer",
            VideoEncoding = new(encoder == "oversized" ? new string('x', VirtualOutputProtocol.MaximumStreamCodecLength + 1) : encoder, requested, false)
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(report);

        // Assert
        Assert.Equal(invalid, errors.Contains("The reported video encoder is invalid or oversized."));
    }

    /// <summary>Verifies producer retention is restricted to the physical-effective media stage.</summary>
    [Theory]
    [InlineData(VirtualOutputStreamStage.PhysicalOutput, 0)]
    [InlineData(VirtualOutputStreamStage.VirtualOutput, 1)]
    public void Validate_RetainedProducerSource_RequiresPhysicalOutput(VirtualOutputStreamStage stage, int expectedErrors)
    {
        // Arrange
        var request = new VirtualOutputOpenStreamRequest
        {
            Session = new VirtualOutputSessionContext { SessionId = VirtualOutputWorkerSessionId.New(), Generation = 1 },
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = "2.1",
            Stage = stage,
            RetainSourceForProducer = true
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(request);

        // Assert
        Assert.Equal(expectedErrors, errors.Count);
    }

    /// <summary>Verifies optional producer correlation accepts only canonical producer identities.</summary>
    [Theory]
    [InlineData(null, 0)]
    [InlineData("12345678901234567890123456789012", 0)]
    [InlineData("worker-producer", 1)]
    [InlineData("", 1)]
    public void Validate_WorkerProducerIdentity_RejectsInvalidIds(string? producerId, int expectedErrors)
    {
        // Arrange
        var report = new VirtualOutputStreamStatusReport
        {
            Session = new VirtualOutputSessionContext { SessionId = VirtualOutputWorkerSessionId.New(), Generation = 1 },
            RequestId = VirtualOutputStreamRequestId.New(),
            StreamSessionId = StreamSessionId.New(),
            WorkerMediaSessionId = "viewer",
            WorkerProducerId = producerId,
            Format = HostedStreamFormat.Cmaf
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(report);

        // Assert
        Assert.Equal(expectedErrors, errors.Count);
    }

    /// <summary>Verifies worker presentation reports reject invalid nested media metadata.</summary>
    [Fact]
    public void Validate_StreamStatusReportWithInvalidMediaMetadata_ReturnsBoundaryError()
    {
        // Arrange
        var report = new VirtualOutputStreamStatusReport
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = VirtualOutputWorkerSessionId.New(),
                Generation = 1
            },
            RequestId = VirtualOutputStreamRequestId.New(),
            StreamSessionId = StreamSessionId.New(),
            WorkerMediaSessionId = "worker-media",
            Format = HostedStreamFormat.Cmaf,
            StartupStage = VirtualOutputStreamStartupStage.Ready,
            Tracks =
            [
                new ActiveStreamTrack(MediaTrackType.Video, "mpeg2video", new string('c', VirtualOutputProtocol.MaximumStreamCodecLength + 1), null, null, null, null, null, null, null)
                {
                    SourceIndex = 0
                }
            ]
        };

        // Act
        var errors = VirtualOutputProtocolValidation.Validate(report);

        // Assert
        Assert.Contains("Stream-status tracks contain invalid or oversized media metadata.", errors);
    }

    /// <summary>Verifies framed control messages leave following stream bytes available to the caller.</summary>
    [Fact]
    public async Task WireProtocol_ReadControlMessage_PreservesFollowingBody()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var response = VirtualOutputIpcResponse.Success(new VirtualOutputOpenStreamResponse
        {
            Accepted = true,
            ContentType = "video/mp2t",
            StreamSessionId = StreamSessionId.New(),
            Source = new MediaProbeResult(
                [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 8_000_000, 1920, 1080, null, null)],
                8_000_000),
            Profile = new VirtualTranscodeProfile
            {
                VideoPreset = VideoEncoderPreset.Fast,
                MaximumVideoBitRateMbps = 12
            }
        });
        await VirtualOutputWireProtocol.WriteAsync(stream, response, TestContext.Current.CancellationToken);
        await stream.WriteAsync(new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        stream.Position = 0;

        // Act
        var actual = await VirtualOutputWireProtocol.ReadAsync<VirtualOutputIpcResponse>(stream, TestContext.Current.CancellationToken);
        var payload = actual.ReadPayload<VirtualOutputOpenStreamResponse>();
        var body = new byte[3];
        await stream.ReadExactlyAsync(body, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(actual.Succeeded);
        Assert.Equal("h264", Assert.Single(payload.Source!.Tracks).Codec);
        Assert.Equal(VideoEncoderPreset.Fast, payload.Profile!.VideoPreset);
        Assert.Equal(12, payload.Profile.MaximumVideoBitRateMbps);
        Assert.Equal([1, 2, 3], body);
    }

    /// <summary>Verifies ordinary control messages remain constrained to the default one-mebibyte boundary.</summary>
    [Fact]
    public async Task WireProtocol_DefaultLimit_RejectsLargeMessage()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var message = new string('x', VirtualOutputProtocol.MaximumControlMessageBytes);

        // Act
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            VirtualOutputWireProtocol.WriteAsync(stream, message, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains(VirtualOutputProtocol.MaximumControlMessageBytes.ToString(), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies guide responses may use their larger explicit boundary while remaining framed and bounded.</summary>
    [Fact]
    public async Task WireProtocol_GuideLimit_RoundTripsLargeMessage()
    {
        // Arrange
        await using var stream = new MemoryStream();
        var message = new string('x', VirtualOutputProtocol.MaximumControlMessageBytes + 1);
        await VirtualOutputWireProtocol.WriteAsync(
            stream,
            message,
            VirtualOutputProtocol.MaximumGuideMessageBytes,
            TestContext.Current.CancellationToken);
        stream.Position = 0;

        // Act
        var actual = await VirtualOutputWireProtocol.ReadAsync<string>(
            stream,
            VirtualOutputProtocol.MaximumGuideMessageBytes,
            TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(message, actual);
    }
}
