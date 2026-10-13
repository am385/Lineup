using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lineup.LineupLink.Tests;

/// <summary>Tests the Lineup Link protocol, security, adapters, and registration.</summary>
public sealed class LineupLinkTests
{
    /// <summary>Verifies client secrets remain redacted and preserve edits do not replace them.</summary>
    [Fact]
    public void PhysicalSettings_PreserveSecret_KeepsRedactedToken()
    {
        // Arrange
        var driver = new LineupLinkPhysicalTunerDriver();
        var configuration = new AdapterConfiguration(
            driver.Kind,
            JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = "https://remote.example/",
                VirtualDeviceId = Guid.NewGuid(),
                AccessToken = "client-secret"
            }));

        // Act
        var snapshot = driver.GetSettings(configuration);
        var result = driver.ApplySettings(
            configuration,
            [new AdapterSettingEdit { Key = "accessToken", Action = AdapterSettingEditAction.Preserve }]);
        var updated = result.Configuration?.Configuration.Deserialize<LineupLinkPhysicalConfiguration>();
        var tokenSnapshot = Assert.Single(snapshot.Values, value => value.Key == "accessToken");

        // Assert
        Assert.Empty(AdapterSettingsValidator.ValidateDescriptor(driver.SettingsDescriptor));
        Assert.Empty(AdapterSettingsValidator.ValidateSnapshot(driver.SettingsDescriptor, snapshot));
        Assert.True(tokenSnapshot.HasValue);
        Assert.Null(tokenSnapshot.Value);
        Assert.True(result.Succeeded);
        Assert.Equal("client-secret", updated?.AccessToken);
    }

    /// <summary>Verifies physical probing maps the authenticated remote manifest into safe identity details.</summary>
    [Fact]
    public async Task PhysicalProbe_Manifest_ReturnsIdentityAndSuggestedName()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            Assert.Equal("Bearer client-secret", context.Request.Headers["Authorization"]);
            Assert.Equal(LineupLinkProtocol.ManifestPath(virtualDeviceId), context.Request.Url?.AbsolutePath);
            var body = JsonSerializer.Serialize(new LineupLinkManifest
            {
                InstanceId = instanceId,
                VirtualDeviceId = virtualDeviceId,
                Name = "Remote Living Room",
                TunerCount = 6,
                GuideAvailable = true
            });
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }, TestContext.Current.CancellationToken);
        var driver = new LineupLinkPhysicalTunerDriver();
        var configuration = new AdapterConfiguration(
            driver.Kind,
            JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = $"http://127.0.0.1:{port}/",
                VirtualDeviceId = virtualDeviceId,
                AccessToken = "client-secret",
                TransportSecurity = LineupLinkTransportSecurity.Http
            }));

        // Act
        var result = await ((IPhysicalDeviceProbe)driver).ProbeAsync(configuration, TestContext.Current.CancellationToken);
        await responseTask;

        // Assert
        Assert.Equal("Remote Living Room", result.SuggestedName);
        Assert.Contains(result.Details, detail => detail.Label == "Lineup instance ID" && detail.Value == instanceId.ToString("D"));
        Assert.Contains(result.Details, detail => detail.Label == "Virtual device ID" && detail.Value == virtualDeviceId.ToString("D"));
        Assert.Contains(result.Details, detail => detail.Label == "Tuners" && detail.Value == "6");
        Assert.DoesNotContain(result.Details, detail => detail.Value.Contains("client-secret", StringComparison.Ordinal));
        Assert.True(driver.Capabilities.SupportsTunerStatus);
    }

    /// <summary>Verifies physical probing does not expose a remote error body that echoes the configured token.</summary>
    [Fact]
    public async Task PhysicalProbe_UnauthorizedRemoteError_DoesNotExposeToken()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes("""{"message":"client-secret"}""");
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }, TestContext.Current.CancellationToken);
        var driver = new LineupLinkPhysicalTunerDriver();
        var configuration = new AdapterConfiguration(
            driver.Kind,
            JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = $"http://127.0.0.1:{port}/",
                VirtualDeviceId = virtualDeviceId,
                AccessToken = "client-secret",
                TransportSecurity = LineupLinkTransportSecurity.Http
            }));

        // Act
        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            async () => await ((IPhysicalDeviceProbe)driver).ProbeAsync(configuration, TestContext.Current.CancellationToken));
        await responseTask;

        // Assert
        Assert.Contains("rejected the configured access token", exception.Message);
        Assert.DoesNotContain("client-secret", exception.Message);
    }

    /// <summary>Verifies remote lineup metadata is retained when channels are projected as physical sources.</summary>
    [Fact]
    public async Task PhysicalDevice_RefreshLineup_RetainsRemoteChannelMetadata()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var physicalDeviceId = PhysicalDeviceId.New();
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            Assert.Equal(LineupLinkProtocol.LineupPath(virtualDeviceId), context.Request.Url?.AbsolutePath);
            var body = JsonSerializer.Serialize(new LineupLinkLineup
            {
                VirtualDeviceId = virtualDeviceId,
                Channels =
                [
                    new LineupLinkChannel
                    {
                        Key = "2.3",
                        Number = "2.3",
                        Name = "News",
                        Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["hd"] = "True",
                            ["videoCodec"] = "mpeg2video",
                            ["audioCodec"] = "ac3",
                            ["signalStrength"] = "95",
                            ["signalQuality"] = "88"
                        }
                    }
                ]
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }, TestContext.Current.CancellationToken);
        using var client = new LineupLinkClient(new LineupLinkPhysicalConfiguration
        {
            BaseUrl = $"http://127.0.0.1:{port}/",
            VirtualDeviceId = virtualDeviceId,
            AccessToken = "client-secret",
            TransportSecurity = LineupLinkTransportSecurity.Http
        });
        var device = new LineupLinkPhysicalTunerDevice(
            physicalDeviceId,
            "Remote Lineup Link",
            new LineupLinkManifest
            {
                InstanceId = instanceId,
                VirtualDeviceId = virtualDeviceId,
                Name = "Remote output"
            },
            client);

        // Act
        var channels = await device.RefreshLineupAsync(TestContext.Current.CancellationToken);
        await responseTask;

        // Assert
        var channel = Assert.Single(channels);
        Assert.Equal("True", channel.Metadata["hd"]);
        Assert.Equal("mpeg2video", channel.Metadata["videoCodec"]);
        Assert.Equal("ac3", channel.Metadata["audioCodec"]);
        Assert.Equal("95", channel.Metadata["signalStrength"]);
        Assert.Equal("88", channel.Metadata["signalQuality"]);
        Assert.Equal(instanceId.ToString("D"), channel.Metadata["lineupLinkInstanceId"]);
        Assert.Equal(virtualDeviceId.ToString("D"), channel.Metadata["lineupLinkVirtualDeviceId"]);
    }

    /// <summary>Verifies Lineup Link physical status exposes remote identity and idle receiver capacity.</summary>
    [Fact]
    public async Task PhysicalDevice_RefreshStatus_ReturnsRemoteDetailsAndIdleReceivers()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var physicalDeviceId = PhysicalDeviceId.New();
        var manifest = new LineupLinkManifest
        {
            InstanceId = instanceId,
            VirtualDeviceId = virtualDeviceId,
            Name = "Remote output",
            TunerCount = 3,
            GuideAvailable = true
        };
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            Assert.Equal(LineupLinkProtocol.ManifestPath(virtualDeviceId), context.Request.Url?.AbsolutePath);
            await WriteJsonAsync(context.Response, manifest);
        }, TestContext.Current.CancellationToken);
        using var device = new LineupLinkPhysicalTunerDevice(
            physicalDeviceId,
            "Remote Lineup Link",
            manifest,
            new LineupLinkClient(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = $"http://127.0.0.1:{port}/",
                VirtualDeviceId = virtualDeviceId,
                AccessToken = "client-secret",
                TransportSecurity = LineupLinkTransportSecurity.Http
            }));

        // Act
        var status = await device.RefreshStatusAsync(TestContext.Current.CancellationToken);
        await responseTask;

        // Assert
        Assert.Equal("Remote output", status.Details["Remote name"]);
        Assert.Equal($"http://127.0.0.1:{port}/", status.Details["Remote base URL"]);
        Assert.Equal(instanceId.ToString("D"), status.Details["Lineup instance ID"]);
        Assert.Equal(virtualDeviceId.ToString("D"), status.Details["Virtual device ID"]);
        Assert.Equal("2", status.Details["Protocol version"]);
        Assert.Equal("3", status.Details["Tuners"]);
        Assert.Equal("Available", status.Details["Guide"]);
        Assert.Equal(3, status.Receivers.Count);
        Assert.All(status.Receivers, receiver =>
        {
            Assert.False(receiver.IsActive);
            Assert.False(receiver.IsStreaming);
        });
    }

    /// <summary>Verifies Lineup Link physical status tracks local receiver use for an active remote stream.</summary>
    [Fact]
    public async Task PhysicalDevice_OpenStream_TracksReceiverUntilLeaseIsDisposed()
    {
        // Arrange
        var virtualDeviceId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();
        var physicalDeviceId = PhysicalDeviceId.New();
        var manifest = new LineupLinkManifest
        {
            InstanceId = instanceId,
            VirtualDeviceId = virtualDeviceId,
            Name = "Remote output",
            TunerCount = 2
        };
        var lineup = new LineupLinkLineup
        {
            VirtualDeviceId = virtualDeviceId,
            Channels =
            [
                new LineupLinkChannel
                {
                    Key = "7.1",
                    Number = "7.1",
                    Name = "News"
                }
            ]
        };
        var port = GetAvailablePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            for (var requestIndex = 0; requestIndex < 4; requestIndex++)
            {
                var context = await listener.GetContextAsync();
                switch (requestIndex)
                {
                    case 0:
                        Assert.Equal(LineupLinkProtocol.LineupPath(virtualDeviceId), context.Request.Url?.AbsolutePath);
                        await WriteJsonAsync(context.Response, lineup);
                        break;
                    case 1:
                        Assert.Equal(LineupLinkProtocol.StreamPath(virtualDeviceId, "7.1"), context.Request.Url?.AbsolutePath);
                        context.Response.ContentType = LineupLinkProtocol.MpegTsMediaType;
                        context.Response.ContentLength64 = 3;
                        await context.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 });
                        context.Response.Close();
                        break;
                    default:
                        Assert.Equal(LineupLinkProtocol.ManifestPath(virtualDeviceId), context.Request.Url?.AbsolutePath);
                        await WriteJsonAsync(context.Response, manifest);
                        break;
                }
            }
        }, TestContext.Current.CancellationToken);
        using var device = new LineupLinkPhysicalTunerDevice(
            physicalDeviceId,
            "Remote Lineup Link",
            manifest,
            new LineupLinkClient(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = $"http://127.0.0.1:{port}/",
                VirtualDeviceId = virtualDeviceId,
                AccessToken = "client-secret",
                TransportSecurity = LineupLinkTransportSecurity.Http
            }));
        var channels = await device.RefreshLineupAsync(TestContext.Current.CancellationToken);

        // Act
        var lease = await device.OpenStreamAsync(
            new PhysicalStreamRequest { ChannelId = Assert.Single(channels).Id },
            TestContext.Current.CancellationToken);
        var activeStatus = await device.RefreshStatusAsync(TestContext.Current.CancellationToken);
        await lease.DisposeAsync();
        var idleStatus = await device.RefreshStatusAsync(TestContext.Current.CancellationToken);
        await responseTask;

        // Assert
        var activeReceiver = Assert.Single(activeStatus.Receivers, receiver => receiver.IsStreaming);
        Assert.Equal(0, activeReceiver.Index);
        Assert.Equal("News", activeReceiver.Channel);
        Assert.Equal("7.1", activeReceiver.VirtualChannel);
        Assert.Equal($"http://127.0.0.1:{port}{LineupLinkProtocol.StreamPath(virtualDeviceId, "7.1")}", activeReceiver.Target);
        Assert.All(idleStatus.Receivers, receiver =>
        {
            Assert.False(receiver.IsActive);
            Assert.False(receiver.IsStreaming);
        });
    }

    /// <summary>Verifies multiple grants authenticate independently and revocation affects only the selected consumer.</summary>
    [Fact]
    public void AccessGrants_CreateAndRevoke_AuthenticateIndependently()
    {
        // Arrange
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new LineupLinkVirtualConfiguration
        {
            InstanceId = Guid.NewGuid(),
            PublicBaseUrl = "https://lineup.example/"
        };
        var first = LineupLinkAccessGrantManager.Create(configuration, "Alice", null, now);
        var second = LineupLinkAccessGrantManager.Create(first.Configuration!, "Bob", now.AddDays(30), now);

        // Act
        var revoked = LineupLinkAccessGrantManager.Revoke(second.Configuration!, first.Grant!.Id, now.AddMinutes(1));
        var alice = LineupLinkAccessGrantAuthenticator.Authenticate(first.GeneratedToken, revoked.Configuration!, now.AddMinutes(2));
        var bob = LineupLinkAccessGrantAuthenticator.Authenticate(second.GeneratedToken, revoked.Configuration!, now.AddMinutes(2));

        // Assert
        Assert.Null(alice);
        Assert.NotNull(bob);
        Assert.Equal(second.Grant!.Id, bob.Id);
        Assert.Equal("Bob", bob.Name);
    }

    /// <summary>Verifies rotating one grant invalidates only its previous token.</summary>
    [Fact]
    public void AccessGrants_Rotate_InvalidatesOnlyPreviousToken()
    {
        // Arrange
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new LineupLinkVirtualConfiguration { InstanceId = Guid.NewGuid() };
        var first = LineupLinkAccessGrantManager.Create(configuration, "Alice", null, now);
        var second = LineupLinkAccessGrantManager.Create(first.Configuration!, "Bob", null, now);

        // Act
        var rotated = LineupLinkAccessGrantManager.Rotate(second.Configuration!, first.Grant!.Id, now.AddMinutes(1));

        // Assert
        Assert.Null(LineupLinkAccessGrantAuthenticator.Authenticate(first.GeneratedToken, rotated.Configuration!, now.AddMinutes(2)));
        Assert.Equal(first.Grant.Id, LineupLinkAccessGrantAuthenticator.Authenticate(rotated.GeneratedToken, rotated.Configuration!, now.AddMinutes(2))?.Id);
        Assert.Equal(second.Grant!.Id, LineupLinkAccessGrantAuthenticator.Authenticate(second.GeneratedToken, rotated.Configuration!, now.AddMinutes(2))?.Id);
    }

    /// <summary>Verifies only revoked grants can be purged, including the final grant, without affecting other consumers.</summary>
    [Fact]
    public void AccessGrants_Purge_RequiresRevocationAndRetainsRemainingGrant()
    {
        // Arrange
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new LineupLinkVirtualConfiguration { InstanceId = Guid.NewGuid() };
        var first = LineupLinkAccessGrantManager.Create(configuration, "Alice", null, now);
        var second = LineupLinkAccessGrantManager.Create(first.Configuration!, "Bob", null, now);

        // Act
        var activePurge = LineupLinkAccessGrantManager.Purge(second.Configuration!, first.Grant!.Id);
        var revoked = LineupLinkAccessGrantManager.Revoke(second.Configuration!, first.Grant.Id, now.AddMinutes(1));
        var purged = LineupLinkAccessGrantManager.Purge(revoked.Configuration!, first.Grant.Id);
        var finalRevoked = LineupLinkAccessGrantManager.Revoke(purged.Configuration!, second.Grant!.Id, now.AddMinutes(2));
        var finalPurge = LineupLinkAccessGrantManager.Purge(finalRevoked.Configuration!, second.Grant.Id);

        // Assert
        Assert.Null(activePurge.Configuration);
        Assert.Contains(activePurge.Errors, error => error.Contains("revoked", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(first.Grant.Id, purged.Grant?.Id);
        Assert.Equal(second.Grant.Id, Assert.Single(purged.Configuration!.AccessGrants).Id);
        Assert.Null(LineupLinkAccessGrantAuthenticator.Authenticate(first.GeneratedToken, purged.Configuration, now.AddMinutes(2)));
        Assert.Equal(second.Grant.Id, LineupLinkAccessGrantAuthenticator.Authenticate(second.GeneratedToken, purged.Configuration, now.AddMinutes(2))?.Id);
        Assert.NotNull(finalPurge.Configuration);
        Assert.Empty(finalPurge.Configuration.AccessGrants);
        Assert.Null(LineupLinkAccessGrantAuthenticator.Authenticate(second.GeneratedToken, finalPurge.Configuration, now.AddMinutes(3)));
    }

    /// <summary>Verifies a Lineup Link output with no grants is valid and rejects every bearer token.</summary>
    [Fact]
    public void VirtualDriver_ZeroAccessGrants_IsValidAndDeniesAuthentication()
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var typed = new LineupLinkVirtualConfiguration
        {
            InstanceId = Guid.NewGuid(),
            PublicBaseUrl = "https://lineup.example/"
        };
        var configuration = new AdapterConfiguration(
            driver.Kind,
            JsonSerializer.SerializeToElement(typed));

        // Act
        var errors = driver.Validate(configuration);
        var authenticated = LineupLinkAccessGrantAuthenticator.Authenticate(LineupLinkToken.Generate(), typed, DateTime.UtcNow);

        // Assert
        Assert.Empty(errors);
        Assert.Null(authenticated);
        Assert.Empty(driver.GetAccessGrants(configuration));
    }

    /// <summary>Verifies expired grants and duplicate consumer names are rejected safely.</summary>
    [Fact]
    public void AccessGrants_ExpirationAndDuplicateName_AreEnforced()
    {
        // Arrange
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var configuration = new LineupLinkVirtualConfiguration { InstanceId = Guid.NewGuid() };
        var created = LineupLinkAccessGrantManager.Create(configuration, "Alice", now.AddMinutes(1), now);

        // Act
        var expired = LineupLinkAccessGrantAuthenticator.Authenticate(created.GeneratedToken, created.Configuration!, now.AddMinutes(2));
        var duplicate = LineupLinkAccessGrantManager.Create(created.Configuration!, "alice", null, now);

        // Assert
        Assert.Null(expired);
        Assert.Null(duplicate.Configuration);
        Assert.Contains(duplicate.Errors, error => error.Contains("unique", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Verifies bearer tokens are random, persisted only as hashes, and compared correctly.</summary>
    [Fact]
    public void Token_HashesAndVerifiesWithoutPersistingRawValue()
    {
        // Arrange
        var first = LineupLinkToken.Generate();
        var second = LineupLinkToken.Generate();

        // Act
        var hash = LineupLinkToken.Hash(first);

        // Assert
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, hash);
        Assert.True(LineupLinkToken.Verify(first, hash));
        Assert.False(LineupLinkToken.Verify(second, hash));
        Assert.False(LineupLinkToken.Verify(first, "not-base64"));
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value));
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    /// <summary>Verifies connection bundles preserve all pair settings and reject incompatible versions.</summary>
    [Fact]
    public void ConnectionBundle_RoundTripsAndRejectsUnknownVersion()
    {
        // Arrange
        var bundle = new LineupLinkConnectionBundle
        {
            BaseUrl = "https://lineup.example:8443",
            VirtualDeviceId = Guid.NewGuid(),
            AccessToken = LineupLinkToken.Generate(),
            TransportSecurity = LineupLinkTransportSecurity.Https,
            CertificateSha256 = new string('A', 64)
        };

        // Act
        var serialized = LineupLinkConnectionBundleCodec.Serialize(bundle);
        var redacted = LineupLinkConnectionBundleCodec.SerializeRedacted(bundle);
        var restored = LineupLinkConnectionBundleCodec.Deserialize(serialized);
        var withoutAddress = LineupLinkConnectionBundleCodec.Serialize(bundle with { BaseUrl = null });
        var invalidBundle = JsonSerializer.Serialize(bundle with { BundleVersion = 2 });
        var invalidProtocol = JsonSerializer.Serialize(bundle with { ProtocolVersion = LineupLinkProtocol.Version + 1 });

        // Assert
        Assert.Equal(bundle, restored);
        using var json = JsonDocument.Parse(serialized);
        Assert.Equal(1, json.RootElement.GetProperty("bundleVersion").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.False(json.RootElement.TryGetProperty("version", out _));
        using var redactedJson = JsonDocument.Parse(redacted);
        Assert.Equal(LineupLinkConnectionBundleCodec.RedactedAccessToken, redactedJson.RootElement.GetProperty("accessToken").GetString());
        Assert.DoesNotContain(bundle.AccessToken, redacted, StringComparison.Ordinal);
        using var withoutAddressJson = JsonDocument.Parse(withoutAddress);
        Assert.Equal(JsonValueKind.Null, withoutAddressJson.RootElement.GetProperty("baseUrl").ValueKind);
        Assert.Null(LineupLinkConnectionBundleCodec.Deserialize(withoutAddress).BaseUrl);
        Assert.Throws<ArgumentException>(() => LineupLinkConnectionBundleCodec.Deserialize(invalidBundle));
        Assert.Throws<ArgumentException>(() => LineupLinkConnectionBundleCodec.Deserialize(invalidProtocol));
    }

    /// <summary>Verifies the v2 guide contract uses Americanized program terminology on the wire.</summary>
    [Fact]
    public void GuideContract_WebSerialization_UsesPrograms()
    {
        // Arrange
        var guide = new LineupLinkGuide
        {
            VirtualDeviceId = Guid.NewGuid(),
            Programs = []
        };

        // Act
        var json = JsonSerializer.SerializeToElement(guide, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // Assert
        Assert.Equal(2, LineupLinkProtocol.Version);
        Assert.True(json.TryGetProperty("programs", out _));
        Assert.False(json.TryGetProperty("programmes", out _));
    }

    /// <summary>Verifies virtual outputs reject recursive links and aggregate capacity without an HDHomeRun byte cap.</summary>
    [Fact]
    public void VirtualDriver_RejectsLineupLinkInputsAndAggregatesCapacity()
    {
        // Arrange
        var driver = new LineupLinkVirtualTunerDriver();
        var physicalDevices = new[]
        {
            Snapshot(200),
            Snapshot(100)
        };

        // Act
        var capacity = driver.GetCapacity(physicalDevices);

        // Assert
        Assert.False(driver.SupportsPhysicalAdapter(LineupLinkProtocol.AdapterKind));
        Assert.True(driver.SupportsPhysicalAdapter("hdhomerun"));
        Assert.Equal(300, capacity.TunerCount);
    }

    /// <summary>Verifies remote channel identities are deterministic and scoped by both remote identities.</summary>
    [Fact]
    public void Identity_IsDeterministicAndCollisionScoped()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        var virtualDeviceId = Guid.NewGuid();

        // Act
        var first = LineupLinkIdentity.CreateProviderKey(instanceId, virtualDeviceId, "7.10");
        var repeated = LineupLinkIdentity.CreateProviderKey(instanceId, virtualDeviceId, "7.10");
        var otherDevice = LineupLinkIdentity.CreateProviderKey(instanceId, Guid.NewGuid(), "7.10");

        // Assert
        Assert.Equal(first, repeated);
        Assert.NotEqual(first, otherDevice);
        Assert.DoesNotContain("7.10", first, StringComparison.Ordinal);
    }

    /// <summary>Verifies independent registration methods compose all Lineup Link adapters.</summary>
    [Fact]
    public void Registration_ComposesPhysicalVirtualAndGuideAdapters()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddSingleton<IPhysicalDeviceResolver>(new EmptyPhysicalDeviceResolver());

        // Act
        services
            .AddLineupLinkPhysicalTuner()
            .AddLineupLinkVirtualTuner()
            .AddLineupLinkGuideSource();
        using var provider = services.BuildServiceProvider();

        // Assert
        Assert.Contains(provider.GetServices<IPhysicalTunerDriver>(), driver => driver.Kind == LineupLinkProtocol.AdapterKind);
        Assert.Contains(provider.GetServices<IVirtualTunerDriver>(), driver => driver.Kind == LineupLinkProtocol.AdapterKind);
        Assert.Contains(provider.GetServices<Lineup.Extensibility.Guides.IGuideSourceDriver>(), driver => driver.Kind == LineupLinkProtocol.AdapterKind);
    }

    private static PhysicalDeviceSnapshot Snapshot(int tunerCount) =>
        new()
        {
            Id = PhysicalDeviceId.New(),
            Name = "Device",
            Kind = "hdhomerun",
            Capacity = new PhysicalTunerCapacity(tunerCount)
        };

    private sealed class EmptyPhysicalDeviceResolver : IPhysicalDeviceResolver
    {
        public IReadOnlyList<IPhysicalTunerDevice> Devices => [];

        public IPhysicalTunerDevice GetRequired(PhysicalDeviceId deviceId) =>
            throw new InvalidOperationException($"Physical device {deviceId} is not active.");
    }
}
