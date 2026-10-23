using System.Text.Json;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.LineupLink.Physical;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Lineup.GuideOutputs.Xmltv.Plugin;

namespace Lineup.Web.Services;

/// <summary>
/// Converts editable Web topology forms to provider-neutral Core configuration.
/// </summary>
public sealed class TopologySettingsEditor
{
    internal const string PhysicalHdHomeRunKind = "hdhomerun";
    internal const string LineupLinkKind = LineupLinkProtocol.AdapterKind;
    private const string VirtualHdHomeRunKind = "hdhomerun";
    private const string WebPlayerKind = WebPlayerVirtualTunerDriver.AdapterKind;

    /// <summary>Creates the default persisted topology containing the built-in Web Player.</summary>
    public static StreamingConfiguration CreateDefault() => new()
    {
        VirtualDevices = [WebPlayerVirtualTunerDriver.CreateBuiltIn()],
        GuideOutputs =
        [
            XmltvGuideOutputDriver.CreateBuiltIn(
                Path.Combine(AppConstants.DefaultGuideOutputDirectoryPath, AppConstants.DefaultXmltvFileName))
        ]
    };

    /// <summary>Ensures the built-in Web Player exists in a persisted topology.</summary>
    public static StreamingConfiguration EnsureBuiltInWebPlayer(StreamingConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return WebPlayerVirtualTunerDriver.EnsureBuiltIn(configuration);
    }

    /// <summary>Creates mutable forms from a Core topology.</summary>
    public TopologySettingsModel Edit(
        StreamingConfiguration configuration,
        string xmltvOutputPath = "xmltv.xml",
        IEnumerable<IGuideOutputDriver>? guideOutputDrivers = null)
    {
        configuration = EnsureBuiltInWebPlayer(configuration);
        configuration = new XmltvGuideOutputDriver().Normalize(
            configuration,
            new StreamingConfigurationInitializationContext { DefaultXmltvOutputPath = xmltvOutputPath });
        var model = new TopologySettingsModel
        {
            KeepWarmPeriodSeconds = configuration.KeepWarmPeriodSeconds,
            PhysicalDefaults = new PhysicalTranscodeSettingsModel
            {
                ConvertHevcToH264 = configuration.PhysicalDefaults.ConvertHevcToH264,
                Deinterlace = configuration.PhysicalDefaults.Deinterlace,
                Audio = configuration.PhysicalDefaults.Audio,
                Ac4Audio = configuration.PhysicalDefaults.Ac4Audio,
                VideoPreset = configuration.PhysicalDefaults.VideoPreset,
                VideoQuality = configuration.PhysicalDefaults.VideoQuality,
                MaximumVideoBitRateMbps = configuration.PhysicalDefaults.MaximumVideoBitRateMbps
            },
            VirtualDefaults = new VirtualTranscodeSettingsModel
            {
                ConvertHevcToH264 = configuration.VirtualDefaults.ConvertHevcToH264,
                Deinterlace = configuration.VirtualDefaults.Deinterlace,
                Audio = configuration.VirtualDefaults.Audio,
                Ac4Audio = configuration.VirtualDefaults.Ac4Audio,
                VideoPreset = configuration.VirtualDefaults.VideoPreset,
                VideoQuality = configuration.VirtualDefaults.VideoQuality,
                MaximumVideoBitRateMbps = configuration.VirtualDefaults.MaximumVideoBitRateMbps
            },
            PreservedVirtualOutputSecurityPolicies = configuration.VirtualOutputSecurityPolicies.ToList(),
            PreservedVirtualOutputWorkerPolicies = configuration.VirtualOutputWorkerPolicies.ToList(),
            PhysicalDevices = configuration.PhysicalDevices.Select(device => new PhysicalDeviceSettingsModel
            {
                Id = device.Id,
                Name = device.Name,
                Enabled = device.Enabled,
                Kind = device.Adapter.Kind,
                AdapterConfiguration = device.Adapter.Configuration.Clone(),
                Address = ReadString(device.Adapter.Configuration, "Address"),
                LinkBaseUrl = ReadString(device.Adapter.Configuration, "BaseUrl"),
                LinkVirtualDeviceId = ReadGuid(device.Adapter.Configuration, "VirtualDeviceId"),
                LinkAccessToken = ReadString(device.Adapter.Configuration, "AccessToken"),
                LinkTransportSecurity = ReadEnum(device.Adapter.Configuration, "TransportSecurity", LineupLinkTransportSecurity.Https),
                LinkCertificateSha256 = ReadString(device.Adapter.Configuration, "CertificateSha256"),
                OverrideConvertHevcToH264 = device.TranscodeOverrides.ConvertHevcToH264.HasValue,
                ConvertHevcToH264 = device.TranscodeOverrides.ConvertHevcToH264 ?? configuration.PhysicalDefaults.ConvertHevcToH264,
                OverrideDeinterlace = device.TranscodeOverrides.Deinterlace.HasValue,
                Deinterlace = device.TranscodeOverrides.Deinterlace ?? configuration.PhysicalDefaults.Deinterlace,
                OverrideAudio = device.TranscodeOverrides.Audio.HasValue,
                Audio = device.TranscodeOverrides.Audio ?? configuration.PhysicalDefaults.Audio,
                OverrideAc4Audio = device.TranscodeOverrides.Ac4Audio.HasValue,
                Ac4Audio = device.TranscodeOverrides.Ac4Audio ?? configuration.PhysicalDefaults.Ac4Audio,
                OverrideVideoPreset = device.TranscodeOverrides.VideoPreset.HasValue,
                VideoPreset = device.TranscodeOverrides.VideoPreset ?? configuration.PhysicalDefaults.VideoPreset,
                OverrideVideoQuality = device.TranscodeOverrides.VideoQuality.HasValue,
                VideoQuality = device.TranscodeOverrides.VideoQuality ?? configuration.PhysicalDefaults.VideoQuality,
                OverrideMaximumVideoBitRate = device.TranscodeOverrides.MaximumVideoBitRateMbps.HasValue,
                MaximumVideoBitRateMbps = device.TranscodeOverrides.MaximumVideoBitRateMbps ?? configuration.PhysicalDefaults.MaximumVideoBitRateMbps
            }).ToList(),
            GuideSources = configuration.GuideSources.Select(source => new GuideSourceSettingsModel
            {
                Id = source.Id,
                Name = source.Name,
                Enabled = source.Enabled,
                Kind = source.Adapter.Kind,
                AdapterConfiguration = source.Adapter.Configuration.Clone(),
                SubscriptionMode = source.Subscription.Mode,
                SelectedDeviceIds = source.Subscription.SelectedDeviceIds.ToHashSet()
            }).ToList(),
            GuideOutputs = configuration.GuideOutputs.Select(output => new GuideOutputSettingsModel
            {
                Id = output.Id,
                Name = output.Name,
                Key = output.Key,
                Enabled = output.Enabled,
                IsBuiltIn = output.IsBuiltIn,
                Kind = output.Adapter.Kind,
                AdapterConfiguration = output.Adapter.Configuration.Clone(),
                OutputPath = ReadString(output.Adapter.Configuration, "OutputPath"),
                VirtualDeviceSelectionMode = output.VirtualDeviceSelectionMode,
                SelectedVirtualDeviceIds = output.SelectedVirtualDeviceIds.ToHashSet(),
                GuideSourceSelectionMode = output.GuideSourceSelectionMode,
                SelectedGuideSourceIds = output.SelectedGuideSourceIds.ToHashSet()
            }).ToList(),
            VirtualDevices = configuration.VirtualDevices.Select((device, index) =>
            {
                var binding = device.Hosting?.Bindings.FirstOrDefault();
                var securityPolicy = binding is null
                    ? null
                    : configuration.VirtualOutputSecurityPolicies.FirstOrDefault(policy =>
                        string.Equals(policy.Key, binding.SecurityPolicyKey, StringComparison.OrdinalIgnoreCase));
                var workerPolicy = device.Hosting is null
                    ? null
                    : configuration.VirtualOutputWorkerPolicies.FirstOrDefault(policy =>
                        string.Equals(policy.Key, device.Hosting.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase));
                var listenerPort = binding?.Port ?? FindAvailableOutputPort(configuration, index);
                var publicBaseUri = binding?.PublicBaseUri;
                if (string.IsNullOrWhiteSpace(publicBaseUri))
                {
                    publicBaseUri = string.Equals(device.Adapter.Kind, LineupLinkKind, StringComparison.OrdinalIgnoreCase)
                        ? ReadString(device.Adapter.Configuration, "PublicBaseUrl")
                        : ReadString(device.Adapter.Configuration, "AdvertisedBaseUrl");
                }
                if (string.IsNullOrWhiteSpace(publicBaseUri))
                {
                    publicBaseUri = $"http://localhost:{listenerPort}/";
                }
                var settings = new VirtualDeviceSettingsModel
                {
                    Id = device.Id,
                    Name = device.Name,
                    Enabled = device.Enabled,
                    Kind = device.Adapter.Kind,
                    AdapterConfiguration = device.Adapter.Configuration.Clone(),
                    TunerCountCap = ReadNullableInt(device.Adapter.Configuration, "TunerCountCap"),
                    ExternalDeviceId = ReadExternalDeviceId(device),
                    EnableNetworkAdvertisement = ReadBool(device.Adapter.Configuration, "EnableNetworkAdvertisement", defaultValue: true),
                    AdvertisedBaseUrl = ReadString(device.Adapter.Configuration, "AdvertisedBaseUrl"),
                    LinkInstanceId = ReadGuid(device.Adapter.Configuration, "InstanceId") ?? Guid.NewGuid(),
                    LinkPublicBaseUrl = ReadString(device.Adapter.Configuration, "PublicBaseUrl"),
                    LinkRequireEncryption = ReadBool(device.Adapter.Configuration, "RequireEncryption", defaultValue: true),
                    LinkAccessGrants = ReadLineupLinkAccessGrants(device.Adapter.Configuration),
                    LinkCertificateSha256 = ReadString(device.Adapter.Configuration, "CertificateSha256"),
                    UseDedicatedListener = device.Hosting is not null,
                    ExecutionMode = device.Hosting?.ExecutionMode ?? VirtualOutputExecutionMode.ManagedProcess,
                    ListenerTransport = binding?.Transport ?? VirtualOutputTransport.Http,
                    ListenerAddresses = binding is null ? "localhost" : string.Join(", ", binding.Addresses),
                    ListenerPort = listenerPort,
                    PublicBaseUri = publicBaseUri,
                    PublishedPort = ResolvePublishedPort(binding),
                    AdditionalBindings = device.Hosting?.Bindings.Skip(1).ToList() ?? [],
                    CertificateReference = securityPolicy?.CertificateReference ?? string.Empty,
                    ClientCertificateMode = securityPolicy?.ClientCertificateMode ?? VirtualOutputClientCertificateMode.None,
                    AllowedNetworks = securityPolicy is null ? string.Empty : string.Join(", ", securityPolicy.AllowedNetworks),
                    MaximumConnections = securityPolicy?.MaximumConnections ?? 0,
                    RequestsPerMinutePerClient = securityPolicy?.RequestsPerMinutePerClient ?? 0,
                    AllowForwardedHeaders = securityPolicy?.AllowForwardedHeaders ?? false,
                    TrustedProxies = securityPolicy is null ? string.Empty : string.Join(", ", securityPolicy.TrustedProxies),
                    MaximumRestarts = workerPolicy?.MaximumRestarts ?? 5,
                    RestartWindowSeconds = workerPolicy?.RestartWindowSeconds ?? 300,
                    DrainTimeoutSeconds = workerPolicy?.DrainTimeoutSeconds ?? 15,
                    LeaseGracePeriodSeconds = workerPolicy?.LeaseGracePeriodSeconds ?? 15,
                    MaximumOutstandingBrokerCalls = workerPolicy?.MaximumOutstandingBrokerCalls ?? 32,
                    MaximumMemoryMib = workerPolicy?.MaximumMemoryMib ?? 0,
                    MaximumCpuPercent = workerPolicy?.MaximumCpuPercent ?? 0,
                    SubscriptionMode = device.Subscription.Mode,
                    SelectedDeviceIds = device.Subscription.SelectedDeviceIds.ToHashSet(),
                    OverrideConvertHevcToH264 = device.TranscodeOverrides.ConvertHevcToH264.HasValue,
                    ConvertHevcToH264 = device.TranscodeOverrides.ConvertHevcToH264 ?? configuration.VirtualDefaults.ConvertHevcToH264,
                    OverrideDeinterlace = device.TranscodeOverrides.Deinterlace.HasValue,
                    Deinterlace = device.TranscodeOverrides.Deinterlace ?? configuration.VirtualDefaults.Deinterlace,
                    OverrideAudio = device.TranscodeOverrides.Audio.HasValue,
                    Audio = device.TranscodeOverrides.Audio ?? configuration.VirtualDefaults.Audio,
                    OverrideAc4Audio = device.TranscodeOverrides.Ac4Audio.HasValue,
                    Ac4Audio = device.TranscodeOverrides.Ac4Audio ?? configuration.VirtualDefaults.Ac4Audio,
                    OverrideVideoPreset = device.TranscodeOverrides.VideoPreset.HasValue,
                    VideoPreset = device.TranscodeOverrides.VideoPreset ?? configuration.VirtualDefaults.VideoPreset,
                    OverrideVideoQuality = device.TranscodeOverrides.VideoQuality.HasValue,
                    VideoQuality = device.TranscodeOverrides.VideoQuality ?? configuration.VirtualDefaults.VideoQuality,
                    OverrideMaximumVideoBitRate = device.TranscodeOverrides.MaximumVideoBitRateMbps.HasValue,
                    MaximumVideoBitRateMbps = device.TranscodeOverrides.MaximumVideoBitRateMbps ?? configuration.VirtualDefaults.MaximumVideoBitRateMbps
                };
                settings.OverridePublicBaseUri = binding is not null &&
                    !AreEquivalentRootUris(settings.AutomaticPublicBaseUri, publicBaseUri);
                return settings;
            }).ToList()
        };
        InitializeGuideOutputSettings(model.GuideOutputs, guideOutputDrivers);
        return model;
    }

    /// <summary>Builds and validates provider-neutral configuration with selection sets independent of the editable forms.</summary>
    public IReadOnlyList<string> TryBuild(
        TopologySettingsModel model,
        out StreamingConfiguration configuration,
        IEnumerable<IGuideSourceDriver>? guideSourceDrivers = null,
        IEnumerable<IPhysicalTunerDriver>? physicalTunerDrivers = null,
        IEnumerable<IVirtualTunerDriver>? virtualTunerDrivers = null,
        IEnumerable<IGuideOutputDriver>? guideOutputDrivers = null,
        IEnumerable<int>? managementListenerPorts = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        var errors = new List<string>();
        var outputDrivers = guideOutputDrivers?.ToDictionary(driver => driver.Kind, StringComparer.OrdinalIgnoreCase);
        configuration = new StreamingConfiguration
        {
            KeepWarmPeriodSeconds = model.KeepWarmPeriodSeconds,
            PhysicalDefaults = new PhysicalTranscodeProfile
            {
                ConvertHevcToH264 = model.PhysicalDefaults.ConvertHevcToH264,
                Deinterlace = model.PhysicalDefaults.Deinterlace,
                Audio = model.PhysicalDefaults.Audio,
                Ac4Audio = model.PhysicalDefaults.Ac4Audio,
                VideoPreset = model.PhysicalDefaults.VideoPreset,
                VideoQuality = model.PhysicalDefaults.VideoQuality,
                MaximumVideoBitRateMbps = model.PhysicalDefaults.MaximumVideoBitRateMbps
            },
            VirtualDefaults = new VirtualTranscodeProfile
            {
                ConvertHevcToH264 = model.VirtualDefaults.ConvertHevcToH264,
                Deinterlace = model.VirtualDefaults.Deinterlace,
                Audio = model.VirtualDefaults.Audio,
                Ac4Audio = model.VirtualDefaults.Ac4Audio,
                VideoPreset = model.VirtualDefaults.VideoPreset,
                VideoQuality = model.VirtualDefaults.VideoQuality,
                MaximumVideoBitRateMbps = model.VirtualDefaults.MaximumVideoBitRateMbps
            },
            VirtualOutputSecurityPolicies =
            [
                .. model.PreservedVirtualOutputSecurityPolicies.Where(policy =>
                    !model.VirtualDevices.Any(device =>
                        device.UseDedicatedListener &&
                        string.Equals(policy.Key, GetSecurityPolicyKey(device), StringComparison.OrdinalIgnoreCase))),
                .. model.VirtualDevices.Where(device => device.UseDedicatedListener).Select(BuildSecurityPolicy)
            ],
            VirtualOutputWorkerPolicies =
            [
                .. model.PreservedVirtualOutputWorkerPolicies.Where(policy =>
                    !model.VirtualDevices.Any(device =>
                        device.UseDedicatedListener &&
                        string.Equals(policy.Key, GetWorkerPolicyKey(device), StringComparison.OrdinalIgnoreCase))),
                .. model.VirtualDevices.Where(device => device.UseDedicatedListener).Select(BuildWorkerPolicy)
            ],
            PhysicalDevices = model.PhysicalDevices.Select((device, order) => new PhysicalDeviceDefinition
            {
                Id = device.Id,
                Name = device.Name.Trim(),
                Enabled = device.Enabled,
                Order = order,
                Adapter = BuildPhysicalAdapter(device),
                TranscodeOverrides = new PhysicalTranscodeOverrides
                {
                    ConvertHevcToH264 = device.OverrideConvertHevcToH264 ? device.ConvertHevcToH264 : null,
                    Deinterlace = device.OverrideDeinterlace ? device.Deinterlace : null,
                    Audio = device.OverrideAudio ? device.Audio : null,
                    Ac4Audio = device.OverrideAc4Audio ? device.Ac4Audio : null,
                    VideoPreset = device.OverrideVideoPreset ? device.VideoPreset : null,
                    VideoQuality = device.OverrideVideoQuality ? device.VideoQuality : null,
                    MaximumVideoBitRateMbps = device.OverrideMaximumVideoBitRate ? device.MaximumVideoBitRateMbps : null
                }
            }).ToArray(),
            GuideSources = model.GuideSources.Select(source => new GuideSourceDefinition
            {
                Id = source.Id,
                Name = source.Name.Trim(),
                Enabled = source.Enabled,
                Adapter = BuildGuideAdapter(source),
                Subscription = new PhysicalDeviceSubscription
                {
                    Mode = source.SubscriptionMode,
                    SelectedDeviceIds = source.SelectedDeviceIds.ToHashSet()
                }
            }).ToArray(),
            GuideOutputs = model.GuideOutputs.Select(output => new GuideOutputDefinition
            {
                Id = output.Id,
                Name = output.Name.Trim(),
                Key = output.Key.Trim(),
                Enabled = output.Enabled,
                IsBuiltIn = output.IsBuiltIn,
                Adapter = BuildGuideOutputAdapter(output, outputDrivers, errors),
                VirtualDeviceSelectionMode = output.VirtualDeviceSelectionMode,
                SelectedVirtualDeviceIds = output.SelectedVirtualDeviceIds.ToHashSet(),
                GuideSourceSelectionMode = output.GuideSourceSelectionMode,
                SelectedGuideSourceIds = output.SelectedGuideSourceIds.ToHashSet()
            }).ToArray(),
            VirtualDevices = model.VirtualDevices.Select(device => new VirtualDeviceDefinition
            {
                Id = device.Id,
                Name = device.Name.Trim(),
                Enabled = device.Enabled,
                Adapter = BuildVirtualAdapter(device),
                Hosting = BuildHosting(device),
                Subscription = new PhysicalDeviceSubscription
                {
                    Mode = device.SubscriptionMode,
                    SelectedDeviceIds = device.SelectedDeviceIds.ToHashSet()
                },
                TranscodeOverrides = new VirtualTranscodeOverrides
                {
                    ConvertHevcToH264 = device.OverrideConvertHevcToH264 ? device.ConvertHevcToH264 : null,
                    Deinterlace = device.OverrideDeinterlace ? device.Deinterlace : null,
                    Audio = device.OverrideAudio ? device.Audio : null,
                    Ac4Audio = device.OverrideAc4Audio ? device.Ac4Audio : null,
                    VideoPreset = device.OverrideVideoPreset ? device.VideoPreset : null,
                    VideoQuality = device.OverrideVideoQuality ? device.VideoQuality : null,
                    MaximumVideoBitRateMbps = device.OverrideMaximumVideoBitRate ? device.MaximumVideoBitRateMbps : null
                }
            }).ToArray()
        };

        errors.AddRange(StreamingConfigurationValidator.Validate(configuration));
        if (managementListenerPorts is not null)
        {
            var reservedPorts = managementListenerPorts.Where(port => port is >= 1 and <= 65_535).ToHashSet();
            foreach (var device in configuration.VirtualDevices.Where(device => device.Enabled && device.Hosting is not null))
            {
                foreach (var binding in device.Hosting!.Bindings.Where(binding => reservedPorts.Contains(binding.Port)))
                {
                    errors.Add($"Virtual device {device.Name} output binding port {binding.Port} conflicts with a Lineup management UI listener.");
                }
            }
        }
        if (guideSourceDrivers is not null)
        {
            var drivers = guideSourceDrivers.ToDictionary(driver => driver.Kind, StringComparer.OrdinalIgnoreCase);
            foreach (var source in configuration.GuideSources)
            {
                if (!drivers.TryGetValue(source.Adapter.Kind, out var driver))
                {
                    errors.Add($"Guide source {source.Name} uses unregistered adapter kind '{source.Adapter.Kind}'.");
                    continue;
                }
                errors.AddRange(driver.Validate(source.Adapter).Select(error => $"Guide source {source.Name}: {error}"));
                try
                {
                    GuideSourceSubscriptionResolver.Resolve(source, configuration.PhysicalDevices, driver);
                }
                catch (InvalidOperationException exception)
                {
                    errors.Add(exception.Message);
                }
            }
            if (physicalTunerDrivers is not null)
            {
                var physicalDrivers = physicalTunerDrivers.ToDictionary(driver => driver.Kind, StringComparer.OrdinalIgnoreCase);
                foreach (var device in configuration.PhysicalDevices)
                {
                    if (!physicalDrivers.TryGetValue(device.Adapter.Kind, out var physicalDriver))
                    {
                        errors.Add($"Physical device {device.Name} uses unregistered adapter kind '{device.Adapter.Kind}'.");
                        continue;
                    }
                    errors.AddRange(physicalDriver.Validate(device.Adapter).Select(error => $"Physical device {device.Name}: {error}"));
                }
            }
            if (virtualTunerDrivers is not null)
            {
                var virtualDrivers = virtualTunerDrivers.ToDictionary(driver => driver.Kind, StringComparer.OrdinalIgnoreCase);
                foreach (var device in configuration.VirtualDevices)
                {
                    if (!virtualDrivers.TryGetValue(device.Adapter.Kind, out var virtualDriver))
                    {
                        errors.Add($"Virtual device {device.Name} uses unregistered adapter kind '{device.Adapter.Kind}'.");
                        continue;
                    }
                    errors.AddRange(virtualDriver.Validate(device.Adapter).Select(error => $"Virtual device {device.Name}: {error}"));
                    try
                    {
                        VirtualDeviceSubscriptionResolver.Resolve(device, configuration.PhysicalDevices, virtualDriver);
                    }
                    catch (InvalidOperationException exception)
                    {
                        errors.Add(exception.Message);
                    }
                }
            }
        }
        if (outputDrivers is not null)
        {
            foreach (var output in configuration.GuideOutputs)
            {
                if (!outputDrivers.TryGetValue(output.Adapter.Kind, out var outputDriver))
                {
                    errors.Add($"Guide output {output.Name} uses unregistered adapter kind '{output.Adapter.Kind}'.");
                    continue;
                }
                errors.AddRange(outputDriver.Validate(output.Adapter).Select(error => $"Guide output {output.Name}: {error}"));
            }
        }
        foreach (var device in model.PhysicalDevices.Where(device => device.IsHdHomeRun && string.IsNullOrWhiteSpace(device.Address)))
        {
            errors.Add($"Physical device {device.Name} requires an address.");
        }
        foreach (var device in model.PhysicalDevices.Where(device => device.IsHdHomeRun && !string.IsNullOrWhiteSpace(device.Address) && !IsValidHdHomeRunAddress(device.Address)))
        {
            errors.Add($"Physical device {device.Name} address must be a host name, IP address, or absolute HTTP or HTTPS address without a path, query, or fragment.");
        }
        foreach (var device in model.VirtualDevices.Where(device => device.IsHdHomeRun && device.TunerCountCap is < 1 or > byte.MaxValue))
        {
            errors.Add($"Virtual device {device.Name} tuner-count cap must be between 1 and 255.");
        }
        foreach (var device in model.VirtualDevices.Where(device => device.IsHdHomeRun && !HdHomeRunProxyIdentity.IsValidDeviceId(NormalizeExternalDeviceId(device))))
        {
            errors.Add($"Virtual device {device.Name} requires a valid eight-character HDHomeRun DeviceID.");
        }
        foreach (var device in model.VirtualDevices.Where(device => device.UseDedicatedListener && device.PublishedPort is < 1 or > 65_535))
        {
            errors.Add($"Virtual device {device.Name} published port must be between 1 and 65535.");
        }
        errors.AddRange(WebPlayerVirtualTunerDriver.ValidateBuiltIn(configuration));
        return errors;
    }

    /// <summary>Adds a form using defaults supplied by a registered physical tuner driver.</summary>
    public PhysicalDeviceSettingsModel AddPhysicalDevice(TopologySettingsModel model, IPhysicalTunerDriver driver)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(driver);
        if (!driver.SupportsUserCreation)
        {
            throw new ArgumentException($"Physical tuner driver '{driver.Kind}' does not support user-created devices.", nameof(driver));
        }

        var id = PhysicalDeviceId.New();
        var adapter = driver.CreateDefaultConfiguration(id);
        if (!string.Equals(adapter.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Physical tuner driver '{driver.Kind}' created configuration for adapter kind '{adapter.Kind}'.");
        }

        var address = ReadString(adapter.Configuration, "Address");
        if (!string.IsNullOrWhiteSpace(address) && model.PhysicalDevices.Any(
            device => string.Equals(device.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(device.Address.Trim(), address.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            address = string.Empty;
        }

        var device = new PhysicalDeviceSettingsModel
        {
            Id = id,
            Name = $"{driver.DisplayName} {model.PhysicalDevices.Count(device => string.Equals(device.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase)) + 1}",
            Kind = driver.Kind,
            AdapterConfiguration = adapter.Configuration.Clone(),
            Address = address,
            LinkBaseUrl = ReadString(adapter.Configuration, "BaseUrl"),
            LinkVirtualDeviceId = ReadGuid(adapter.Configuration, "VirtualDeviceId"),
            LinkAccessToken = ReadString(adapter.Configuration, "AccessToken"),
            LinkTransportSecurity = ReadEnum(adapter.Configuration, "TransportSecurity", LineupLinkTransportSecurity.Https),
            LinkCertificateSha256 = ReadString(adapter.Configuration, "CertificateSha256"),
            ConvertHevcToH264 = model.PhysicalDefaults.ConvertHevcToH264,
            Deinterlace = model.PhysicalDefaults.Deinterlace,
            Audio = model.PhysicalDefaults.Audio,
            Ac4Audio = model.PhysicalDefaults.Ac4Audio,
            VideoPreset = model.PhysicalDefaults.VideoPreset,
            VideoQuality = model.PhysicalDefaults.VideoQuality,
            MaximumVideoBitRateMbps = model.PhysicalDefaults.MaximumVideoBitRateMbps
        };
        model.PhysicalDevices.Add(device);
        return device;
    }

    /// <summary>Adds a form using defaults supplied by a registered virtual tuner driver.</summary>
    public VirtualDeviceSettingsModel AddVirtualDevice(TopologySettingsModel model, IVirtualTunerDriver driver)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(driver);
        if (!driver.SupportsUserCreation)
        {
            throw new ArgumentException($"Virtual tuner driver '{driver.Kind}' does not support user-created devices.", nameof(driver));
        }

        var id = VirtualDeviceId.New();
        var adapter = driver.CreateDefaultConfiguration(id);
        if (!string.Equals(adapter.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Virtual tuner driver '{driver.Kind}' created configuration for adapter kind '{adapter.Kind}'.");
        }

        var device = new VirtualDeviceSettingsModel
        {
            Id = id,
            Name = $"{driver.DisplayName} {model.VirtualDevices.Count(device => string.Equals(device.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase)) + 1}",
            Kind = driver.Kind,
            AdapterConfiguration = adapter.Configuration.Clone(),
            TunerCountCap = ReadNullableInt(adapter.Configuration, "TunerCountCap"),
            ExternalDeviceId = ReadExternalDeviceId(id, adapter.Configuration),
            EnableNetworkAdvertisement = ReadBool(adapter.Configuration, "EnableNetworkAdvertisement", defaultValue: true),
            AdvertisedBaseUrl = ReadString(adapter.Configuration, "AdvertisedBaseUrl"),
            LinkInstanceId = ReadGuid(adapter.Configuration, "InstanceId") ?? Guid.NewGuid(),
            LinkPublicBaseUrl = ReadString(adapter.Configuration, "PublicBaseUrl"),
            LinkRequireEncryption = ReadBool(adapter.Configuration, "RequireEncryption", defaultValue: true),
            LinkAccessGrants = ReadLineupLinkAccessGrants(adapter.Configuration),
            LinkCertificateSha256 = ReadString(adapter.Configuration, "CertificateSha256"),
            ListenerPort = FindAvailableOutputPort(model),
            ConvertHevcToH264 = model.VirtualDefaults.ConvertHevcToH264,
            Deinterlace = model.VirtualDefaults.Deinterlace,
            Audio = model.VirtualDefaults.Audio,
            Ac4Audio = model.VirtualDefaults.Ac4Audio,
            VideoPreset = model.VirtualDefaults.VideoPreset,
            VideoQuality = model.VirtualDefaults.VideoQuality,
            MaximumVideoBitRateMbps = model.VirtualDefaults.MaximumVideoBitRateMbps
        };
        model.VirtualDevices.Add(device);
        return device;
    }

    /// <summary>Adds a form using defaults supplied by a registered guide-source driver.</summary>
    public GuideSourceSettingsModel AddGuideSource(TopologySettingsModel model, IGuideSourceDriver driver)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(driver);
        if (!driver.SupportsUserCreation)
        {
            throw new ArgumentException($"Guide-source driver '{driver.Kind}' does not support user-created sources.", nameof(driver));
        }

        var id = GuideSourceId.New();
        var adapter = driver.CreateDefaultConfiguration(id);
        if (!string.Equals(adapter.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Guide-source driver '{driver.Kind}' created configuration for adapter kind '{adapter.Kind}'.");
        }

        var source = new GuideSourceSettingsModel
        {
            Id = id,
            Name = $"{driver.DisplayName} {model.GuideSources.Count(source => string.Equals(source.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase)) + 1}",
            Kind = driver.Kind,
            AdapterConfiguration = adapter.Configuration.Clone(),
            SubscriptionMode = PhysicalDeviceSubscriptionMode.All
        };
        model.GuideSources.Add(source);
        return source;
    }

    /// <summary>Adds a form using defaults supplied by a registered guide-output driver.</summary>
    public GuideOutputSettingsModel AddGuideOutput(
        TopologySettingsModel model,
        IGuideOutputDriver driver,
        string defaultOutputDirectoryPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(driver);
        if (!driver.SupportsUserCreation)
        {
            throw new ArgumentException($"Guide-output driver '{driver.Kind}' does not support user-created outputs.", nameof(driver));
        }

        var id = GuideOutputId.New();
        var name = $"{driver.DisplayName} {model.GuideOutputs.Count(output => string.Equals(output.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase)) + 1}";
        var key = GuideOutputKey.CreateUnique(name, model.GuideOutputs.Select(output => output.Key));
        var adapter = driver.CreateDefaultConfiguration(
            id,
            new GuideOutputCreationContext
            {
                DefaultOutputDirectoryPath = defaultOutputDirectoryPath,
                OutputKey = key
            });
        if (!string.Equals(adapter.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Guide-output driver '{driver.Kind}' created configuration for adapter kind '{adapter.Kind}'.");
        }

        var output = new GuideOutputSettingsModel
        {
            Id = id,
            Name = name,
            Key = key,
            Kind = driver.Kind,
            AdapterConfiguration = adapter.Configuration.Clone(),
            OutputPath = ReadString(adapter.Configuration, "OutputPath")
        };
        InitializeGuideOutputSettings(output, driver);
        model.GuideOutputs.Add(output);
        return output;
    }

    /// <summary>Removes a physical device and its stale selected subscriptions.</summary>
    public void RemovePhysicalDevice(TopologySettingsModel model, PhysicalDeviceId id)
    {
        model.PhysicalDevices.RemoveAll(device => device.Id == id);
        foreach (var virtualDevice in model.VirtualDevices)
        {
            virtualDevice.SelectedDeviceIds.Remove(id);
        }
        foreach (var guideSource in model.GuideSources)
        {
            guideSource.SelectedDeviceIds.Remove(id);
        }
    }

    /// <summary>Removes a virtual device unless it is the built-in Web Player.</summary>
    public void RemoveVirtualDevice(TopologySettingsModel model, VirtualDeviceId id)
    {
        model.VirtualDevices.RemoveAll(device => device.Id == id && device.Kind != WebPlayerKind);
        foreach (var output in model.GuideOutputs)
        {
            output.SelectedVirtualDeviceIds.Remove(id);
        }
    }

    /// <summary>Removes a guide source.</summary>
    public void RemoveGuideSource(TopologySettingsModel model, GuideSourceId id)
    {
        model.GuideSources.RemoveAll(source => source.Id == id);
        foreach (var output in model.GuideOutputs)
        {
            output.SelectedGuideSourceIds.Remove(id);
        }
    }

    /// <summary>Removes a custom guide output.</summary>
    public void RemoveGuideOutput(TopologySettingsModel model, GuideOutputId id)
    {
        model.GuideOutputs.RemoveAll(output => output.Id == id && !output.IsBuiltIn);
    }

    private static AdapterConfiguration EmptyAdapter(string kind) => new(kind, JsonSerializer.SerializeToElement(new { }));

    private static List<LineupLinkAccessGrantSettingsModel> ReadLineupLinkAccessGrants(JsonElement configuration)
    {
        try
        {
            var typed = configuration.Deserialize<LineupLinkVirtualConfiguration>();
            return typed is null
                ? []
                : typed.AccessGrants
                    .Select(grant => LineupLinkAccessGrantSettingsModel.Create(grant))
                    .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Builds adapter configuration for one editable physical device.</summary>
    public AdapterConfiguration BuildPhysicalAdapter(PhysicalDeviceSettingsModel device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsLineupLink)
        {
            return new AdapterConfiguration(LineupLinkKind, JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
            {
                BaseUrl = device.LinkBaseUrl.Trim(),
                VirtualDeviceId = device.LinkVirtualDeviceId ?? Guid.Empty,
                AccessToken = device.LinkAccessToken.Trim(),
                TransportSecurity = device.LinkTransportSecurity,
                CertificateSha256 = NormalizeOptional(device.LinkCertificateSha256)
            }));
        }
        if (!device.IsHdHomeRun)
        {
            var configuration = device.AdapterConfiguration.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement(new { })
                : device.AdapterConfiguration.Clone();
            return new AdapterConfiguration(device.Kind, configuration);
        }

        return new AdapterConfiguration(PhysicalHdHomeRunKind, JsonSerializer.SerializeToElement(new { Address = device.Address.Trim() }));
    }

    private static AdapterConfiguration BuildVirtualAdapter(VirtualDeviceSettingsModel device)
    {
        if (device.IsLineupLink)
        {
            return new AdapterConfiguration(LineupLinkKind, JsonSerializer.SerializeToElement(new LineupLinkVirtualConfiguration
            {
                InstanceId = device.LinkInstanceId,
                PublicBaseUrl = device.UseDedicatedListener ? BuildPublicBaseUri(device) : device.LinkPublicBaseUrl.Trim(),
                RequireEncryption = device.EffectiveLineupLinkEncryptionRequired,
                AccessGrants = device.LinkAccessGrants.Select(grant => grant.ToPersisted()).ToArray(),
                CertificateSha256 = device.EffectiveLineupLinkEncryptionRequired
                    ? NormalizeOptional(device.LinkCertificateSha256)
                    : null
            }));
        }
        if (!device.IsHdHomeRun)
        {
            var configuration = device.AdapterConfiguration.ValueKind == JsonValueKind.Undefined
                ? JsonSerializer.SerializeToElement(new { })
                : device.AdapterConfiguration;
            return new AdapterConfiguration(device.Kind, configuration);
        }

        return new AdapterConfiguration(VirtualHdHomeRunKind, JsonSerializer.SerializeToElement(new
        {
            DeviceId = NormalizeExternalDeviceId(device),
            device.EnableNetworkAdvertisement,
            AdvertisedBaseUrl = device.UseDedicatedListener ? BuildPublicBaseUri(device) : device.AdvertisedBaseUrl.Trim(),
            device.TunerCountCap
        }));
    }

    private static VirtualOutputHostingDefinition? BuildHosting(VirtualDeviceSettingsModel device)
    {
        if (!device.UseDedicatedListener)
        {
            return null;
        }
        return new VirtualOutputHostingDefinition
        {
            ExecutionMode = device.ExecutionMode,
            Bindings =
            [
                new VirtualOutputBindingDefinition
                {
                    Transport = device.ListenerTransport,
                    Addresses = SplitList(device.ListenerAddresses),
                    Port = device.ListenerPort,
                    PublicBaseUri = BuildPublicBaseUri(device),
                    SecurityPolicyKey = GetSecurityPolicyKey(device)
                },
                .. device.AdditionalBindings
            ],
            WorkerPolicyKey = GetWorkerPolicyKey(device)
        };
    }

    private static VirtualOutputSecurityPolicyDefinition BuildSecurityPolicy(VirtualDeviceSettingsModel device) =>
        new()
        {
            Key = GetSecurityPolicyKey(device),
            Name = $"{device.Name.Trim()} listener security",
            CertificateReference = NormalizeOptional(device.CertificateReference),
            ClientCertificateMode = device.ClientCertificateMode,
            AllowedNetworks = SplitList(device.AllowedNetworks),
            MaximumConnections = device.MaximumConnections,
            RequestsPerMinutePerClient = device.RequestsPerMinutePerClient,
            AllowForwardedHeaders = device.AllowForwardedHeaders,
            TrustedProxies = SplitList(device.TrustedProxies)
        };

    private static VirtualOutputWorkerPolicyDefinition BuildWorkerPolicy(VirtualDeviceSettingsModel device) =>
        new()
        {
            Key = GetWorkerPolicyKey(device),
            Name = $"{device.Name.Trim()} worker policy",
            MaximumRestarts = device.MaximumRestarts,
            RestartWindowSeconds = device.RestartWindowSeconds,
            DrainTimeoutSeconds = device.DrainTimeoutSeconds,
            LeaseGracePeriodSeconds = device.LeaseGracePeriodSeconds,
            MaximumOutstandingBrokerCalls = device.MaximumOutstandingBrokerCalls,
            MaximumMemoryMib = device.MaximumMemoryMib,
            MaximumCpuPercent = device.MaximumCpuPercent
        };

    private static string GetSecurityPolicyKey(VirtualDeviceSettingsModel device) =>
        $"virtual-{device.Id.Value:N}-security";

    private static string GetWorkerPolicyKey(VirtualDeviceSettingsModel device) =>
        $"virtual-{device.Id.Value:N}-worker";

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static int FindAvailableOutputPort(TopologySettingsModel model)
    {
        var usedPorts = model.VirtualDevices
            .Where(device => device.UseDedicatedListener && device.ListenerPort > 0)
            .Select(device => device.ListenerPort)
            .ToHashSet();
        var port = 8600;
        while (usedPorts.Contains(port))
        {
            port++;
        }
        return port;
    }

    private static int FindAvailableOutputPort(StreamingConfiguration configuration, int deviceIndex)
    {
        var usedPorts = configuration.VirtualDevices
            .SelectMany(device => device.Hosting?.Bindings ?? [])
            .Where(binding => binding.Port > 0)
            .Select(binding => binding.Port)
            .ToHashSet();
        var port = 8600;
        for (var index = 0; index <= deviceIndex; index++)
        {
            var device = configuration.VirtualDevices[index];
            if (device.Hosting is not null || !RequiresDedicatedListener(device.Adapter.Kind))
            {
                continue;
            }
            while (usedPorts.Contains(port))
            {
                port++;
            }
            if (index == deviceIndex)
            {
                return port;
            }
            usedPorts.Add(port);
        }
        return port;
    }

    private static bool RequiresDedicatedListener(string adapterKind) =>
        string.Equals(adapterKind, VirtualHdHomeRunKind, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(adapterKind, LineupLinkKind, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(adapterKind, WebPlayerKind, StringComparison.OrdinalIgnoreCase);

    private static int? ResolvePublishedPort(VirtualOutputBindingDefinition? binding)
    {
        if (binding is null ||
            !Uri.TryCreate(binding.PublicBaseUri, UriKind.Absolute, out var publicUri) ||
            publicUri.Port == binding.Port)
        {
            return null;
        }
        return publicUri.Port;
    }

    private static string BuildPublicBaseUri(VirtualDeviceSettingsModel device)
        => device.EffectivePublicBaseUri;

    private static bool AreEquivalentRootUris(string first, string second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var firstUri) &&
        Uri.TryCreate(second, UriKind.Absolute, out var secondUri) &&
        firstUri.AbsoluteUri.Equals(secondUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static AdapterConfiguration BuildGuideAdapter(GuideSourceSettingsModel source)
    {
        var configuration = source.AdapterConfiguration.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { })
            : source.AdapterConfiguration.Clone();
        return new AdapterConfiguration(source.Kind, configuration);
    }

    private static AdapterConfiguration BuildGuideOutputAdapter(
        GuideOutputSettingsModel output,
        IReadOnlyDictionary<string, IGuideOutputDriver>? drivers,
        ICollection<string> errors)
    {
        if (string.Equals(output.Kind, "xmltv", StringComparison.OrdinalIgnoreCase))
        {
            return new AdapterConfiguration(output.Kind, JsonSerializer.SerializeToElement(new
            {
                OutputPath = output.OutputPath.Trim()
            }));
        }

        var configuration = output.AdapterConfiguration.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { })
            : output.AdapterConfiguration.Clone();
        var adapter = new AdapterConfiguration(output.Kind, configuration);
        if (output.AdapterSettingsErrors.Count > 0)
        {
            foreach (var error in output.AdapterSettingsErrors)
            {
                errors.Add($"Guide output {output.Name}: {error}");
            }
            return adapter;
        }
        if (output.AdapterSettings.Count == 0 ||
            drivers is null ||
            !drivers.TryGetValue(output.Kind, out var driver) ||
            driver is not IAdapterSettingsProvider provider)
        {
            return adapter;
        }

        var result = provider.ApplySettings(
            adapter,
            output.AdapterSettings.Select(setting => new AdapterSettingEdit
            {
                Key = setting.Descriptor.Key,
                Action = setting.Action,
                Value = setting.Value?.Clone()
            }).ToArray());
        output.GeneratedSecrets = result.GeneratedSecrets;
        if (result.Succeeded)
        {
            return result.Configuration!;
        }

        foreach (var error in result.Errors.SelectMany(pair => pair.Value.Select(message => $"{pair.Key}: {message}")))
        {
            errors.Add($"Guide output {output.Name}: {error}");
        }
        return adapter;
    }

    private static void InitializeGuideOutputSettings(
        IEnumerable<GuideOutputSettingsModel> outputs,
        IEnumerable<IGuideOutputDriver>? drivers)
    {
        if (drivers is null)
        {
            return;
        }

        var driversByKind = drivers.ToDictionary(driver => driver.Kind, StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            if (driversByKind.TryGetValue(output.Kind, out var driver))
            {
                InitializeGuideOutputSettings(output, driver);
            }
        }
    }

    private static void InitializeGuideOutputSettings(GuideOutputSettingsModel output, IGuideOutputDriver driver)
    {
        if (string.Equals(output.Kind, XmltvGuideOutputDriver.AdapterKind, StringComparison.OrdinalIgnoreCase) ||
            driver is not IAdapterSettingsProvider provider)
        {
            return;
        }

        var descriptorErrors = AdapterSettingsValidator.ValidateDescriptor(provider.SettingsDescriptor);
        var adapter = new AdapterConfiguration(output.Kind, output.AdapterConfiguration.Clone());
        var snapshot = provider.GetSettings(adapter);
        var snapshotErrors = AdapterSettingsValidator.ValidateSnapshot(provider.SettingsDescriptor, snapshot);
        output.AdapterSettingsErrors = descriptorErrors.Concat(snapshotErrors).ToArray();
        if (output.AdapterSettingsErrors.Count > 0)
        {
            return;
        }

        var values = snapshot.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        output.AdapterSettings = provider.SettingsDescriptor.Fields.Select(field =>
        {
            var value = values[field.Key];
            return new AdapterSettingFieldSettingsModel
            {
                Descriptor = field,
                Value = value.Value?.Clone(),
                HasValue = value.HasValue,
                OriginalHasValue = value.HasValue
            };
        }).ToList();
    }

    private static string ReadString(JsonElement configuration, string propertyName) =>
        configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty(propertyName, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    private static int? ReadNullableInt(JsonElement configuration, string propertyName) =>
        configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static Guid? ReadGuid(JsonElement configuration, string propertyName) =>
        configuration.ValueKind == JsonValueKind.Object
        && configuration.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static bool ReadBool(JsonElement configuration, string propertyName, bool defaultValue) =>
        configuration.ValueKind == JsonValueKind.Object
        && configuration.TryGetProperty(propertyName, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static TEnum ReadEnum<TEnum>(JsonElement configuration, string propertyName, TEnum defaultValue)
        where TEnum : struct, Enum =>
        configuration.ValueKind == JsonValueKind.Object
        && configuration.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && Enum.TryParse<TEnum>(value.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : defaultValue;

    private static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : LineupLinkConfigurationValidation.NormalizeCertificateSha256(value);

    private static string ReadExternalDeviceId(VirtualDeviceDefinition device) => ReadExternalDeviceId(device.Id, device.Adapter.Configuration);

    private static string ReadExternalDeviceId(VirtualDeviceId deviceId, JsonElement configuration)
    {
        var configured = ReadString(configuration, "DeviceId");
        return HdHomeRunProxyIdentity.IsValidDeviceId(configured)
            ? configured.ToUpperInvariant()
            : HdHomeRunProxyIdentity.CreateDeviceId(deviceId.Value.ToString("N"));
    }

    private static string NormalizeExternalDeviceId(VirtualDeviceSettingsModel device) =>
        string.IsNullOrWhiteSpace(device.ExternalDeviceId)
            ? HdHomeRunProxyIdentity.CreateDeviceId(device.Id.Value.ToString("N"))
            : device.ExternalDeviceId.Trim().ToUpperInvariant();

    private static bool IsValidHdHomeRunAddress(string address)
    {
        var candidate = address.Trim();
        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"http://{candidate}";
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(parsed.Host)
            && parsed.AbsolutePath == "/"
            && string.IsNullOrEmpty(parsed.Query)
            && string.IsNullOrEmpty(parsed.Fragment)
            && string.IsNullOrEmpty(parsed.UserInfo);
    }
}

/// <summary>Editable streaming topology.</summary>
public sealed class TopologySettingsModel
{
    /// <summary>Gets or sets how long unused shared live pipelines remain available for reuse.</summary>
    public int KeepWarmPeriodSeconds { get; set; }

    /// <summary>Gets or sets physical-stage defaults.</summary>
    public PhysicalTranscodeSettingsModel PhysicalDefaults { get; set; } = new();

    /// <summary>Gets or sets virtual-stage defaults.</summary>
    public VirtualTranscodeSettingsModel VirtualDefaults { get; set; } = new();

    /// <summary>Gets physical device forms.</summary>
    public List<PhysicalDeviceSettingsModel> PhysicalDevices { get; init; } = [];

    /// <summary>Gets guide source forms.</summary>
    public List<GuideSourceSettingsModel> GuideSources { get; init; } = [];

    /// <summary>Gets guide output forms.</summary>
    public List<GuideOutputSettingsModel> GuideOutputs { get; init; } = [];

    /// <summary>Gets virtual device forms.</summary>
    public List<VirtualDeviceSettingsModel> VirtualDevices { get; init; } = [];

    /// <summary>Gets persisted security policies not represented by the primary-listener editor.</summary>
    public List<VirtualOutputSecurityPolicyDefinition> PreservedVirtualOutputSecurityPolicies { get; init; } = [];

    /// <summary>Gets persisted worker policies retained while editing primary listener settings.</summary>
    public List<VirtualOutputWorkerPolicyDefinition> PreservedVirtualOutputWorkerPolicies { get; init; } = [];
}

/// <summary>Defines editable transcoder settings shared by global and device forms.</summary>
public interface ITranscodeSettingsModel
{
    /// <summary>Gets or sets HEVC conversion.</summary>
    bool ConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets deinterlacing.</summary>
    SourceDeinterlacePolicy Deinterlace { get; set; }

    /// <summary>Gets or sets non-AC-4 audio handling.</summary>
    OutputAudioPolicy Audio { get; set; }

    /// <summary>Gets or sets AC-4 audio handling.</summary>
    OutputAudioPolicy Ac4Audio { get; set; }

    /// <summary>Gets or sets the video encoder preset.</summary>
    VideoEncoderPreset VideoPreset { get; set; }

    /// <summary>Gets or sets video quality.</summary>
    int VideoQuality { get; set; }

    /// <summary>Gets or sets maximum video bitrate.</summary>
    int MaximumVideoBitRateMbps { get; set; }
}

/// <summary>Defines editable sparse transcoder overrides for a device.</summary>
public interface ITranscodeOverrideSettingsModel : ITranscodeSettingsModel
{
    /// <summary>Gets or sets whether HEVC conversion is overridden.</summary>
    bool OverrideConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets whether deinterlacing is overridden.</summary>
    bool OverrideDeinterlace { get; set; }

    /// <summary>Gets or sets whether non-AC-4 audio handling is overridden.</summary>
    bool OverrideAudio { get; set; }

    /// <summary>Gets or sets whether AC-4 audio handling is overridden.</summary>
    bool OverrideAc4Audio { get; set; }

    /// <summary>Gets or sets whether the video encoder preset is overridden.</summary>
    bool OverrideVideoPreset { get; set; }

    /// <summary>Gets or sets whether video quality is overridden.</summary>
    bool OverrideVideoQuality { get; set; }

    /// <summary>Gets or sets whether bitrate is overridden.</summary>
    bool OverrideMaximumVideoBitRate { get; set; }
}

/// <summary>Editable physical-stage defaults.</summary>
public sealed class PhysicalTranscodeSettingsModel : ITranscodeSettingsModel
{
    /// <summary>Gets or sets HEVC conversion.</summary>
    public bool ConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets deinterlacing.</summary>
    public SourceDeinterlacePolicy Deinterlace { get; set; }

    /// <summary>Gets or sets non-AC-4 audio handling.</summary>
    public OutputAudioPolicy Audio { get; set; }

    /// <summary>Gets or sets AC-4 audio handling.</summary>
    public OutputAudioPolicy Ac4Audio { get; set; } = OutputAudioPolicy.Copy;

    /// <summary>Gets or sets the video encoder preset.</summary>
    public VideoEncoderPreset VideoPreset { get; set; } = VideoEncoderPreset.VeryFast;

    /// <summary>Gets or sets video quality.</summary>
    public int VideoQuality { get; set; } = 21;

    /// <summary>Gets or sets maximum video bitrate.</summary>
    public int MaximumVideoBitRateMbps { get; set; } = 10;
}

/// <summary>Editable virtual-stage defaults.</summary>
public sealed class VirtualTranscodeSettingsModel : ITranscodeSettingsModel
{
    /// <summary>Gets or sets HEVC conversion.</summary>
    public bool ConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets deinterlacing.</summary>
    public SourceDeinterlacePolicy Deinterlace { get; set; }

    /// <summary>Gets or sets non-AC-4 audio handling.</summary>
    public OutputAudioPolicy Audio { get; set; }

    /// <summary>Gets or sets AC-4 audio handling.</summary>
    public OutputAudioPolicy Ac4Audio { get; set; } = OutputAudioPolicy.Ac3;

    /// <summary>Gets or sets the video encoder preset.</summary>
    public VideoEncoderPreset VideoPreset { get; set; } = VideoEncoderPreset.VeryFast;

    /// <summary>Gets or sets video quality.</summary>
    public int VideoQuality { get; set; } = 21;

    /// <summary>Gets or sets maximum video bitrate.</summary>
    public int MaximumVideoBitRateMbps { get; set; } = 10;
}

/// <summary>Represents one editable Lineup Link access grant and its one-time secret state.</summary>
public sealed class LineupLinkAccessGrantSettingsModel
{
    /// <summary>Gets or sets the stable grant identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user-facing grant name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the persisted token hash.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Gets or sets when the grant was created in UTC.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets when the grant was last rotated in UTC.</summary>
    public DateTime? RotatedAtUtc { get; set; }

    /// <summary>Gets or sets when the grant expires in UTC.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Gets or sets when the grant was revoked in UTC.</summary>
    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Gets or sets the one-time plaintext token.</summary>
    public string RawToken { get; set; } = string.Empty;

    /// <summary>Gets or sets transient exported connection-bundle text.</summary>
    public string ConnectionBundle { get; set; } = string.Empty;

    /// <summary>Gets or sets whether transient connection-bundle text is visible.</summary>
    public bool ConnectionBundleExpanded { get; set; }

    /// <summary>Gets or sets whether permanent purge confirmation is visible.</summary>
    public bool PurgeConfirmationVisible { get; set; }

    /// <summary>Creates an editable model from one persisted grant.</summary>
    public static LineupLinkAccessGrantSettingsModel Create(LineupLinkAccessGrant grant, string? rawToken = null) =>
        new()
        {
            Id = grant.Id,
            Name = grant.Name,
            TokenHash = grant.TokenHash,
            CreatedAtUtc = grant.CreatedAtUtc,
            RotatedAtUtc = grant.RotatedAtUtc,
            ExpiresAtUtc = grant.ExpiresAtUtc,
            RevokedAtUtc = grant.RevokedAtUtc,
            RawToken = rawToken ?? string.Empty
        };

    /// <summary>Creates the hash-only persisted grant.</summary>
    public LineupLinkAccessGrant ToPersisted() =>
        new()
        {
            Id = Id,
            Name = Name.Trim(),
            TokenHash = TokenHash,
            CreatedAtUtc = CreatedAtUtc,
            RotatedAtUtc = RotatedAtUtc,
            ExpiresAtUtc = ExpiresAtUtc,
            RevokedAtUtc = RevokedAtUtc
        };
}

/// <summary>Editable physical device.</summary>
public sealed class PhysicalDeviceSettingsModel : ITranscodeOverrideSettingsModel
{
    /// <summary>Gets or sets identity.</summary>
    public PhysicalDeviceId Id { get; set; }

    /// <summary>Gets or sets display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets enabled state.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets adapter kind.</summary>
    public string Kind { get; set; } = "hdhomerun";

    /// <summary>Gets or sets adapter-owned configuration for device types without specialized Web fields.</summary>
    public JsonElement AdapterConfiguration { get; set; } = JsonSerializer.SerializeToElement(new { });

    /// <summary>Gets whether this is a physical HDHomeRun.</summary>
    public bool IsHdHomeRun => string.Equals(Kind, TopologySettingsEditor.PhysicalHdHomeRunKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether this is a Lineup Link physical input.</summary>
    public bool IsLineupLink => string.Equals(Kind, TopologySettingsEditor.LineupLinkKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets or sets HDHomeRun address.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote Lineup base URL.</summary>
    public string LinkBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote virtual-device identity.</summary>
    public Guid? LinkVirtualDeviceId { get; set; }

    /// <summary>Gets or sets the remote bearer token.</summary>
    public string LinkAccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the remote transport security.</summary>
    public LineupLinkTransportSecurity LinkTransportSecurity { get; set; } = LineupLinkTransportSecurity.Https;

    /// <summary>Gets or sets the optional remote certificate fingerprint.</summary>
    public string LinkCertificateSha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets transient connection-bundle text used for import.</summary>
    public string LinkConnectionBundle { get; set; } = string.Empty;

    /// <summary>Gets or sets whether HEVC conversion is overridden.</summary>
    public bool OverrideConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets effective HEVC conversion.</summary>
    public bool ConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets whether deinterlacing is overridden.</summary>
    public bool OverrideDeinterlace { get; set; }

    /// <summary>Gets or sets effective deinterlacing.</summary>
    public SourceDeinterlacePolicy Deinterlace { get; set; }

    /// <summary>Gets or sets whether non-AC-4 audio handling is overridden.</summary>
    public bool OverrideAudio { get; set; }

    /// <summary>Gets or sets effective non-AC-4 audio handling.</summary>
    public OutputAudioPolicy Audio { get; set; }

    /// <summary>Gets or sets whether AC-4 audio handling is overridden.</summary>
    public bool OverrideAc4Audio { get; set; }

    /// <summary>Gets or sets effective AC-4 audio handling.</summary>
    public OutputAudioPolicy Ac4Audio { get; set; } = OutputAudioPolicy.Copy;

    /// <summary>Gets or sets whether the video encoder preset is overridden.</summary>
    public bool OverrideVideoPreset { get; set; }

    /// <summary>Gets or sets the effective video encoder preset.</summary>
    public VideoEncoderPreset VideoPreset { get; set; } = VideoEncoderPreset.VeryFast;

    /// <summary>Gets or sets whether video quality is overridden.</summary>
    public bool OverrideVideoQuality { get; set; }

    /// <summary>Gets or sets effective video quality.</summary>
    public int VideoQuality { get; set; } = 21;

    /// <summary>Gets or sets whether bitrate is overridden.</summary>
    public bool OverrideMaximumVideoBitRate { get; set; }

    /// <summary>Gets or sets effective maximum bitrate.</summary>
    public int MaximumVideoBitRateMbps { get; set; } = 10;
}

/// <summary>Editable guide source.</summary>
public sealed class GuideSourceSettingsModel
{
    /// <summary>Gets or sets identity.</summary>
    public GuideSourceId Id { get; set; }

    /// <summary>Gets or sets display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets enabled state.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets adapter kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets adapter-owned configuration.</summary>
    public JsonElement AdapterConfiguration { get; set; } = JsonSerializer.SerializeToElement(new { });

    /// <summary>Gets or sets physical subscription mode.</summary>
    public PhysicalDeviceSubscriptionMode SubscriptionMode { get; set; } = PhysicalDeviceSubscriptionMode.All;

    /// <summary>Gets selected physical device IDs.</summary>
    public HashSet<PhysicalDeviceId> SelectedDeviceIds { get; set; } = [];
}

/// <summary>Editable guide output.</summary>
public sealed class GuideOutputSettingsModel
{
    /// <summary>Gets or sets identity.</summary>
    public GuideOutputId Id { get; set; }

    /// <summary>Gets or sets display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the stable code-safe output key.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets enabled state.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets whether this output is plugin-owned and cannot be removed.</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Gets or sets adapter kind.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets adapter-owned configuration.</summary>
    public JsonElement AdapterConfiguration { get; set; } = JsonSerializer.SerializeToElement(new { });

    /// <summary>Gets portable adapter-owned settings exposed by the selected plugin.</summary>
    public List<AdapterSettingFieldSettingsModel> AdapterSettings { get; set; } = [];

    /// <summary>Gets descriptor or snapshot errors that prevent portable settings from being edited.</summary>
    public IReadOnlyList<string> AdapterSettingsErrors { get; set; } = [];

    /// <summary>Gets generated plaintext secrets returned by the plugin after a successful apply.</summary>
    public IReadOnlyDictionary<string, string> GeneratedSecrets { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets or sets the XMLTV publication path.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Gets or sets virtual-device selection behavior.</summary>
    public GuideOutputSelectionMode VirtualDeviceSelectionMode { get; set; } = GuideOutputSelectionMode.All;

    /// <summary>Gets selected virtual-device IDs.</summary>
    public HashSet<VirtualDeviceId> SelectedVirtualDeviceIds { get; set; } = [];

    /// <summary>Gets or sets guide-source selection behavior.</summary>
    public GuideOutputSelectionMode GuideSourceSelectionMode { get; set; } = GuideOutputSelectionMode.All;

    /// <summary>Gets selected guide-source IDs.</summary>
    public HashSet<GuideSourceId> SelectedGuideSourceIds { get; set; } = [];
}

/// <summary>Editable state for one portable adapter setting.</summary>
public sealed class AdapterSettingFieldSettingsModel
{
    /// <summary>Gets the plugin-owned field descriptor.</summary>
    public required AdapterSettingDescriptor Descriptor { get; init; }

    /// <summary>Gets or sets the current non-secret serialized value.</summary>
    public JsonElement? Value { get; set; }

    /// <summary>Gets or sets whether the working field has a configured value.</summary>
    public bool HasValue { get; set; }

    /// <summary>Gets whether the persisted snapshot initially had a configured value.</summary>
    public bool OriginalHasValue { get; set; }

    /// <summary>Gets or sets the pending mutation submitted to the plugin.</summary>
    public AdapterSettingEditAction Action { get; set; } = AdapterSettingEditAction.Preserve;
}

/// <summary>Editable virtual device.</summary>
public sealed class VirtualDeviceSettingsModel : ITranscodeOverrideSettingsModel
{
    /// <summary>Gets or sets identity.</summary>
    public VirtualDeviceId Id { get; set; }

    /// <summary>Gets or sets display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets enabled state.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets adapter kind.</summary>
    public string Kind { get; set; } = "hdhomerun";

    /// <summary>Gets or sets adapter-owned configuration for device types without specialized Web fields.</summary>
    public JsonElement AdapterConfiguration { get; set; } = JsonSerializer.SerializeToElement(new { });

    /// <summary>Gets whether this is the built-in Web Player.</summary>
    public bool IsWebPlayer => string.Equals(Kind, WebPlayerVirtualTunerDriver.AdapterKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether this is a virtual HDHomeRun.</summary>
    public bool IsHdHomeRun => string.Equals(Kind, "hdhomerun", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether this is a Lineup Link virtual output.</summary>
    public bool IsLineupLink => string.Equals(Kind, TopologySettingsEditor.LineupLinkKind, StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets whether this adapter can use the current dedicated listener workers.</summary>
    public bool SupportsDedicatedHosting => IsHdHomeRun || IsLineupLink || IsWebPlayer;

    /// <summary>Gets or sets whether this output owns an isolated listener process.</summary>
    public bool UseDedicatedListener { get; set; }

    /// <summary>Gets or sets how the isolated worker is started.</summary>
    public VirtualOutputExecutionMode ExecutionMode { get; set; } = VirtualOutputExecutionMode.ManagedProcess;

    /// <summary>Gets or sets the dedicated listener transport.</summary>
    public VirtualOutputTransport ListenerTransport { get; set; }

    /// <summary>Gets or sets comma-separated listener addresses.</summary>
    public string ListenerAddresses { get; set; } = "localhost";

    /// <summary>Gets or sets the dedicated listener TCP port.</summary>
    public int ListenerPort { get; set; }

    /// <summary>Gets or sets an externally published port that differs from the listener port.</summary>
    public int? PublishedPort { get; set; }

    private bool _overridePublicBaseUri;

    /// <summary>Gets or sets whether the automatically derived client-facing URL is overridden.</summary>
    public bool OverridePublicBaseUri
    {
        get => _overridePublicBaseUri;
        set
        {
            if (value && !_overridePublicBaseUri && string.IsNullOrWhiteSpace(PublicBaseUri))
            {
                PublicBaseUri = AutomaticPublicBaseUri;
            }
            _overridePublicBaseUri = value;
        }
    }

    /// <summary>Gets or sets the manually overridden client-facing listener root URI.</summary>
    public string PublicBaseUri { get; set; } = string.Empty;

    /// <summary>Gets the client-facing root URI derived from the listener transport, address, and published port, or an empty string while the derived port is invalid.</summary>
    public string AutomaticPublicBaseUri
    {
        get
        {
            var address = ListenerAddresses
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "localhost";
            if (address is "*" or "+" or "0.0.0.0" or "::" or "[::]")
            {
                address = "localhost";
            }
            var scheme = ListenerTransport == VirtualOutputTransport.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
            var port = PublishedPort is >= 1 and <= 65_535 ? PublishedPort.Value : ListenerPort;
            if (port is < 1 or > 65_535)
            {
                return string.Empty;
            }
            return new UriBuilder(scheme, address, port).Uri.AbsoluteUri;
        }
    }

    /// <summary>Gets the effective client-facing listener root URI.</summary>
    public string EffectivePublicBaseUri
    {
        get
        {
            var value = OverridePublicBaseUri ? PublicBaseUri.Trim() : AutomaticPublicBaseUri;
            if (PublishedPort is not int publishedPort ||
                publishedPort is < 1 or > 65_535 ||
                !Uri.TryCreate(value, UriKind.Absolute, out var parsed))
            {
                return value;
            }
            return new UriBuilder(parsed) { Port = publishedPort }.Uri.AbsoluteUri;
        }
    }

    /// <summary>Gets additional persisted bindings retained while editing the primary listener.</summary>
    public List<VirtualOutputBindingDefinition> AdditionalBindings { get; set; } = [];

    /// <summary>Gets or sets the trusted-host certificate reference used for HTTPS.</summary>
    public string CertificateReference { get; set; } = string.Empty;

    /// <summary>Gets or sets client-certificate enforcement.</summary>
    public VirtualOutputClientCertificateMode ClientCertificateMode { get; set; }

    /// <summary>Gets or sets comma-separated client network CIDRs.</summary>
    public string AllowedNetworks { get; set; } = string.Empty;

    /// <summary>Gets or sets the public connection limit.</summary>
    public int MaximumConnections { get; set; }

    /// <summary>Gets or sets the per-client request limit.</summary>
    public int RequestsPerMinutePerClient { get; set; }

    /// <summary>Gets or sets whether trusted forwarded headers are allowed.</summary>
    public bool AllowForwardedHeaders { get; set; }

    /// <summary>Gets or sets comma-separated trusted proxy addresses or CIDRs.</summary>
    public string TrustedProxies { get; set; } = string.Empty;

    /// <summary>Gets or sets the maximum restarts in the restart window.</summary>
    public int MaximumRestarts { get; set; } = 5;

    /// <summary>Gets or sets the restart accounting window in seconds.</summary>
    public int RestartWindowSeconds { get; set; } = 300;

    /// <summary>Gets or sets the graceful drain timeout in seconds.</summary>
    public int DrainTimeoutSeconds { get; set; } = 15;

    /// <summary>Gets or sets the lease grace period in seconds.</summary>
    public int LeaseGracePeriodSeconds { get; set; } = 15;

    /// <summary>Gets or sets the maximum concurrent broker calls.</summary>
    public int MaximumOutstandingBrokerCalls { get; set; } = 32;

    /// <summary>Gets or sets the requested worker memory limit in mebibytes.</summary>
    public int MaximumMemoryMib { get; set; }

    /// <summary>Gets or sets the requested worker CPU percentage limit.</summary>
    public int MaximumCpuPercent { get; set; }

    /// <summary>Gets or sets an optional HDHomeRun tuner-count cap.</summary>
    public int? TunerCountCap { get; set; }

    /// <summary>Gets or sets the persisted externally advertised HDHomeRun DeviceID.</summary>
    public string ExternalDeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the virtual HDHomeRun participates in UDP and SSDP discovery.</summary>
    public bool EnableNetworkAdvertisement { get; set; } = true;

    /// <summary>Gets or sets the externally reachable Lineup root URL advertised during discovery.</summary>
    public string AdvertisedBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the stable Lineup Link provider identity.</summary>
    public Guid LinkInstanceId { get; set; } = Guid.NewGuid();

    /// <summary>Gets or sets the externally reachable Lineup base URL.</summary>
    public string LinkPublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets whether Lineup Link requests require HTTPS.</summary>
    public bool LinkRequireEncryption { get; set; } = true;

    /// <summary>Gets whether the effective Lineup Link endpoint requires HTTPS.</summary>
    public bool EffectiveLineupLinkEncryptionRequired =>
        UseDedicatedListener
            ? Uri.TryCreate(EffectivePublicBaseUri, UriKind.Absolute, out var publicUri) &&
              string.Equals(publicUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            : LinkRequireEncryption;

    /// <summary>Gets independently managed Lineup Link access grants.</summary>
    public List<LineupLinkAccessGrantSettingsModel> LinkAccessGrants { get; set; } = [];

    /// <summary>Gets or sets the name used when creating the next Lineup Link access grant.</summary>
    public string LinkNewAccessGrantName { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional local expiration used when creating the next access grant.</summary>
    public DateTime? LinkNewAccessGrantExpiresAtLocal { get; set; }

    /// <summary>Gets or sets the optional server-certificate fingerprint.</summary>
    public string LinkCertificateSha256 { get; set; } = string.Empty;

    /// <summary>Gets or sets transient exported connection-bundle text.</summary>
    public string LinkConnectionBundle { get; set; } = string.Empty;

    /// <summary>Gets or sets whether transient connection-bundle text is visible.</summary>
    public bool LinkConnectionBundleExpanded { get; set; }

    /// <summary>Gets or sets subscription mode.</summary>
    public PhysicalDeviceSubscriptionMode SubscriptionMode { get; set; }

    /// <summary>Gets selected physical devices.</summary>
    public HashSet<PhysicalDeviceId> SelectedDeviceIds { get; init; } = [];

    /// <summary>Gets or sets whether HEVC conversion is overridden.</summary>
    public bool OverrideConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets effective HEVC conversion.</summary>
    public bool ConvertHevcToH264 { get; set; }

    /// <summary>Gets or sets whether deinterlacing is overridden.</summary>
    public bool OverrideDeinterlace { get; set; }

    /// <summary>Gets or sets effective deinterlacing.</summary>
    public SourceDeinterlacePolicy Deinterlace { get; set; }

    /// <summary>Gets or sets whether non-AC-4 audio handling is overridden.</summary>
    public bool OverrideAudio { get; set; }

    /// <summary>Gets or sets effective non-AC-4 audio handling.</summary>
    public OutputAudioPolicy Audio { get; set; }

    /// <summary>Gets or sets whether AC-4 audio handling is overridden.</summary>
    public bool OverrideAc4Audio { get; set; }

    /// <summary>Gets or sets effective AC-4 audio handling.</summary>
    public OutputAudioPolicy Ac4Audio { get; set; } = OutputAudioPolicy.Copy;

    /// <summary>Gets or sets whether the video encoder preset is overridden.</summary>
    public bool OverrideVideoPreset { get; set; }

    /// <summary>Gets or sets the effective video encoder preset.</summary>
    public VideoEncoderPreset VideoPreset { get; set; } = VideoEncoderPreset.VeryFast;

    /// <summary>Gets or sets whether video quality is overridden.</summary>
    public bool OverrideVideoQuality { get; set; }

    /// <summary>Gets or sets effective video quality.</summary>
    public int VideoQuality { get; set; } = 21;

    /// <summary>Gets or sets whether bitrate is overridden.</summary>
    public bool OverrideMaximumVideoBitRate { get; set; }

    /// <summary>Gets or sets effective maximum bitrate.</summary>
    public int MaximumVideoBitRateMbps { get; set; } = 10;
}
