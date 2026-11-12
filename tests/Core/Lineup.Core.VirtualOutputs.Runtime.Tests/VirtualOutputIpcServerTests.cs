using System.Text.Json;
using Lineup.Core.Common.Guides;
using Lineup.Core.Common.Streaming;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Core.Streaming.FFmpeg;
using Lineup.Core.VirtualOutputs.Hosting;
using Lineup.Core.VirtualOutputs.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Core.VirtualOutputs.Runtime.Tests;

/// <summary>Verifies real local IPC framing, registration, authorization context, and typed broker dispatch.</summary>
public sealed class VirtualOutputIpcServerTests
{
    /// <summary>Over-limit or canceled guide reads return no partial snapshot and do not poison a newer coherent response.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_GuideFailure_PreservesLaterGeneration(bool cancel)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        using var authority = new VirtualOutputSessionAuthority();
        var id = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(id, "guide-worker", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadGuide },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var registration = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = id,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reject = true;
        var channel = new VirtualGuideChannel { ChannelId = new(id, Guid.NewGuid()), Number = "1", Name = "Example", Programs = [] };
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.GetGuideAsync(Arg.Any<VirtualOutputGuideRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (reject && cancel)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()); }
                finally { canceled.TrySetResult(); }
            }
            return new VirtualOutputGuideResponse
            {
                Snapshot = new VirtualGuideSnapshot
                {
                    VirtualDeviceId = id,
                    Generation = new(DateTimeOffset.UtcNow, reject ? "A" : "B"),
                    Channels = reject ? Enumerable.Repeat(channel, GuideTransfer.MaximumChannels + 1).ToArray() : [channel]
                }
            };
        });
        var endpoint = CreateEndpoint();
        using var server = new VirtualOutputIpcServer(endpoint, authority, broker, new VirtualOutputRuntimeStatus(), CreateSettings(id, maximumOutstandingBrokerCalls: 4),
            new ActiveStreamRegistry(), NullLogger<VirtualOutputIpcServer>.Instance, Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(watchdog.Token);
        try
        {
            var client = new VirtualOutputBrokerClient(endpoint);
            var request = new VirtualOutputGuideRequest { Session = session };

            // Act
            var first = client.GetGuideAsync(request, requestCancellation.Token);
            if (cancel)
            {
                await entered.Task.WaitAsync(watchdog.Token);
                await requestCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
                await canceled.Task.WaitAsync(watchdog.Token);
            }
            else
            {
                var failure = await Assert.ThrowsAsync<VirtualOutputClientException>(() => first);
                Assert.Equal(VirtualOutputFailureCode.ResourceLimit, failure.FailureCode);
            }
            reject = false;
            var responses = await Task.WhenAll(client.GetGuideAsync(request, watchdog.Token), client.GetGuideAsync(request, watchdog.Token));

            // Assert
            Assert.All(responses, response =>
            {
                Assert.Equal("B", response.Snapshot.Generation!.ImportRevision);
                Assert.Single(response.Snapshot.Channels);
            });
        }
        finally
        {
            await server.StopAsync(watchdog.Token);
        }
    }

    /// <summary>Dedicated reservations share management limits and retire remotely only after the actual idle owner releases its channel.</summary>
    [Fact]
    public async Task Client_MediaAdmission_UsesDeploymentBudgetAndOwnerCheckedReclamation()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        using var authority = new VirtualOutputSessionAuthority();
        var id = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(id, "media-worker", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var registered = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = id,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        var session = new VirtualOutputSessionContext { SessionId = registered.SessionId!.Value, Generation = ticket.Generation };
        var endpoint = CreateEndpoint();
        var options = new StreamingResourceOptions { MaximumProducers = 1, MaximumProcessingOperations = 1, MinimumMediaFreeSpaceMib = int.MaxValue };
        var admission = new MediaWorkAdmission(options);
        using var server = new VirtualOutputIpcServer(endpoint, authority, Substitute.For<IVirtualOutputBroker>(), new VirtualOutputRuntimeStatus(),
            CreateSettings(id, maximumOutstandingBrokerCalls: 1), new ActiveStreamRegistry(), NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>(), admission, options);
        await server.StartAsync(watchdog.Token);
        var lost = false;
        var remote = new BrokerMediaWorkAdmission(new(endpoint), session, () => lost = true, NullLogger.Instance);
        var active = true;
        IAsyncDisposable? remoteOwner = null;
        try
        {
            remoteOwner = await remote.AcquireAsync(MediaWorkKind.Producer, async _ =>
            {
                if (active) { return false; }
                await remoteOwner!.DisposeAsync();
                return true;
            }, watchdog.Token);

            // Act
            var failure = await Assert.ThrowsAsync<MediaResourceLimitException>(() =>
                admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: watchdog.Token).AsTask());
            active = false;
            await using var localOwner = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: watchdog.Token);
            await using var remoteProcessing = await remote.AcquireAsync(MediaWorkKind.Processing, cancellationToken: watchdog.Token);
            var storage = Assert.Throws<MediaResourceLimitException>(() => remote.CheckWorkspace(Path.GetTempPath()));
            var saturated = await Assert.ThrowsAsync<MediaResourceLimitException>(() =>
                admission.AcquireAsync(MediaWorkKind.Processing, cancellationToken: watchdog.Token).AsTask());

            // Assert
            Assert.Equal("media_producer_limit", failure.Code);
            Assert.Equal("media_processing_limit", saturated.Code);
            Assert.Equal("media_storage_limit", storage.Code);
            Assert.Equal(1, admission.GetDiagnostics().Producers);
            Assert.Equal(1, admission.GetDiagnostics().Processing);
            Assert.False(lost);
        }
        finally
        {
            if (remoteOwner is not null) { await remoteOwner.DisposeAsync(); }
            await server.StopAsync(watchdog.Token);
        }
    }

    /// <summary>Revoking authenticated ownership requests fail-closed worker cleanup before returning the reservation to the deployment.</summary>
    [Fact]
    public async Task Client_MediaAdmission_RevocationWaitsForOwnedCleanup()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        using var authority = new VirtualOutputSessionAuthority();
        var id = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(id, "media-worker", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var registered = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = id,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        var session = new VirtualOutputSessionContext { SessionId = registered.SessionId!.Value, Generation = ticket.Generation };
        var endpoint = CreateEndpoint();
        var admission = new MediaWorkAdmission(new() { MaximumProducers = 1 });
        using var server = new VirtualOutputIpcServer(endpoint, authority, Substitute.For<IVirtualOutputBroker>(), new VirtualOutputRuntimeStatus(),
            CreateSettings(id, maximumOutstandingBrokerCalls: 1), new ActiveStreamRegistry(), NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>(), admission);
        await server.StartAsync(watchdog.Token);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remote = new BrokerMediaWorkAdmission(new(endpoint), session, () => lost.TrySetResult(), NullLogger.Instance);
        await using var owner = await remote.AcquireAsync(MediaWorkKind.Producer, cancellationToken: watchdog.Token);
        try
        {
            // Act
            authority.Revoke(id, "Synthetic media ownership revocation.");
            await lost.Task.WaitAsync(watchdog.Token);
            var beforeCleanup = admission.GetDiagnostics().Producers;
            await owner.DisposeAsync();
            while (admission.GetDiagnostics().Producers != 0) { await Task.Delay(10, watchdog.Token); }
            await using var replacement = await admission.AcquireAsync(MediaWorkKind.Producer, cancellationToken: watchdog.Token);

            // Assert
            Assert.Equal(1, beforeCleanup);
            Assert.Equal(1, admission.GetDiagnostics().Producers);
        }
        finally
        {
            await owner.DisposeAsync();
            await server.StopAsync(watchdog.Token);
        }
    }

    /// <summary>Verifies an accepted old-generation status operation cannot overwrite a concurrently enrolled replacement.</summary>
    [Theory]
    [InlineData(VirtualOutputIpcOperation.Register)]
    [InlineData(VirtualOutputIpcOperation.RenewLease)]
    [InlineData(VirtualOutputIpcOperation.ReportHealth)]
    public async Task Client_StaleStatusOperation_PreservesReplacementGeneration(VirtualOutputIpcOperation operation)
    {
        // Arrange
        using var sessions = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var capabilities = new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReportHealth };
        var ticket = sessions.CreateActivation(deviceId, "test-worker", capabilities, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var request = new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = deviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        };
        var status = new VirtualOutputRuntimeStatus();
        VirtualOutputRuntimeSnapshot? replacement = null;
        VirtualOutputSessionContext? replacementSession = null;
        void EnrollReplacement()
        {
            var next = sessions.CreateActivation(deviceId, "test-worker", capabilities, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
            var registered = sessions.Register(request with { Generation = next.Generation, BootstrapToken = next.BootstrapToken });
            replacementSession = new VirtualOutputSessionContext { SessionId = registered.SessionId!.Value, Generation = next.Generation };
            replacement = new VirtualOutputRuntimeSnapshot
            {
                VirtualDeviceId = deviceId,
                State = VirtualOutputRuntimeState.Healthy,
                Generation = next.Generation,
                ProtocolVersion = VirtualOutputProtocol.MaximumVersion
            };
            status.Set(replacement);
        }
        var authority = Substitute.For<IVirtualOutputSessionAuthority>();
        authority.Register(Arg.Any<VirtualOutputWorkerRegistrationRequest>()).Returns(call =>
        {
            var response = sessions.Register(call.Arg<VirtualOutputWorkerRegistrationRequest>());
            EnrollReplacement();
            return response;
        });
        authority.Renew(Arg.Any<VirtualOutputLeaseRenewalRequest>()).Returns(call => sessions.Renew(call.Arg<VirtualOutputLeaseRenewalRequest>()));
        authority.Authorize(Arg.Any<VirtualOutputSessionContext>(), Arg.Any<VirtualOutputCapability>()).Returns(call =>
        {
            var response = sessions.Authorize(call.Arg<VirtualOutputSessionContext>(), call.Arg<VirtualOutputCapability>());
            EnrollReplacement();
            return response;
        });
        var endpoint = CreateEndpoint();
        using var server = new VirtualOutputIpcServer(endpoint, authority, Substitute.For<IVirtualOutputBroker>(), status,
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1), new ActiveStreamRegistry(), NullLogger<VirtualOutputIpcServer>.Instance, Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var client = new VirtualOutputBrokerClient(endpoint);
            var registration = operation == VirtualOutputIpcOperation.Register ? null : sessions.Register(request);
            var session = registration is null ? null : new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };

            // Act
            switch (operation)
            {
                case VirtualOutputIpcOperation.Register:
                    await client.RegisterAsync(request, TestContext.Current.CancellationToken);
                    break;
                case VirtualOutputIpcOperation.RenewLease:
                    await client.RenewLeaseAsync(new VirtualOutputLeaseRenewalRequest { Session = session!, Ready = false }, TestContext.Current.CancellationToken);
                    break;
                case VirtualOutputIpcOperation.ReportHealth:
                    await client.ReportHealthAsync(new VirtualOutputWorkerHealthReport { Session = session!, Ready = false }, TestContext.Current.CancellationToken);
                    break;
            }

            // Assert
            Assert.NotNull(replacement);
            Assert.Same(replacement, Assert.Single(status.GetSnapshots()));
            Assert.True(sessions.Authorize(Assert.IsType<VirtualOutputSessionContext>(replacementSession), VirtualOutputCapability.ReportHealth).Authorized);
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies transport loss cancels upstream work and releases a one-call budget before or after stream acceptance.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Client_DisconnectsDuringStream_ReleasesUpstreamAndCallBudget(bool accepted, bool lateAcceptance)
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(10));
        using var openCancellation = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(deviceId, "test-worker",
            new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream, VirtualOutputCapability.ReadManifest },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var registration = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = deviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        var session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation };
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeStreams = new ActiveStreamRegistry();
        var source = new BlockingReadStream();
        var streamId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var media = new MediaProbeResult([], null);
        var output = new VirtualOutputBrokeredStream(activeStreams,
            new VirtualMpegTsOutput(source, media, media, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()),
            new VirtualOutputOpenStreamResponse { Accepted = true, StreamSessionId = streamId, ContentType = "video/mp2t", Source = media });
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.OpenStreamAsync(Arg.Any<VirtualOutputOpenStreamRequest>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            entered.TrySetResult(token);
            if (lateAcceptance)
            {
                await release.Task;
            }
            else if (!accepted)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    await output.DisposeAsync();
                }
            }
            return output;
        });
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VirtualOutputManifest { VirtualDeviceId = deviceId, Name = "Replacement call" });
        using var server = new VirtualOutputIpcServer(endpoint, authority, broker, new VirtualOutputRuntimeStatus(),
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1), activeStreams, NullLogger<VirtualOutputIpcServer>.Instance, Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(watchdog.Token);
        try
        {
            var client = new VirtualOutputBrokerClient(endpoint);
            var opening = client.OpenStreamAsync(new VirtualOutputOpenStreamRequest
            {
                Session = session,
                RequestId = VirtualOutputStreamRequestId.New(),
                ChannelKey = "48.3"
            }, openCancellation.Token);
            var upstreamToken = await entered.Task.WaitAsync(watchdog.Token);
            using var observer = upstreamToken.Register(() => canceled.TrySetResult());

            // Act
            if (accepted)
            {
                var opened = await opening;
                await opened.Stream.DisposeAsync();
            }
            else
            {
                await openCancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
            }
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2), watchdog.Token);
            release.TrySetResult();
            await source.Disposed.WaitAsync(TimeSpan.FromSeconds(2), watchdog.Token);
            var replacement = await client.GetManifestAsync(new VirtualOutputManifestRequest { Session = session }, watchdog.Token);

            // Assert
            Assert.Equal(deviceId, replacement.VirtualDeviceId);
            Assert.True(upstreamToken.IsCancellationRequested);
            Assert.True(source.IsDisposed);
            Assert.Empty(activeStreams.GetActiveStreams());
            Assert.True(authority.Authorize(session, VirtualOutputCapability.OpenStream).Authorized);
        }
        finally
        {
            release.TrySetResult();
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Verifies a terminal upstream failure closes an accepted MPEG-TS body without appending control JSON to the media bytes.</summary>
    [Fact]
    public async Task Client_UpstreamFailsAfterAcceptance_ReceivesMediaThenEof()
    {
        // Arrange
        var endpoint = CreateEndpoint();
        using var authority = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = authority.CreateActivation(deviceId, "test-worker", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var activeStreams = new ActiveStreamRegistry();
        var sessionId = StreamSessionId.New();
        var media = new MediaProbeResult([], null);
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.OpenStreamAsync(Arg.Any<VirtualOutputOpenStreamRequest>(), Arg.Any<CancellationToken>()).Returns(new VirtualOutputBrokeredStream(
            activeStreams, new VirtualMpegTsOutput(new FailingMediaStream(), media, media, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()),
            new VirtualOutputOpenStreamResponse { Accepted = true, StreamSessionId = sessionId, ContentType = "video/mp2t", Source = media }));
        using var server = new VirtualOutputIpcServer(endpoint, authority, broker, new VirtualOutputRuntimeStatus(),
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1), activeStreams, NullLogger<VirtualOutputIpcServer>.Instance, Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var client = new VirtualOutputBrokerClient(endpoint);
            var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
            {
                WorkerId = ticket.WorkerId,
                VirtualDeviceId = ticket.VirtualDeviceId,
                Generation = ticket.Generation,
                BootstrapToken = ticket.BootstrapToken,
                MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
                MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
            }, TestContext.Current.CancellationToken);
            await using var source = await client.OpenStreamAsync(new VirtualOutputOpenStreamRequest
            {
                Session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation },
                RequestId = VirtualOutputStreamRequestId.New(),
                ChannelKey = "31.2"
            }, TestContext.Current.CancellationToken);
            using var received = new MemoryStream();

            // Act
            await source.Stream.CopyToAsync(received, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            // Assert
            Assert.Equal(new byte[] { 0x47 }, received.ToArray());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
    }

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
        string? brokerTrace = null;
        long? brokerGeneration = null;
        broker.GetManifestAsync(Arg.Any<VirtualOutputManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                brokerTrace = System.Diagnostics.Activity.Current?.TraceId.ToString();
                brokerGeneration = System.Diagnostics.Activity.Current?.GetTagItem("lineup.worker.generation") is long generation ? generation : null;
                return new VirtualOutputManifest
                {
                    VirtualDeviceId = deviceId,
                    Name = "Isolated output",
                    TunerCount = 4
                };
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
            NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);

        // Act
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        }, TestContext.Current.CancellationToken);
        using var operation = Lineup.Core.Extensibility.Diagnostics.LineupTelemetry.Begin("ipc_test", cancellationToken: TestContext.Current.CancellationToken);
        var callerTrace = System.Diagnostics.Activity.Current!.TraceId.ToString();
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
        Assert.Equal(callerTrace, brokerTrace);
        Assert.Equal(ticket.Generation, brokerGeneration);
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
            NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
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

    /// <summary>Verifies correlated readiness releases startup admission and replaces media stages without replacing broker identity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_ReportStreamStatus_MergesCorrelatedPresentation(bool retained)
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
        var activeStreams = new ActiveStreamRegistry(resources: new() { MaximumPendingStarts = 1 });
        using var startup = activeStreams.TryBeginStartup(0, () => { });
        var mediaStream = new BlockingReadStream();
        var streamSessionId = StreamSessionId.New();
        var originalFlow = new ActiveStreamFlowContext
        {
            SyntheticSource = "physical-source",
            VirtualDeviceId = deviceId
        };
        Assert.NotNull(startup);
        Assert.True(startup.TryRegister(new ActiveStreamSnapshot(
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
        }, mediaStream.Dispose));
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
                }, retainedProducerSource: retained));
        var server = new VirtualOutputIpcServer(
            endpoint,
            authority,
            broker,
            new VirtualOutputRuntimeStatus(),
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1),
            activeStreams,
            NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
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
        var report = new VirtualOutputStreamStatusReport
        {
            Session = request.Session,
            RequestId = request.RequestId,
            StreamSessionId = streamSessionId,
            WorkerMediaSessionId = "worker-media-session",
            WorkerProducerId = StreamSessionId.New().ToString(),
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
        };
        var starting = await client.ReportStreamStatusAsync(report with { StartupStage = VirtualOutputStreamStartupStage.Starting }, TestContext.Current.CancellationToken);
        Assert.True(starting.Updated);
        Assert.Null(activeStreams.TryBeginStartup(0, () => { }));
        var response = await client.ReportStreamStatusAsync(report, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.Updated);
        using var nextStartup = activeStreams.TryBeginStartup(0, () => { });
        Assert.NotNull(nextStartup);
        var snapshot = Assert.Single(activeStreams.GetActiveStreams());
        Assert.Equal(HostedStreamFormat.Cmaf, snapshot.Format);
        Assert.Equal("client-id", snapshot.ClientId);
        Assert.Equal("192.0.2.10", snapshot.ClientAddress);
        Assert.Equal(originalFlow with { ReusableOutputId = new StreamSessionId(Guid.ParseExact(report.WorkerProducerId, "N")) }, snapshot.Flow);
        Assert.Equal(request.RequestId.ToString(), snapshot.BrokerRequestId);
        Assert.Equal("worker-media-session", snapshot.WorkerMediaSessionId);
        Assert.Equal(nameof(VirtualOutputStreamStartupStage.Ready), snapshot.WorkerStartupStage);
        Assert.Equal(["h264", "aac"], snapshot.VirtualOutput!.Tracks.Select(track => track.Codec));
        Assert.DoesNotContain(snapshot.VirtualOutput.Tracks, track => track.Type == MediaTrackType.Subtitle);
        var mismatched = await Assert.ThrowsAsync<VirtualOutputClientException>(() =>
            client.ReportStreamStatusAsync(report with { RequestId = VirtualOutputStreamRequestId.New(), ReleaseViewer = true }, TestContext.Current.CancellationToken));
        Assert.Equal(VirtualOutputFailureCode.InvalidRequest, mismatched.FailureCode);
        var wrongStream = await Assert.ThrowsAsync<VirtualOutputClientException>(() =>
            client.ReportStreamStatusAsync(report with { StreamSessionId = StreamSessionId.New(), ReleaseViewer = true }, TestContext.Current.CancellationToken));
        Assert.Equal(VirtualOutputFailureCode.InvalidRequest, wrongStream.FailureCode);
        Assert.Single(activeStreams.GetActiveStreams());
        if (retained)
        {
            var released = await client.ReportStreamStatusAsync(report with { ReleaseViewer = true }, TestContext.Current.CancellationToken);
            Assert.True(released.Updated);
            Assert.Empty(activeStreams.GetActiveStreams());
            Assert.False(mediaStream.IsDisposed);
        }
        else
        {
            var rejected = await Assert.ThrowsAsync<VirtualOutputClientException>(() =>
                client.ReportStreamStatusAsync(report with { ReleaseViewer = true }, TestContext.Current.CancellationToken));
            Assert.Equal(VirtualOutputFailureCode.InvalidRequest, rejected.FailureCode);
            Assert.Single(activeStreams.GetActiveStreams());
        }
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
            NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
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
            NullLogger<VirtualOutputIpcServer>.Instance,
            Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(TestContext.Current.CancellationToken);
        var client = new VirtualOutputBrokerClient(endpoint);

        // Act
        var registration = await client.RegisterAsync(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = "invalid-token",
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        }, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(registration.Accepted);
        Assert.Empty(status.GetSnapshots());
        await server.StopAsync(TestContext.Current.CancellationToken);
        server.Dispose();
    }

    /// <summary>Verifies unexpected maintenance faults surface immediately and cannot skip the server's final stream cleanup.</summary>
    [Fact]
    public async Task Server_ExpirationFailure_FaultsAndCleansUpBeforeShutdown()
    {
        // Arrange
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(15));
        var endpoint = CreateEndpoint();
        using var sessions = new VirtualOutputSessionAuthority();
        var deviceId = VirtualDeviceId.New();
        var ticket = sessions.CreateActivation(deviceId, "test-worker", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.OpenStream },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var registration = sessions.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = deviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        var failure = new InvalidOperationException("Unexpected maintenance authorization failure.");
        var failAuthorization = 0;
        var authority = Substitute.For<IVirtualOutputSessionAuthority>();
        authority.Authorize(Arg.Any<VirtualOutputSessionContext>(), Arg.Any<VirtualOutputCapability>()).Returns(call =>
            Volatile.Read(ref failAuthorization) == 0
                ? sessions.Authorize(call.ArgAt<VirtualOutputSessionContext>(0), call.ArgAt<VirtualOutputCapability>(1))
                : throw failure);
        var activeStreams = new ActiveStreamRegistry();
        var stream = new BlockingReadStream();
        var streamId = StreamSessionId.New();
        activeStreams.Register(new ActiveStreamSnapshot(streamId.ToString(), "48.3", HostedStreamFormat.MpegTs, DateTime.UtcNow, null, []));
        var media = new MediaProbeResult([], null);
        var broker = Substitute.For<IVirtualOutputBroker>();
        broker.OpenStreamAsync(Arg.Any<VirtualOutputOpenStreamRequest>(), Arg.Any<CancellationToken>()).Returns(new VirtualOutputBrokeredStream(
            activeStreams, new VirtualMpegTsOutput(stream, media, media, new VirtualTranscodeProfile(), new ActiveStreamFlowContext()),
            new VirtualOutputOpenStreamResponse { Accepted = true, StreamSessionId = streamId, ContentType = "video/mp2t", Source = media }));
        using var server = new VirtualOutputIpcServer(endpoint, authority, broker, new VirtualOutputRuntimeStatus(),
            CreateSettings(deviceId, maximumOutstandingBrokerCalls: 1), activeStreams, NullLogger<VirtualOutputIpcServer>.Instance, Substitute.For<IPluginConnectionBudget>());
        await server.StartAsync(watchdog.Token);
        try
        {
            var client = new VirtualOutputBrokerClient(endpoint);
            var opened = await client.OpenStreamAsync(new VirtualOutputOpenStreamRequest
            {
                Session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation },
                RequestId = VirtualOutputStreamRequestId.New(),
                ChannelKey = "48.3"
            }, watchdog.Token);
            // Release the transport directly because this test intentionally terminates the broker before client cleanup.
            await using var connection = opened.Stream;

            // Act
            Volatile.Write(ref failAuthorization, 1);
            var observed = await Record.ExceptionAsync(() => server.ExecuteTask!.WaitAsync(watchdog.Token));

            // Assert
            Assert.Same(failure, observed);
            Assert.True(stream.IsDisposed);
            Assert.Empty(activeStreams.GetActiveStreams());
        }
        finally
        {
            await server.StopAsync(TestContext.Current.CancellationToken);
        }
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

    private sealed class FailingMediaStream : MemoryStream
    {
        /// <summary>Creates a stream with a single valid transport byte before a terminal processor failure.</summary>
        public FailingMediaStream() : base([0x47])
        {
        }

        /// <inheritdoc />
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                throw new MediaProcessException(234, "corrupt packet; invalid frame dimensions");
            }
            return base.ReadAsync(buffer, cancellationToken);
        }

        /// <inheritdoc />
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) => CopyCheckedAsync(destination, cancellationToken);

        private async Task CopyCheckedAsync(Stream destination, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            while (await ReadAsync(buffer, cancellationToken) is var count && count > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets completion of owned source disposal.</summary>
        internal Task Disposed => _disposed.Task;

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
