using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Lineup.Plugin.LineupLink.Physical;
using Lineup.Plugin.LineupLink.Protocol;
using Lineup.Plugin.LineupLink.Virtual;
using Lineup.Core.VirtualOutputs.Protocol;
using Lineup.Core.VirtualOutputs.Runtime;
using Lineup.App.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.App.Web.Tests.Services;

/// <summary>Verifies equivalent-source protection and capacity failures survive Lineup Link publication and downstream import.</summary>
public sealed class LineupLinkProtectionPublicationTests
{
    /// <summary>Verifies shutdown tolerates the listener's pending accept completing concurrently with cancellation and close.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LineupServer_DisposeWithPendingAccept_DoesNotFault(bool serveRequest)
    {
        // Arrange
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        for (var index = 0; index < 20; index++)
        {
            var server = new LineupServer(new LineupLinkLineup { VirtualDeviceId = Guid.NewGuid() });
            if (serveRequest)
            {
                using var response = await client.GetAsync(server.BaseUri, TestContext.Current.CancellationToken);
                response.EnsureSuccessStatusCode();
            }

            // Act
            var failure = await Record.ExceptionAsync(() => server.DisposeAsync().AsTask());

            // Assert
            Assert.Null(failure);
        }
    }

    /// <summary>Verifies real remote failures retain capacity codes through physical import, shared acquisition and management re-export.</summary>
    [Theory]
    [InlineData("resource_limit", 429, "resource_limit")]
    [InlineData("stream_limit", 429, "stream_limit")]
    [InlineData("source_capacity_exceeded", 503, "source_capacity_exceeded")]
    [InlineData("source_unavailable", 503, "source_unavailable")]
    [InlineData("source_no_signal", 503, "source_no_signal")]
    [InlineData("malformed", 429, "source_unavailable")]
    public async Task GetStreamAsync_RemoteFailure_PreservesCapacityClassification(string code, int remoteStatus, string expectedCode)
    {
        // Arrange
        var id = VirtualDeviceId.New();
        var driver = new LineupLinkVirtualTunerDriver();
        var grant = LineupLinkAccessGrantManager.Create(new LineupLinkVirtualConfiguration
        {
            RequireEncryption = false,
            PublicBaseUrl = "http://localhost:8080/"
        }, "capacity-test", null, DateTime.UtcNow);
        var output = new VirtualDeviceDefinition
        {
            Id = id,
            Name = "Re-export",
            Adapter = new AdapterConfiguration(driver.Kind, JsonSerializer.SerializeToElement(grant.Configuration))
        };
        var lineup = new LineupLinkLineup
        {
            VirtualDeviceId = id.Value,
            Channels = [new LineupLinkChannel { Key = "7.1", Number = "7.1", Name = "News" }]
        };
        await using var server = new LineupServer(lineup, code, remoteStatus);
        using var client = new LineupLinkClient(new LineupLinkPhysicalConfiguration
        {
            BaseUrl = server.BaseUri.AbsoluteUri,
            VirtualDeviceId = id.Value,
            AccessToken = "test-token",
            TransportSecurity = LineupLinkTransportSecurity.Http
        });
        using var importedDevice = new LineupLinkPhysicalTunerDevice(PhysicalDeviceId.New(), "Imported",
            new LineupLinkManifest { InstanceId = Guid.NewGuid(), VirtualDeviceId = id.Value, Name = "Source", TunerCount = 1 }, client);
        var imported = Assert.Single(await importedDevice.RefreshLineupAsync(TestContext.Current.CancellationToken));
        var candidate = new ResolvedStreamingCandidate
        {
            PhysicalDevice = importedDevice,
            SourceChannelId = imported.Id,
            Acquisition = new StreamAcquisitionDescriptor(importedDevice.Snapshot.Id,
                PhysicalAcquisitionKey.Create(new PhysicalStreamRequest { ChannelId = imported.Id }), importedDevice.Snapshot.Capacity),
            PhysicalProfile = new PhysicalTranscodeProfile(),
            DiagnosticUri = server.BaseUri
        };
        var route = new ResolvedStreamingRoute
        {
            VirtualDevice = output,
            PhysicalDevice = importedDevice,
            SourceChannelId = imported.Id,
            Acquisition = candidate.Acquisition,
            DiagnosticUri = candidate.DiagnosticUri,
            Candidates = [candidate],
            SourceCandidates = [imported.Id],
            PhysicalProfile = candidate.PhysicalProfile,
            VirtualProfile = new VirtualTranscodeProfile()
        };
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings { Streaming = new StreamingConfiguration { VirtualDevices = [output] } });
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.ResolveVirtualAsync(id, "7.1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ResolvedStreamingRoute?>(route));
        var graph = new StreamingSessionGraph(new PhysicalCapacityLeaseRegistry(), NullLogger<StreamingSessionGraph>.Instance);
        var multiplexer = new TunerStreamMultiplexer(graph, Substitute.For<IMediaProcessFactory>(), NullLogger<TunerStreamMultiplexer>.Instance);
        var streams = new VirtualMpegTsOutputService(routes, settings, Substitute.For<IMediaProbeService>(), multiplexer,
            Substitute.For<IChannelSlateStreamFactory>(), NullLogger<VirtualMpegTsOutputService>.Instance);
        var controller = new LineupLinkController(settings, new StreamingAdapterRegistry([], [driver], []), Substitute.For<IPhysicalDeviceCatalog>(), routes,
            Substitute.For<IGuideOrchestrator>(), streams, new ActiveStreamRegistry(), NullLogger<LineupLinkController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = "Bearer " + grant.GeneratedToken;

        // Act
        IActionResult result;
        try
        {
            result = await controller.GetStreamAsync(id.Value, "7.1", TestContext.Current.CancellationToken);
        }
        finally
        {
            await graph.StopAsync(TestContext.Current.CancellationToken);
        }

        // Assert
        var response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, response.StatusCode);
        Assert.Equal(expectedCode, Assert.IsType<LineupLinkError>(response.Value).Code);
    }

    /// <summary>Verifies a clear equivalent remains acquirable downstream while protected-only outputs honor their slate/error policy.</summary>
    [Theory]
    [InlineData(false, false, false, ProtectedContentMode.ReturnError)]
    [InlineData(false, false, true, ProtectedContentMode.ReturnError)]
    [InlineData(true, false, false, ProtectedContentMode.ReturnError)]
    [InlineData(true, false, true, ProtectedContentMode.ReturnError)]
    [InlineData(false, true, false, ProtectedContentMode.ReturnError)]
    [InlineData(false, true, true, ProtectedContentMode.ReturnError)]
    [InlineData(true, true, false, ProtectedContentMode.ReturnError)]
    [InlineData(true, true, true, ProtectedContentMode.ReturnError)]
    [InlineData(true, false, false, ProtectedContentMode.StreamSlate)]
    [InlineData(true, false, true, ProtectedContentMode.StreamSlate)]
    [InlineData(true, true, false, ProtectedContentMode.StreamSlate)]
    [InlineData(true, true, true, ProtectedContentMode.StreamSlate)]
    public async Task GetLineupAsync_EquivalentProtection_ConsumerCanAcquireClearOutput(bool preferredProtected, bool fallbackProtected, bool dedicated, ProtectedContentMode mode)
    {
        // Arrange
        var id = VirtualDeviceId.New();
        var driver = new LineupLinkVirtualTunerDriver();
        var grant = LineupLinkAccessGrantManager.Create(new LineupLinkVirtualConfiguration
        {
            RequireEncryption = false,
            PublicBaseUrl = "http://localhost:8080/"
        }, "protection-test", null, DateTime.UtcNow);
        var output = new VirtualDeviceDefinition
        {
            Id = id,
            Name = "Export",
            Hosting = dedicated ? new VirtualOutputHostingDefinition() : null,
            Adapter = new AdapterConfiguration(driver.Kind, JsonSerializer.SerializeToElement(grant.Configuration))
        };
        var physical = Enumerable.Range(0, 2).Select(index => new PhysicalDeviceDefinition
        {
            Id = PhysicalDeviceId.New(),
            Name = $"Source {index}",
            Order = index,
            Adapter = new AdapterConfiguration("fake", JsonSerializer.SerializeToElement(new { }))
        }).ToArray();
        var group = Guid.NewGuid();
        var identities = physical.Select((device, index) => new PhysicalChannelIdentity
        {
            EquivalenceGroupId = group,
            Source = new PhysicalSourceChannel
            {
                Id = new SourceChannelId(device.Id, "7.1"),
                ProviderNumber = "7.1",
                ProviderName = "News",
                Metadata = new Dictionary<string, string>
                {
                    ["drm"] = (index == 0 ? preferredProtected : fallbackProtected).ToString(),
                    ["preferred-detail"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
            }
        }).ToArray();
        var projected = ChannelTopologyResolver.Resolve(output, physical, identities).Channels;
        var settings = Substitute.For<ILineupSettingsService>();
        settings.Settings.Returns(new LineupSettings
        {
            ProtectedContentMode = mode,
            Streaming = new StreamingConfiguration { PhysicalDevices = physical, VirtualDevices = [output] }
        });
        var routes = Substitute.For<IStreamingRouteResolver>();
        routes.GetVirtualLineupAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult(projected));
        var adapters = new StreamingAdapterRegistry([], [driver], []);
        Assert.NotNull(grant.GeneratedToken);
        var exported = await ExportAsync(output, settings, routes, adapters, grant.GeneratedToken, dedicated);
        await using var server = new LineupServer(exported);
        using var client = new LineupLinkClient(new LineupLinkPhysicalConfiguration
        {
            BaseUrl = server.BaseUri.AbsoluteUri,
            VirtualDeviceId = id.Value,
            AccessToken = grant.GeneratedToken,
            TransportSecurity = LineupLinkTransportSecurity.Http
        });
        using var importedDevice = new LineupLinkPhysicalTunerDevice(PhysicalDeviceId.New(), "Imported",
            new LineupLinkManifest { InstanceId = Guid.NewGuid(), VirtualDeviceId = id.Value, Name = output.Name, TunerCount = 2 }, client);
        var imported = Assert.Single(await importedDevice.RefreshLineupAsync(TestContext.Current.CancellationToken));
        var candidate = new ResolvedStreamingCandidate
        {
            PhysicalDevice = importedDevice,
            SourceChannelId = imported.Id,
            SourceMetadata = imported.Metadata,
            Acquisition = new StreamAcquisitionDescriptor(importedDevice.Snapshot.Id,
                PhysicalAcquisitionKey.Create(new PhysicalStreamRequest { ChannelId = imported.Id }), importedDevice.Snapshot.Capacity),
            PhysicalProfile = new PhysicalTranscodeProfile(),
            DiagnosticUri = new Uri("http://source.invalid/7.1")
        };
        var consumerRoute = new ResolvedStreamingRoute
        {
            VirtualDevice = output,
            PhysicalDevice = importedDevice,
            SourceChannelId = imported.Id,
            Acquisition = candidate.Acquisition,
            DiagnosticUri = candidate.DiagnosticUri,
            Candidates = [candidate],
            SourceCandidates = [imported.Id],
            PhysicalProfile = candidate.PhysicalProfile,
            VirtualProfile = new VirtualTranscodeProfile()
        };
        var consumerRoutes = Substitute.For<IStreamingRouteResolver>();
        consumerRoutes.ResolveVirtualAsync(id, "7.1", Arg.Any<CancellationToken>()).Returns(Task.FromResult<ResolvedStreamingRoute?>(consumerRoute));
        var multiplexer = Substitute.For<ITunerStreamMultiplexer>();
        multiplexer.SubscribeFirstAvailableAsync(consumerRoute, Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<ResolvedTunerStream>(new AcquisitionReachedException()));
        var consumerSettings = Substitute.For<ILineupSettingsService>();
        consumerSettings.Settings.Returns(new LineupSettings { ProtectedContentMode = ProtectedContentMode.ReturnError });
        var service = new VirtualMpegTsOutputService(consumerRoutes, consumerSettings, Substitute.For<IMediaProbeService>(), multiplexer,
            Substitute.For<IChannelSlateStreamFactory>(), NullLogger<VirtualMpegTsOutputService>.Instance);

        // Act
        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var stream = await service.OpenAsync(id, "7.1", TestContext.Current.CancellationToken);
        });

        // Assert
        var knownProtected = preferredProtected && fallbackProtected && mode == ProtectedContentMode.ReturnError;
        Assert.Equal(knownProtected, ProtectedContentDetector.IsProtected(imported.Metadata));
        Assert.Equal("0", imported.Metadata["preferred-detail"]);
        Assert.Equal(preferredProtected.ToString(), identities[0].Source.Metadata["drm"]);
        if (knownProtected)
        {
            Assert.Equal(PhysicalStreamFailureCategory.ProtectedContent, Assert.IsType<StreamAcquisitionException>(exception).Category);
            await multiplexer.DidNotReceive().SubscribeFirstAvailableAsync(Arg.Any<ResolvedStreamingRoute>(), Arg.Any<CancellationToken>());
        }
        else
        {
            Assert.IsType<AcquisitionReachedException>(exception);
            await multiplexer.Received(1).SubscribeFirstAvailableAsync(consumerRoute, Arg.Any<CancellationToken>());
        }
    }

    private static async Task<LineupLinkLineup> ExportAsync(VirtualDeviceDefinition output, ILineupSettingsService settings, IStreamingRouteResolver routes, IStreamingAdapterRegistry adapters, string token, bool dedicated)
    {
        if (!dedicated)
        {
            var controller = new LineupLinkController(settings, adapters, Substitute.For<IPhysicalDeviceCatalog>(), routes,
                Substitute.For<IGuideOrchestrator>(), Substitute.For<IVirtualMpegTsOutputService>(), new ActiveStreamRegistry(), NullLogger<LineupLinkController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers.Authorization = "Bearer " + token;
            return Assert.IsType<LineupLinkLineup>((await controller.GetLineupAsync(output.Id.Value, TestContext.Current.CancellationToken)).Value);
        }
        using var authority = new VirtualOutputSessionAuthority();
        var ticket = authority.CreateActivation(output.Id, "test-output", new HashSet<VirtualOutputCapability> { VirtualOutputCapability.ReadLineup },
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        var registration = authority.Register(new VirtualOutputWorkerRegistrationRequest
        {
            WorkerId = ticket.WorkerId,
            VirtualDeviceId = ticket.VirtualDeviceId,
            Generation = ticket.Generation,
            BootstrapToken = ticket.BootstrapToken,
            MinimumProtocolVersion = VirtualOutputProtocol.MinimumVersion,
            MaximumProtocolVersion = VirtualOutputProtocol.MaximumVersion
        });
        var broker = new VirtualOutputBroker(authority, settings, adapters, Substitute.For<IPhysicalDeviceCatalog>(), routes,
            Substitute.For<IGuideOrchestrator>(), Substitute.For<IVirtualMpegTsOutputService>(), new ActiveStreamRegistry());
        var response = await broker.GetLineupAsync(new VirtualOutputLineupRequest
        {
            Session = new VirtualOutputSessionContext { SessionId = registration.SessionId!.Value, Generation = ticket.Generation }
        }, TestContext.Current.CancellationToken);
        return new LineupLinkLineup
        {
            VirtualDeviceId = output.Id.Value,
            Channels = response.Channels.Select(channel => new LineupLinkChannel
            {
                Key = channel.Number,
                Number = channel.Number,
                Name = channel.Name,
                Metadata = channel.Metadata
            }).ToArray()
        };
    }

    private sealed class AcquisitionReachedException : Exception;

    private sealed class LineupServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _task;

        /// <summary>Starts an owned loopback endpoint serving the production export.</summary>
        public LineupServer(LineupLinkLineup lineup, string? failureCode = null, int status = 503)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.AbsoluteUri);
            _listener.Start();
            _task = ServeAsync(lineup, failureCode, status);
        }

        /// <summary>Gets the owned endpoint's ephemeral address.</summary>
        public Uri BaseUri { get; }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Close();
            try
            {
                await _task;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException exception) when (_shutdown.IsCancellationRequested && exception.ObjectName == typeof(HttpListener).FullName)
            {
            }
            finally
            {
                _shutdown.Dispose();
            }
        }

        private async Task ServeAsync(LineupLinkLineup lineup, string? failureCode, int status)
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                context.Response.ContentType = "application/json";
                if (context.Request.Url!.AbsolutePath.Contains("/streams/", StringComparison.Ordinal) && failureCode is not null)
                {
                    context.Response.StatusCode = status;
                    var body = failureCode == "malformed"
                        ? "{"
                        : JsonSerializer.Serialize(new LineupLinkError { Code = failureCode, Message = "Remote stream refused." }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                    await context.Response.OutputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body), _shutdown.Token);
                }
                else
                {
                    await JsonSerializer.SerializeAsync(context.Response.OutputStream, lineup, new JsonSerializerOptions(JsonSerializerDefaults.Web), _shutdown.Token);
                }
                context.Response.Close();
            }
        }
    }
}
