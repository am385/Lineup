using System.Text.Json;
using Lineup.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Streaming.FFmpeg;
using Lineup.VirtualOutputs.Hosting;
using Lineup.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies real local IPC framing, registration, authorization context, and typed broker dispatch.</summary>
public sealed class VirtualOutputIpcServerTests
{
    /// <summary>Verifies a worker can register and request its manifest over the platform local transport.</summary>
    [Fact]
    public async Task Client_RegisterAndGetManifest_RoundTripsOverLocalIpc()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability>
            {
                VirtualOutputCapability.ReadManifest,
                VirtualOutputCapability.ReportHealth
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualOutputManifest
            {
                VirtualDeviceId = deviceId,
                Name = "Isolated output",
                TunerCount = 4
            });
        var status = new VirtualOutputRuntimeStatus();
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = deviceId,
                        Name = "Isolated output",
                        Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })),
                        Hosting = new VirtualOutputHostingDefinition
                        {
                            WorkerPolicyKey = "default"
                        }
                    }
                ],
                VirtualOutputWorkerPolicies =
                [
                    new VirtualOutputWorkerPolicyDefinition
                    {
                        Key = "default",
                        Name = "Default"
                    }
                ]
            }
        });
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            status,
            settings,
            new ActiveStreamRegistry(),
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);

        // Act
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 2
        }, TestContext.Current.CancellationToken);
        var manifest = await client.GetManifestAsync(new VirtualOutputManifestRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = registration.SessionId!.Value,
                Generation = ticket.Generation
            }
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(registration.Accepted);
        Assert.Equal(deviceId, manifest.VirtualDeviceId);
        Assert.Equal(4, manifest.TunerCount);
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies deterministic stream closure remains available when the open stream occupies the worker's complete broker-call budget.</summary>
    [Fact]
    public async Task Client_CloseStream_BypassesOutstandingCallLimit()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability>
            {
                VirtualOutputCapability.OpenStream,
                VirtualOutputCapability.ReportHealth
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var activeStreams = new ActiveStreamRegistry();
        var mediaStream = new BlockingReadStream();
        var streamSessionId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamSessionId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var source = new MediaProbeResult(
            [new MediaTrackMetadata(0, MediaTrackType.Video, "h264", 4_000_000, 1280, 720, null, null)],
            4_000_000);
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.OpenStreamAsync(Arg.Any<VirtualOutputOpenStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualOutputBrokeredStream(
                activeStreams,
                new VirtualMpegTsOutput(mediaStream, source, source, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()),
                new VirtualOutputOpenStreamResponse
                {
                    Accepted = true,
                    ContentType = "video/mp2t",
                    StreamSessionId = streamSessionId,
                    Source = source,
                    Profile = new VirtualTranscodeProfile()
                }));
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            new VirtualOutputRuntimeStatus(),
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1),
            activeStreams,
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 2
        }, TestContext.Current.CancellationToken);
        var request = new VirtualOutputOpenStreamRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = registration.SessionId!.Value,
                Generation = ticket.Generation
            },
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = "48.3",
            Stage = VirtualOutputStreamStage.PhysicalOutput
        };
        await using var opened = await client.OpenStreamAsync(request, TestContext.Current.CancellationToken);

        // Act
        var closed = await opened.CloseAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(closed);
        Assert.True(mediaStream.IsDisposed);
        Assert.Empty(activeStreams.GetActiveStreams());
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies a correlated worker presentation replaces broker media stages without replacing broker-owned identity metadata.</summary>
    [Fact]
    public async Task Client_ReportStreamStatus_MergesCorrelatedPresentation()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability>
            {
                VirtualOutputCapability.OpenStream,
                VirtualOutputCapability.ReportHealth
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var activeStreams = new ActiveStreamRegistry();
        var mediaStream = new BlockingReadStream();
        var streamSessionId = StreamSessionId.New();
        var originalFlow = new ActiveStreamFlowContext
        {
            SyntheticSource = "physical-source",
            VirtualDeviceId = deviceId
        };
        activeStreams.Register(new ActiveStreamSnapshot(
            streamSessionId.ToString(),
            "48.3",
            HostedStreamFormat.MpegTs,
            DateTime.UtcNow,
            9_000_000,
            [new ActiveStreamTrack(MediaTrackType.Video, "mpeg2video", "mpeg2video", 8_000_000, 8_000_000, 1920, 1080, null, null, null)])
        {
            ClientId = "client-id",
            ClientAddress = "192.0.2.10",
            Flow = originalFlow
        });
        var rawSource = new MediaProbeResult(
            [
                new MediaTrackMetadata(0, MediaTrackType.Video, "mpeg2video", 8_000_000, 1920, 1080, null, null),
                new MediaTrackMetadata(1, MediaTrackType.Audio, "ac3", 384_000, null, null, 6, 48_000),
                new MediaTrackMetadata(2, MediaTrackType.Subtitle, "eia_608", null, null, null, null, null)
            ],
            9_000_000);
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.OpenStreamAsync(Arg.Any<VirtualOutputOpenStreamRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualOutputBrokeredStream(
                activeStreams,
                new VirtualMpegTsOutput(mediaStream, rawSource, rawSource, new VirtualTranscodeProfile(), originalFlow),
                new VirtualOutputOpenStreamResponse
                {
                    Accepted = true,
                    ContentType = "video/mp2t",
                    StreamSessionId = streamSessionId,
                    RawSource = rawSource,
                    Source = rawSource,
                    Profile = new VirtualTranscodeProfile()
                }));
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            new VirtualOutputRuntimeStatus(),
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1),
            activeStreams,
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 2
        }, TestContext.Current.CancellationToken);
        var request = new VirtualOutputOpenStreamRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = registration.SessionId!.Value,
                Generation = ticket.Generation
            },
            RequestId = VirtualOutputStreamRequestId.New(),
            ChannelKey = "48.3",
            Stage = VirtualOutputStreamStage.PhysicalOutput
        };
        await using var opened = await client.OpenStreamAsync(request, TestContext.Current.CancellationToken);
        ActiveStreamTrack[] presentationTracks =
        [
            new(MediaTrackType.Video, "mpeg2video", "h264", 8_000_000, 4_000_000, 1920, 1080, null, null, null)
            {
                SourceIndex = 0,
                IsSelected = true
            },
            new(MediaTrackType.Audio, "ac3", "aac", 384_000, 192_000, null, null, 6, 2, 48_000)
            {
                SourceIndex = 1,
                IsSelected = true
            },
            new(MediaTrackType.Subtitle, "eia_608", "not-mapped", null, null, null, null, null, null, null)
            {
                SourceIndex = 2,
                HasClosedCaptions = true
            }
        ];

        // Act
        var response = await client.ReportStreamStatusAsync(new VirtualOutputStreamStatusReport
        {
            Session = request.Session,
            RequestId = request.RequestId,
            StreamSessionId = streamSessionId,
            WorkerMediaSessionId = "worker-media-session",
            Format = HostedStreamFormat.Cmaf,
            Tracks = presentationTracks,
            PhysicalInput = new ActiveStreamMediaSnapshot(9_000_000, rawSource.Tracks.Select(ToMediaTrack).ToArray()),
            PhysicalOutput = new ActiveStreamMediaSnapshot(9_000_000, rawSource.Tracks.Select(ToMediaTrack).ToArray()),
            VirtualOutput = new ActiveStreamMediaSnapshot(
                4_192_000,
                [
                    new ActiveStreamMediaTrack(0, MediaTrackType.Video, "h264", 4_000_000, 1920, 1080, null, null),
                    new ActiveStreamMediaTrack(1, MediaTrackType.Audio, "aac", 192_000, null, null, 2, 48_000)
                ]),
            StartupStage = VirtualOutputStreamStartupStage.Ready
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.Updated);
        var snapshot = Assert.Single(activeStreams.GetActiveStreams());
        Assert.Equal(HostedStreamFormat.Cmaf, snapshot.Format);
        Assert.Equal("client-id", snapshot.ClientId);
        Assert.Equal("192.0.2.10", snapshot.ClientAddress);
        Assert.Same(originalFlow, snapshot.Flow);
        Assert.Equal(request.RequestId.ToString(), snapshot.BrokerRequestId);
        Assert.Equal("worker-media-session", snapshot.WorkerMediaSessionId);
        Assert.Equal(nameof(VirtualOutputStreamStartupStage.Ready), snapshot.WorkerStartupStage);
        Assert.Equal(["h264", "aac"], snapshot.VirtualOutput!.Tracks.Select(track => track.Codec));
        Assert.DoesNotContain(snapshot.VirtualOutput.Tracks, track => track.Type == MediaTrackType.Subtitle);
        await opened.CloseAsync(TestContext.Current.CancellationToken);
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies one worker cannot exceed its configured concurrent broker-call budget.</summary>
    [Fact]
    public async Task Client_ConcurrentBrokerCalls_RejectsCallsBeyondPolicy()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability>
            {
                VirtualOutputCapability.ReadManifest,
                VirtualOutputCapability.ReportHealth
            },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                entered.TrySetResult();
                await release.Task;
                return new VirtualOutputManifest
                {
                    VirtualDeviceId = deviceId,
                    Name = "Isolated output"
                };
            });
        var settings = CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1);
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            new VirtualOutputRuntimeStatus(),
            settings,
            new ActiveStreamRegistry(),
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1
        }, TestContext.Current.CancellationToken);
        var request = new VirtualOutputManifestRequest
        {
            Session = new VirtualOutputSessionContext
            {
                SessionId = registration.SessionId!.Value,
                Generation = ticket.Generation
            }
        };
        var firstCall = client.GetManifestAsync(request, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        var exception = await Assert.ThrowsAsync<VirtualOutputClientException>(
            () => client.GetManifestAsync(request, TestContext.Current.CancellationToken));
        release.TrySetResult();
        await firstCall;

        // Assert
        Assert.Equal(VirtualOutputFailureCode.ResourceLimit, exception.FailureCode);
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies a rejected registration cannot replace the healthy runtime-status projection.</summary>
    [Fact]
    public async Task Client_RejectedRegistration_DoesNotCreateRuntimeStatus()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(
            deviceId,
            "test-worker",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth },
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
        var status = new VirtualOutputRuntimeStatus();
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            Substitute.For<IVirtualOutputBroker>(),
            status,
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1),
            new ActiveStreamRegistry(),
            NullLogger<VirtualOutputIpcServer>.Instance);
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);

        // Act
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = "invalid-token",
            MinimumProtocolVersion = 1,
            MaximumProtocolVersion = 1
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(registration.Accepted);
        Assert.Empty(status.GetSnapshots());
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    private static ILineupSettingsService CreateSettings(
        VirtualDeviceId deviceId,
        int maximumOutstandingBrokerCalls)
    {
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            Streaming = new StreamingConfiguration
            {
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = deviceId,
                        Name = "Isolated output",
                        Adapter = new AdapterConfiguration("test", JsonSerializer.SerializeToElement(new { })),
                        Hosting = new VirtualOutputHostingDefinition
                        {
                            WorkerPolicyKey = "default"
                        }
                    }
                ],
                VirtualOutputWorkerPolicies =
                [
                    new VirtualOutputWorkerPolicyDefinition
                    {
                        Key = "default",
                        Name = "Default",
                        MaximumOutstandingBrokerCalls = maximumOutstandingBrokerCalls
                    }
                ]
            }
        });
        return settings;
    }

    private static VirtualOutputIpcEndpoint CreateEndpoint()
    {
        if (OperatingSystem.IsWindows())
        {
            return new VirtualOutputIpcEndpoint
            {
                Transport = VirtualOutputIpcTransport.NamedPipe,
                Address = $"lineup-tests-{Guid.NewGuid():N}"
            };
        }
        return new VirtualOutputIpcEndpoint
        {
            Transport = VirtualOutputIpcTransport.UnixDomainSocket,
            Address = Path.Combine(Path.GetTempPath(), $"lineup-tests-{Guid.NewGuid():N}.sock")
        };
    }

    private static ActiveStreamMediaTrack ToMediaTrack(MediaTrackMetadata track) =>
        new(
            track.Index,
            track.Type,
            track.Codec,
            track.BitRate,
            track.Width,
            track.Height,
            track.Channels,
            track.SampleRate)
        {
            Language = track.Language,
            Title = track.Title
        };

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _disposed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
