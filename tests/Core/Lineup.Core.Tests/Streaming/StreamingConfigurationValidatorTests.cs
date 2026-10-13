using System.Text.Json;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Xunit;

namespace Lineup.Core.Tests.Streaming;

/// <summary>
/// Verifies streaming configuration invariants.
/// </summary>
public sealed class StreamingConfigurationValidatorTests
{
    /// <summary>
    /// Verifies selected guide subscriptions cannot reference unknown physical devices.
    /// </summary>
    [Fact]
    public void Validate_GuideSourceSelectsUnknownPhysicalDevice_ReturnsError()
    {
        // Arrange
        var unknownId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            GuideSources =
            [
                new GuideSourceDefinition
                {
                    Id = GuideSourceId.New(),
                    Name = "Guide",
                    Adapter = EmptyAdapter("silicondust"),
                    Subscription = new PhysicalDeviceSubscription
                    {
                        Mode = PhysicalDeviceSubscriptionMode.Selected,
                        SelectedDeviceIds = [unknownId]
                    }
                }
            ]
        };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains(unknownId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies selected subscriptions cannot silently reference removed physical devices.
    /// </summary>
    [Fact]
    public void Validate_SelectedUnknownPhysicalDevice_ReturnsError()
    {
        // Arrange
        var unknownId = PhysicalDeviceId.New();
        var configuration = new StreamingConfiguration
        {
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = VirtualDeviceId.New(),
                    Name = "Web Player",
                    Adapter = EmptyAdapter("web-player"),
                    Subscription = new PhysicalDeviceSubscription
                    {
                        Mode = PhysicalDeviceSubscriptionMode.Selected,
                        SelectedDeviceIds = new HashSet<PhysicalDeviceId> { unknownId }
                    }
                }
            ]
        };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains(unknownId.ToString(), StringComparison.Ordinal));
    }

    /// <summary>Verifies keep-warm duration stays within the supported resource-retention boundary.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void Validate_InvalidKeepWarmPeriod_ReturnsError(int keepWarmPeriodSeconds)
    {
        // Arrange
        var configuration = new StreamingConfiguration { KeepWarmPeriodSeconds = keepWarmPeriodSeconds };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains("Keep-warm period", StringComparison.Ordinal));
    }

    /// <summary>Verifies guide-output keys are code-safe and unique.</summary>
    [Fact]
    public void Validate_InvalidAndDuplicateGuideOutputKeys_ReturnsErrors()
    {
        // Arrange
        var configuration = new StreamingConfiguration
        {
            GuideOutputs =
            [
                new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "First", Key = "xmltv_2", Adapter = EmptyAdapter("xmltv") },
                new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "Second", Key = "XMLTV 2", Adapter = EmptyAdapter("xmltv") },
                new GuideOutputDefinition { Id = GuideOutputId.New(), Name = "Third", Key = "xmltv_2", Adapter = EmptyAdapter("xmltv") }
            ]
        };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains("must start with a lowercase letter", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("key 'xmltv_2' is duplicated", StringComparison.Ordinal));
    }

    /// <summary>Verifies display names produce deterministic URL-safe output keys.</summary>
    [Theory]
    [InlineData("XMLTV 2", "xmltv_2")]
    [InlineData("Café & News", "cafe_news")]
    [InlineData("123 Sports", "output_123_sports")]
    public void CreateGuideOutputKey_DisplayName_ReturnsCodeSafeKey(string name, string expected)
    {
        // Act
        var key = GuideOutputKey.Create(name);

        // Assert
        Assert.Equal(expected, key);
        Assert.True(GuideOutputKey.IsValid(key));
    }

    /// <summary>Verifies an isolated HTTPS binding requires known worker and security policies with certificate material.</summary>
    [Fact]
    public void Validate_HttpsBindingWithUnknownPolicies_ReturnsErrors()
    {
        // Arrange
        var configuration = new StreamingConfiguration
        {
            VirtualDevices =
            [
                new VirtualDeviceDefinition
                {
                    Id = VirtualDeviceId.New(),
                    Name = "Lineup Link",
                    Adapter = EmptyAdapter("lineup-link"),
                    Hosting = new VirtualOutputHostingDefinition
                    {
                        WorkerPolicyKey = "missing-worker",
                        Bindings =
                        [
                            new VirtualOutputBindingDefinition
                            {
                                Transport = VirtualOutputTransport.Https,
                                Addresses = ["0.0.0.0"],
                                Port = 9443,
                                PublicBaseUri = "https://lineup.example:9443",
                                SecurityPolicyKey = "missing-security"
                            }
                        ]
                    }
                }
            ]
        };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains("unknown worker policy", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown security policy", StringComparison.Ordinal));
    }

    /// <summary>Verifies an HTTPS binding cannot activate without a certificate reference.</summary>
    [Fact]
    public void Validate_HttpsBindingWithoutCertificate_ReturnsError()
    {
        // Arrange
        var configuration = CreateHostedConfiguration(
            new VirtualOutputBindingDefinition
            {
                Transport = VirtualOutputTransport.Https,
                Addresses = ["192.0.2.10"],
                Port = 9443,
                PublicBaseUri = "https://lineup.example:9443",
                SecurityPolicyKey = "private-network"
            },
            certificateReference: null);

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains("requires a certificate reference", StringComparison.Ordinal));
    }

    /// <summary>Verifies wildcard and interface-specific bindings cannot claim the same port for separate outputs.</summary>
    [Fact]
    public void Validate_OverlappingOutputBindings_ReturnsConflict()
    {
        // Arrange
        var first = CreateHostedDevice(
            new VirtualOutputBindingDefinition
            {
                Addresses = ["0.0.0.0"],
                Port = 9080,
                PublicBaseUri = "http://lineup.example:9080",
                SecurityPolicyKey = "private-network"
            });
        var second = CreateHostedDevice(
            new VirtualOutputBindingDefinition
            {
                Addresses = ["192.0.2.20"],
                Port = 9080,
                PublicBaseUri = "http://lineup-two.example:9080",
                SecurityPolicyKey = "private-network"
            });
        var configuration = CreateHostedConfiguration(first, second);

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains("conflicting output bindings", StringComparison.Ordinal));
    }

    /// <summary>Verifies valid dedicated bindings and policies pass provider-neutral validation.</summary>
    [Fact]
    public void Validate_DedicatedOutputBindings_ReturnsNoErrors()
    {
        // Arrange
        var configuration = CreateHostedConfiguration(
            CreateHostedDevice(
                new VirtualOutputBindingDefinition
                {
                    Addresses = ["192.0.2.10"],
                    Port = 9080,
                    PublicBaseUri = "http://lineup-one.example:9080",
                    SecurityPolicyKey = "private-network"
                }),
            CreateHostedDevice(
                new VirtualOutputBindingDefinition
                {
                    Addresses = ["192.0.2.20"],
                    Port = 9080,
                    PublicBaseUri = "http://lineup-two.example:9080",
                    SecurityPolicyKey = "private-network"
                }));

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Empty(errors);
    }

    /// <summary>Verifies dedicated listener proxy trust requires enabled forwarding and valid explicit addresses.</summary>
    [Theory]
    [InlineData(false, "10.0.0.2", "cannot configure trusted proxies")]
    [InlineData(true, "", "at least one trusted proxy")]
    [InlineData(true, "proxy.internal", "invalid trusted proxy")]
    public void Validate_InvalidTrustedProxyPolicy_ReturnsError(bool allowForwardedHeaders, string trustedProxy, string expected)
    {
        // Arrange
        var configuration = new StreamingConfiguration
        {
            VirtualOutputSecurityPolicies =
            [
                new VirtualOutputSecurityPolicyDefinition
                {
                    Key = "public",
                    Name = "Public",
                    AllowForwardedHeaders = allowForwardedHeaders,
                    TrustedProxies = string.IsNullOrEmpty(trustedProxy) ? [] : [trustedProxy]
                }
            ]
        };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    /// <summary>Verifies every dedicated listener allowed-network value must use valid CIDR notation.</summary>
    [Theory]
    [InlineData("192.0.2.0/33")]
    [InlineData("192.0.2.10")]
    [InlineData("192.0.2.0/24, invalid")]
    public void Validate_InvalidAllowedNetwork_ReturnsError(string allowedNetwork)
    {
        // Arrange
        var configuration = new StreamingConfiguration
        {
            VirtualOutputSecurityPolicies =
            [
                new VirtualOutputSecurityPolicyDefinition
                {
                    Key = "public",
                    Name = "Public",
                    AllowedNetworks = [allowedNetwork]
                }
            ]
        };

        // Act
        var errors = StreamingConfigurationValidator.Validate(configuration);

        // Assert
        Assert.Contains(errors, error => error.Contains("invalid network", StringComparison.Ordinal));
    }

    private static AdapterConfiguration EmptyAdapter(string kind)
    {
        using var document = JsonDocument.Parse("{}");
        return new AdapterConfiguration(kind, document.RootElement.Clone());
    }

    private static StreamingConfiguration CreateHostedConfiguration(VirtualOutputBindingDefinition binding, string? certificateReference) =>
        CreateHostedConfiguration([CreateHostedDevice(binding)], certificateReference);

    private static StreamingConfiguration CreateHostedConfiguration(params VirtualDeviceDefinition[] devices) =>
        CreateHostedConfiguration(devices, certificateReference: null);

    private static StreamingConfiguration CreateHostedConfiguration(IReadOnlyList<VirtualDeviceDefinition> devices, string? certificateReference)
    {
        return new StreamingConfiguration
        {
            VirtualDevices = devices,
            VirtualOutputSecurityPolicies =
            [
                new VirtualOutputSecurityPolicyDefinition
                {
                    Key = "private-network",
                    Name = "Private network",
                    CertificateReference = certificateReference
                }
            ],
            VirtualOutputWorkerPolicies =
            [
                new VirtualOutputWorkerPolicyDefinition
                {
                    Key = "default-worker",
                    Name = "Default worker"
                }
            ]
        };
    }

    private static VirtualDeviceDefinition CreateHostedDevice(VirtualOutputBindingDefinition binding) =>
        new()
        {
            Id = VirtualDeviceId.New(),
            Name = "Output",
            Adapter = EmptyAdapter("lineup-link"),
            Hosting = new VirtualOutputHostingDefinition
            {
                WorkerPolicyKey = "default-worker",
                Bindings = [binding]
            }
        };
}
