using System.Text.Json;
using Lineup.Core;
using Lineup.Core.Settings;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.SiliconDust.HDHomeRun.VirtualTuner;
using Lineup.Tui;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Lineup.Tui.Tests;

/// <summary>
/// Verifies TUI portable-settings and topology editing behavior.
/// </summary>
public sealed class TuiAdapterSettingsEditorTests
{
    /// <summary>Verifies hosting edits persist unexposed security settings while leaving a separately keyed shared policy unchanged.</summary>
    [Theory]
    [InlineData(VirtualOutputClientCertificateMode.Require, true)]
    [InlineData(VirtualOutputClientCertificateMode.Require, false)]
    [InlineData(VirtualOutputClientCertificateMode.Allow, true)]
    [InlineData(VirtualOutputClientCertificateMode.Allow, false)]
    [InlineData(VirtualOutputClientCertificateMode.None, true)]
    [InlineData(VirtualOutputClientCertificateMode.None, false)]
    public async Task BuildSecurityPolicy_HostingEdit_PersistsUnexposedSettings(VirtualOutputClientCertificateMode certificateMode, bool allowForwardedHeaders)
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-tui-security-");
        try
        {
            var path = Path.Combine(root.FullName, AppConstants.SettingsFileName);
            var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, path, root.FullName);
            await service.UpdateAsync(settings => settings.Streaming = CreateSecuredHostingConfiguration(certificateMode, allowForwardedHeaders));
            var original = service.Settings.Streaming;
            var originalPolicy = Assert.Single(original.VirtualOutputSecurityPolicies);

            // Act
            var candidate = CreateSecurityPolicyEdit(original);
            var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, candidate);
            var reloaded = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, path, root.FullName).Settings.Streaming;

            // Assert
            Assert.True(saved);
            Assert.Empty(StreamingConfigurationValidator.Validate(reloaded));
            var device = reloaded.VirtualDevices[0];
            var binding = Assert.Single(device.Hosting!.Bindings);
            var policy = reloaded.VirtualOutputSecurityPolicies.Single(item => item.Key == binding.SecurityPolicyKey);
            Assert.Equal(certificateMode, policy.ClientCertificateMode);
            Assert.Equal(allowForwardedHeaders, policy.AllowForwardedHeaders);
            Assert.Equal(originalPolicy.TrustedProxies, policy.TrustedProxies);
            Assert.Equal($"virtual-{device.Id.Value:N}-security", policy.Key);
            Assert.Equal($"{device.Name} listener security", policy.Name);
            Assert.Equal(8601, binding.Port);
            Assert.Equal("new-certificate", policy.CertificateReference);
            Assert.Equal(["198.51.100.0/24", "2001:db8::/32"], policy.AllowedNetworks);
            Assert.Equal(30, policy.MaximumConnections);
            Assert.Equal(240, policy.RequestsPerMinutePerClient);
            var sharedPolicy = reloaded.VirtualOutputSecurityPolicies.Single(item => item.Key == originalPolicy.Key);
            Assert.Equal(certificateMode, sharedPolicy.ClientCertificateMode);
            Assert.Equal(allowForwardedHeaders, sharedPolicy.AllowForwardedHeaders);
            Assert.Equal(originalPolicy.TrustedProxies, sharedPolicy.TrustedProxies);
            Assert.Equal("original-certificate", sharedPolicy.CertificateReference);
            Assert.Equal(["192.0.2.0/24"], sharedPolicy.AllowedNetworks);
            Assert.Equal(10, sharedPolicy.MaximumConnections);
            Assert.Equal(120, sharedPolicy.RequestsPerMinutePerClient);
            Assert.Equal(originalPolicy.Key, Assert.Single(reloaded.VirtualDevices[1].Hosting!.Bindings).SecurityPolicyKey);
            Assert.Same(originalPolicy, Assert.Single(original.VirtualOutputSecurityPolicies));
            Assert.Equal("original-certificate", originalPolicy.CertificateReference);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies first-time listener policies keep default certificate and proxy behavior while applying editable fields.</summary>
    [Fact]
    public void BuildSecurityPolicy_NoExistingPolicy_KeepsDefaults()
    {
        // Arrange
        var device = CreateHostingConfiguration().VirtualDevices[0];

        // Act
        var policy = TuiAdapterSettingsEditor.BuildSecurityPolicy(device, null, "  ", " 192.0.2.0/24, ::1/128 ", 10, 120);

        // Assert
        Assert.Equal($"virtual-{device.Id.Value:N}-security", policy.Key);
        Assert.Equal($"{device.Name} listener security", policy.Name);
        Assert.Null(policy.CertificateReference);
        Assert.Equal(VirtualOutputClientCertificateMode.None, policy.ClientCertificateMode);
        Assert.False(policy.AllowForwardedHeaders);
        Assert.Empty(policy.TrustedProxies);
        Assert.Equal(["192.0.2.0/24", "::1/128"], policy.AllowedNetworks);
        Assert.Equal(10, policy.MaximumConnections);
        Assert.Equal(120, policy.RequestsPerMinutePerClient);
    }

    /// <summary>Verifies switching to HTTP cannot silently remove an existing client-certificate requirement.</summary>
    [Fact]
    public async Task BuildSecurityPolicy_HttpWithRequiredClientCertificate_RejectsSave()
    {
        // Arrange
        var original = CreateSecuredHostingConfiguration(VirtualOutputClientCertificateMode.Require, true);
        var settings = new LineupSettings { Streaming = original };
        var service = CreateSettingsService(settings);

        // Act
        var candidate = CreateSecurityPolicyEdit(original, VirtualOutputTransport.Http);
        var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, candidate);

        // Assert
        Assert.False(saved);
        Assert.Contains(StreamingConfigurationValidator.Validate(candidate), error => error.Contains("HTTP output binding cannot use client certificates", StringComparison.Ordinal));
        Assert.Same(original, settings.Streaming);
        Assert.Equal(VirtualOutputClientCertificateMode.Require, Assert.Single(original.VirtualOutputSecurityPolicies).ClientCertificateMode);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies invalid listener, security, and worker settings are rejected before persistence.</summary>
    [Theory]
    [InlineData("conflicting-bindings")]
    [InlineData("invalid-network")]
    [InlineData("missing-certificate")]
    [InlineData("invalid-worker")]
    public async Task TrySaveTopologyAsync_InvalidHosting_DoesNotPersist(string invalidSetting)
    {
        // Arrange
        var original = CreateHostingConfiguration();
        var candidate = invalidSetting switch
        {
            "conflicting-bindings" => CreateHostingConfiguration(8600),
            "invalid-network" => original with
            {
                VirtualOutputSecurityPolicies = [original.VirtualOutputSecurityPolicies[0] with { AllowedNetworks = ["invalid-network"] }]
            },
            "missing-certificate" => original with
            {
                VirtualDevices =
                [
                    original.VirtualDevices[0] with
                    {
                        Hosting = original.VirtualDevices[0].Hosting! with
                        {
                            Bindings = [original.VirtualDevices[0].Hosting!.Bindings[0] with { Transport = VirtualOutputTransport.Https }]
                        }
                    },
                    original.VirtualDevices[1]
                ]
            },
            _ => original with
            {
                VirtualOutputWorkerPolicies = [original.VirtualOutputWorkerPolicies[0] with { RestartWindowSeconds = 0 }]
            }
        };
        Assert.NotEmpty(StreamingConfigurationValidator.Validate(candidate));
        var settings = new LineupSettings { Streaming = original };
        var service = CreateSettingsService(settings);

        // Act
        var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, candidate);

        // Assert
        Assert.False(saved);
        Assert.Same(original, settings.Streaming);
        await service.DidNotReceive().UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies nonconflicting hosting changes are saved after complete topology validation.</summary>
    [Fact]
    public async Task TrySaveTopologyAsync_ValidHosting_PersistsConfiguration()
    {
        // Arrange
        var settings = new LineupSettings { Streaming = CreateHostingConfiguration() };
        var service = CreateSettingsService(settings);
        var candidate = CreateHostingConfiguration(8601);

        // Act
        var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, candidate);

        // Assert
        Assert.True(saved);
        Assert.Same(candidate, settings.Streaming);
        Assert.Empty(StreamingConfigurationValidator.Validate(settings.Streaming));
        await service.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies disabling a conflicting listener allows an existing invalid topology to be repaired.</summary>
    [Fact]
    public async Task TrySaveTopologyAsync_DisableConflictingHosting_PersistsRepair()
    {
        // Arrange
        var original = CreateHostingConfiguration(8600);
        var settings = new LineupSettings { Streaming = original };
        var service = CreateSettingsService(settings);
        var candidate = original with
        {
            VirtualDevices = [original.VirtualDevices[0], original.VirtualDevices[1] with { Hosting = null }]
        };

        // Act
        var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, candidate);

        // Assert
        Assert.True(saved);
        Assert.Null(settings.Streaming.VirtualDevices[1].Hosting);
        Assert.Empty(StreamingConfigurationValidator.Validate(settings.Streaming));
        Assert.NotNull(original.VirtualDevices[1].Hosting);
        await service.Received(1).UpdateAsync(Arg.Any<Action<LineupSettings>>());
    }

    /// <summary>Verifies rejected hosting conflicts preserve both persisted settings and their healthy backup.</summary>
    [Fact]
    public async Task TrySaveTopologyAsync_ConflictingHosting_PreservesFilesAndLiveSettings()
    {
        // Arrange
        var root = Directory.CreateTempSubdirectory("lineup-tui-hosting-");
        try
        {
            var settingsPath = Path.Combine(root.FullName, AppConstants.SettingsFileName);
            var backupPath = $"{settingsPath}.bak";
            var service = new LineupSettingsService(NullLogger<LineupSettingsService>.Instance, settingsPath, root.FullName);
            await service.UpdateAsync(settings => settings.Streaming = CreateHostingConfiguration());
            await service.UpdateAsync(settings => settings.TargetDays = 7);
            var original = service.Settings;
            var settingsContent = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
            var backupContent = await File.ReadAllTextAsync(backupPath, TestContext.Current.CancellationToken);

            // Act
            var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, CreateHostingConfiguration(8600));

            // Assert
            Assert.False(saved);
            Assert.Same(original, service.Settings);
            Assert.Equal(settingsContent, await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken));
            Assert.Equal(backupContent, await File.ReadAllTextAsync(backupPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    /// <summary>Verifies invalid persisted topology returns diagnostics instead of invoking activation that prevents reaching configuration.</summary>
    [Fact]
    public async Task ApplyTopologyIfValidAsync_ConflictingHosting_ReturnsErrorsWithoutActivation()
    {
        // Arrange
        var configuration = CreateHostingConfiguration(8600);
        var expectedErrors = StreamingConfigurationValidator.Validate(configuration);
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(configuration, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PhysicalLineupRefreshResult>>(new InvalidOperationException(string.Join(Environment.NewLine, expectedErrors))));
        var guides = Substitute.For<IGuideOrchestrator>();

        // Act
        var errors = await Program.ApplyTopologyIfValidAsync(lineups, guides, configuration, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedErrors, errors);
        Assert.Contains(errors, error => error.Contains("conflicting output bindings on port 8600", StringComparison.Ordinal));
        await lineups.DidNotReceive().RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
        await guides.DidNotReceive().ApplyAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies a corrected topology can activate successfully without restarting the TUI process.</summary>
    [Fact]
    public async Task ApplyTopologyIfValidAsync_RepairedHosting_ActivatesPhysicalDevicesBeforeGuides()
    {
        // Arrange
        var original = CreateHostingConfiguration(8600);
        var settings = new LineupSettings { Streaming = original };
        var service = CreateSettingsService(settings);
        var configuration = original with
        {
            VirtualDevices =
            [
                original.VirtualDevices[0],
                original.VirtualDevices[1] with
                {
                    Hosting = original.VirtualDevices[1].Hosting! with
                    {
                        Bindings = [original.VirtualDevices[1].Hosting!.Bindings[0] with { Port = 8601, PublicBaseUri = "http://localhost:8601/" }]
                    }
                }
            ]
        };
        var calls = new List<string>();
        var lineups = Substitute.For<IPhysicalLineupService>();
        lineups.RefreshAsync(configuration, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("physical");
            return Task.FromResult<IReadOnlyList<PhysicalLineupRefreshResult>>([]);
        });
        var guides = Substitute.For<IGuideOrchestrator>();
        guides.ApplyAsync(configuration, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls.Add("guides");
            return Task.CompletedTask;
        });

        // Act
        var originalErrors = await Program.ApplyTopologyIfValidAsync(lineups, guides, original, TestContext.Current.CancellationToken);
        var saved = await TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, configuration);
        var errors = await Program.ApplyTopologyIfValidAsync(lineups, guides, settings.Streaming, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotEmpty(originalErrors);
        Assert.True(saved);
        Assert.Empty(errors);
        Assert.Equal(["physical", "guides"], calls);
        await lineups.DidNotReceive().RefreshAsync(original, Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies valid topology activation failures are not hidden as configuration diagnostics.</summary>
    [Fact]
    public async Task ApplyTopologyIfValidAsync_RuntimeFailure_PropagatesException()
    {
        // Arrange
        var configuration = CreateHostingConfiguration(8601);
        var lineups = Substitute.For<IPhysicalLineupService>();
        var guides = Substitute.For<IGuideOrchestrator>();
        var failure = new InvalidOperationException("Runtime activation failed.");
        lineups.RefreshAsync(configuration, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<PhysicalLineupRefreshResult>>(failure));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Program.ApplyTopologyIfValidAsync(lineups, guides, configuration, TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(failure, exception);
        await guides.DidNotReceive().ApplyAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies caller cancellation is preserved even when topology would otherwise require recovery.</summary>
    [Fact]
    public async Task ApplyTopologyIfValidAsync_CallerCancellation_DoesNotActivate()
    {
        // Arrange
        var configuration = CreateHostingConfiguration(8600);
        var lineups = Substitute.For<IPhysicalLineupService>();
        var guides = Substitute.For<IGuideOrchestrator>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Program.ApplyTopologyIfValidAsync(lineups, guides, configuration, cancellation.Token));

        // Assert
        await lineups.DidNotReceive().RefreshAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
        await guides.DidNotReceive().ApplyAsync(Arg.Any<StreamingConfiguration>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Verifies persistence errors remain explicit failures rather than validation rejections.</summary>
    [Fact]
    public async Task TrySaveTopologyAsync_PersistenceFailure_PropagatesException()
    {
        // Arrange
        var original = CreateHostingConfiguration();
        var settings = new LineupSettings { Streaming = original };
        var service = CreateSettingsService(settings);
        var failure = new IOException("Settings persistence failed.");
        service.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(Task.FromException(failure));

        // Act
        var exception = await Assert.ThrowsAsync<IOException>(() => TuiAdapterSettingsEditor.TrySaveTopologyAsync(service, CreateHostingConfiguration(8601)));

        // Assert
        Assert.Same(failure, exception);
        Assert.Same(original, settings.Streaming);
    }

    /// <summary>Verifies the TUI derives a client-facing URL from the dedicated listener.</summary>
    [Theory]
    [InlineData(VirtualOutputTransport.Http, "192.0.2.10, ::1", 8600, "http://192.0.2.10:8600/")]
    [InlineData(VirtualOutputTransport.Https, "*", 8443, "https://localhost:8443/")]
    public void BuildAutomaticPublicBaseUri_ListenerSettings_ReturnsClientUrl(
        VirtualOutputTransport transport,
        string addresses,
        int port,
        string expected)
    {
        // Arrange

        // Act
        var result = TuiAdapterSettingsEditor.BuildAutomaticPublicBaseUri(transport, addresses, port);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Verifies dedicated Lineup Link hosting derives protocol security from its client-facing URI.</summary>
    [Theory]
    [InlineData("http://lineup.example:8600/", false)]
    [InlineData("https://lineup.example:8600/", true)]
    public void ApplyDedicatedHosting_LineupLink_DerivesProtocolTransport(string publicBaseUri, bool expectedEncryption)
    {
        // Arrange
        var adapter = new AdapterConfiguration(LineupLinkProtocol.AdapterKind, JsonSerializer.SerializeToElement(new LineupLinkVirtualConfiguration
        {
            PublicBaseUrl = "https://old.example/",
            RequireEncryption = !expectedEncryption,
            CertificateSha256 = new string('A', 64)
        }));

        // Act
        var updated = TuiAdapterSettingsEditor.ApplyDedicatedHosting(adapter, publicBaseUri);

        // Assert
        var configuration = updated.Configuration.Deserialize<LineupLinkVirtualConfiguration>();
        Assert.NotNull(configuration);
        Assert.Equal(publicBaseUri, configuration.PublicBaseUrl);
        Assert.Equal(expectedEncryption, configuration.RequireEncryption);
        Assert.Equal(expectedEncryption ? new string('A', 64) : null, configuration.CertificateSha256);
    }

    /// <summary>Verifies a controlling edit immediately makes its dependent portable setting visible.</summary>
    [Fact]
    public void ApplyEditToSnapshot_EnableNetworkAdvertisement_ShowsAdvertisedBaseUrl()
    {
        // Arrange
        var driver = new HdHomeRunVirtualTunerDriver();
        var configuration = driver.CreateDefaultConfiguration(VirtualDeviceId.New());
        var current = driver.GetSettings(configuration).Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var advertisedBaseUrl = driver.SettingsDescriptor.Fields.Single(field => field.Key == "advertisedBaseUrl");
        var edit = new AdapterSettingEdit
        {
            Key = "enableNetworkAdvertisement",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement(true)
        };
        Assert.False(TuiAdapterSettingsEditor.IsVisible(advertisedBaseUrl, current));

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.True(TuiAdapterSettingsEditor.IsVisible(advertisedBaseUrl, current));
    }

    /// <summary>Verifies setting a controlling boolean to false immediately hides its dependent setting.</summary>
    [Fact]
    public void ApplyEditToSnapshot_DisableControllingBoolean_HidesDependentField()
    {
        // Arrange
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal)
        {
            ["enabled"] = Snapshot("enabled", true)
        };
        var dependent = ConditionalField("enabled", "true");
        var edit = new AdapterSettingEdit
        {
            Key = "enabled",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement(false)
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.False(TuiAdapterSettingsEditor.IsVisible(dependent, current));
    }

    /// <summary>Verifies clearing a controlling setting immediately hides its dependent setting.</summary>
    [Fact]
    public void ApplyEditToSnapshot_ClearControllingValue_HidesDependentField()
    {
        // Arrange
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal)
        {
            ["transportSecurity"] = Snapshot("transportSecurity", "Https")
        };
        var dependent = ConditionalField("transportSecurity", "Https");
        var edit = new AdapterSettingEdit
        {
            Key = "transportSecurity",
            Action = AdapterSettingEditAction.Clear
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.False(TuiAdapterSettingsEditor.IsVisible(dependent, current));
        Assert.False(current["transportSecurity"].HasValue);
    }

    /// <summary>Verifies string-valued controlling edits use the same case-insensitive comparison as persisted snapshots.</summary>
    [Fact]
    public void ApplyEditToSnapshot_SetStringValue_ShowsMatchingDependentField()
    {
        // Arrange
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal);
        var dependent = ConditionalField("transportSecurity", "Https");
        var edit = new AdapterSettingEdit
        {
            Key = "transportSecurity",
            Action = AdapterSettingEditAction.Set,
            Value = JsonSerializer.SerializeToElement("https")
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.True(TuiAdapterSettingsEditor.IsVisible(dependent, current));
    }

    /// <summary>Verifies preserving a controlling setting leaves its visibility state unchanged.</summary>
    [Fact]
    public void ApplyEditToSnapshot_PreserveControllingValue_KeepsDependentFieldVisible()
    {
        // Arrange
        var original = Snapshot("enabled", true);
        var current = new Dictionary<string, AdapterSettingSnapshot>(StringComparer.Ordinal)
        {
            ["enabled"] = original
        };
        var dependent = ConditionalField("enabled", "true");
        var edit = new AdapterSettingEdit
        {
            Key = "enabled",
            Action = AdapterSettingEditAction.Preserve
        };

        // Act
        TuiAdapterSettingsEditor.ApplyEditToSnapshot(current, edit);

        // Assert
        Assert.Same(original, current["enabled"]);
        Assert.True(TuiAdapterSettingsEditor.IsVisible(dependent, current));
    }

    /// <summary>Verifies physical-device removal cleans virtual and guide-source subscriptions.</summary>
    [Fact]
    public void RemovePhysicalDevice_SelectedSubscriptions_RemovesReferences()
    {
        // Arrange
        var physicalId = PhysicalDeviceId.New();
        var configuration = CreateConfiguration(physicalId, out _, out _);

        // Act
        var updated = TuiAdapterSettingsEditor.RemovePhysicalDevice(configuration, physicalId);

        // Assert
        Assert.DoesNotContain(updated.PhysicalDevices, device => device.Id == physicalId);
        Assert.DoesNotContain(physicalId, Assert.Single(updated.VirtualDevices).Subscription.SelectedDeviceIds);
        Assert.DoesNotContain(physicalId, Assert.Single(updated.GuideSources).Subscription.SelectedDeviceIds);
        Assert.Empty(StreamingConfigurationValidator.Validate(updated));
    }

    /// <summary>Verifies virtual-device removal cleans Guide Output selections.</summary>
    [Fact]
    public void RemoveVirtualDevice_GuideOutputSelection_RemovesReference()
    {
        // Arrange
        var configuration = CreateConfiguration(PhysicalDeviceId.New(), out var virtualId, out _);

        // Act
        var updated = TuiAdapterSettingsEditor.RemoveVirtualDevice(configuration, virtualId);

        // Assert
        Assert.DoesNotContain(updated.VirtualDevices, device => device.Id == virtualId);
        Assert.DoesNotContain(virtualId, Assert.Single(updated.GuideOutputs).SelectedVirtualDeviceIds);
        Assert.Empty(StreamingConfigurationValidator.Validate(updated));
    }

    /// <summary>Verifies guide-source removal cleans Guide Output selections.</summary>
    [Fact]
    public void RemoveGuideSource_GuideOutputSelection_RemovesReference()
    {
        // Arrange
        var configuration = CreateConfiguration(PhysicalDeviceId.New(), out _, out var guideSourceId);

        // Act
        var updated = TuiAdapterSettingsEditor.RemoveGuideSource(configuration, guideSourceId);

        // Assert
        Assert.DoesNotContain(updated.GuideSources, source => source.Id == guideSourceId);
        Assert.DoesNotContain(guideSourceId, Assert.Single(updated.GuideOutputs).SelectedGuideSourceIds);
        Assert.Empty(StreamingConfigurationValidator.Validate(updated));
    }

    /// <summary>Verifies custom outputs can be removed while built-in outputs remain protected.</summary>
    [Fact]
    public void RemoveGuideOutput_BuiltInAndCustom_RemovesOnlyCustomOutput()
    {
        // Arrange
        var customId = GuideOutputId.New();
        var builtInId = GuideOutputId.New();
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                new GuideOutputDefinition
                {
                    Id = customId,
                    Name = "Custom",
                    Key = "custom",
                    Adapter = Adapter("xmltv")
                },
                new GuideOutputDefinition
                {
                    Id = builtInId,
                    Name = "Global",
                    Key = "global",
                    IsBuiltIn = true,
                    Adapter = Adapter("xmltv")
                }
            ]
        };

        // Act
        var customRemoved = TuiAdapterSettingsEditor.RemoveGuideOutput(configuration, customId);
        var builtInProtected = TuiAdapterSettingsEditor.RemoveGuideOutput(customRemoved, builtInId);

        // Assert
        var remaining = Assert.Single(builtInProtected.GuideOutputs);
        Assert.Equal(builtInId, remaining.Id);
        Assert.True(remaining.IsBuiltIn);
    }

    private static StreamingConfiguration CreateSecuredHostingConfiguration(VirtualOutputClientCertificateMode certificateMode, bool allowForwardedHeaders)
    {
        var configuration = CreateHostingConfiguration(8602);
        return configuration with
        {
            VirtualDevices = configuration.VirtualDevices.Select(device => device with
            {
                Hosting = device.Hosting! with
                {
                    Bindings = device.Hosting!.Bindings.Select(binding => binding with
                    {
                        Transport = VirtualOutputTransport.Https,
                        PublicBaseUri = $"https://localhost:{binding.Port}/"
                    }).ToArray()
                }
            }).ToArray(),
            VirtualOutputSecurityPolicies =
            [
                configuration.VirtualOutputSecurityPolicies[0] with
                {
                    CertificateReference = "original-certificate",
                    ClientCertificateMode = certificateMode,
                    AllowedNetworks = ["192.0.2.0/24"],
                    MaximumConnections = 10,
                    RequestsPerMinutePerClient = 120,
                    AllowForwardedHeaders = allowForwardedHeaders,
                    TrustedProxies = allowForwardedHeaders ? ["127.0.0.1", "192.0.2.0/24", "::1/128"] : []
                }
            ]
        };
    }

    private static StreamingConfiguration CreateSecurityPolicyEdit(StreamingConfiguration original, VirtualOutputTransport transport = VirtualOutputTransport.Https)
    {
        var device = original.VirtualDevices[0];
        var binding = Assert.Single(device.Hosting!.Bindings);
        var currentPolicy = original.VirtualOutputSecurityPolicies.Single(policy => policy.Key == binding.SecurityPolicyKey);
        var policy = TuiAdapterSettingsEditor.BuildSecurityPolicy(device, currentPolicy, " new-certificate ", " 198.51.100.0/24, 2001:db8::/32 ", 30, 240);
        return original with
        {
            VirtualDevices =
            [
                device with
                {
                    Hosting = device.Hosting with
                    {
                        Bindings =
                        [
                            binding with
                            {
                                Transport = transport,
                                Port = 8601,
                                PublicBaseUri = $"{(transport == VirtualOutputTransport.Https ? "https" : "http")}://localhost:8601/",
                                SecurityPolicyKey = policy.Key
                            }
                        ]
                    }
                },
                original.VirtualDevices[1]
            ],
            VirtualOutputSecurityPolicies = [.. original.VirtualOutputSecurityPolicies, policy]
        };
    }

    private static ILineupSettingsService CreateSettingsService(LineupSettings settings)
    {
        var service = Substitute.For<ILineupSettingsService>();
        service.Settings.Returns(settings);
        service.UpdateAsync(Arg.Any<Action<LineupSettings>>()).Returns(call =>
        {
            call.Arg<Action<LineupSettings>>()(settings);
            return Task.CompletedTask;
        });
        return service;
    }

    private static StreamingConfiguration CreateHostingConfiguration(int? secondPort = null) => new()
    {
        VirtualDevices = Enumerable.Range(0, 2).Select(index => new VirtualDeviceDefinition
        {
            Id = VirtualDeviceId.New(),
            Name = $"Output {index + 1}",
            Adapter = Adapter("hdhomerun"),
            Hosting = (index == 0 ? 8600 : secondPort) is int port
                ? new VirtualOutputHostingDefinition
                {
                    WorkerPolicyKey = "worker",
                    Bindings =
                    [
                        new VirtualOutputBindingDefinition
                        {
                            Addresses = ["localhost"],
                            Port = port,
                            PublicBaseUri = $"http://localhost:{port}/",
                            SecurityPolicyKey = "security"
                        }
                    ]
                }
                : null
        }).ToArray(),
        VirtualOutputSecurityPolicies = [new VirtualOutputSecurityPolicyDefinition { Key = "security", Name = "Security" }],
        VirtualOutputWorkerPolicies = [new VirtualOutputWorkerPolicyDefinition { Key = "worker", Name = "Worker" }]
    };

    private static StreamingConfiguration CreateConfiguration(
        PhysicalDeviceId physicalId,
        out VirtualDeviceId virtualId,
        out GuideSourceId guideSourceId)
    {
        virtualId = VirtualDeviceId.New();
        guideSourceId = GuideSourceId.New();
        return new StreamingConfiguration
        {
            PhysicalDevices =
            [
                new PhysicalDeviceDefinition
                {
                    Id = physicalId,
                    Name = "Physical",
                    Adapter = Adapter("physical")
                }
            ],
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = virtualId,
                    Name = "Virtual",
                    Adapter = Adapter("virtual"),
                    Subscription = new PhysicalDeviceSubscription
                    {
                        Mode = PhysicalDeviceSubscriptionMode.Selected,
                        SelectedDeviceIds = [physicalId]
                    }
                }
            ],
            GuideSources =
            [
                new GuideSourceDefinition
                {
                    Id = guideSourceId,
                    Name = "Guide",
                    Adapter = Adapter("guide"),
                    Subscription = new PhysicalDeviceSubscription
                    {
                        Mode = PhysicalDeviceSubscriptionMode.Selected,
                        SelectedDeviceIds = [physicalId]
                    }
                }
            ],
            GuideOutputs =
            [
                new GuideOutputDefinition
                {
                    Id = GuideOutputId.New(),
                    Name = "Output",
                    Key = "output",
                    Adapter = Adapter("xmltv"),
                    VirtualDeviceSelectionMode = GuideOutputSelectionMode.Selected,
                    SelectedVirtualDeviceIds = [virtualId],
                    GuideSourceSelectionMode = GuideOutputSelectionMode.Selected,
                    SelectedGuideSourceIds = [guideSourceId]
                }
            ]
        };
    }

    private static AdapterConfiguration Adapter(string kind) =>
        new(kind, JsonSerializer.SerializeToElement(new { }));

    private static AdapterSettingDescriptor ConditionalField(string fieldKey, string value) =>
        new()
        {
            Key = "dependent",
            Label = "Dependent",
            VisibleWhen =
            [
                new AdapterSettingCondition
                {
                    FieldKey = fieldKey,
                    Value = value
                }
            ]
        };

    private static AdapterSettingSnapshot Snapshot<T>(string key, T value) =>
        new()
        {
            Key = key,
            Value = JsonSerializer.SerializeToElement(value),
            HasValue = true
        };
}
