using System.Text.Json;
using Lineup.Core;
using Lineup.Common.Configuration;
using Lineup.Extensibility.Configuration;
using Lineup.Common.Guides;
using Lineup.Extensibility.Guides;
using Lineup.Core.Settings;
using Lineup.Common.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.LineupLink.Protocol;
using Lineup.LineupLink.Virtual;
using Spectre.Console;

namespace Lineup.Tui;

/// <summary>
/// Renders portable plugin settings descriptors in the terminal.
/// </summary>
internal static class TuiAdapterSettingsEditor
{
    /// <summary>
    /// Opens the topology configuration menu.
    /// </summary>
    public static async Task ConfigureAsync(
        ILineupSettingsService settings,
        IReadOnlyList<IPhysicalTunerDriver> physicalDrivers,
        IReadOnlyList<IVirtualTunerDriver> virtualDrivers,
        IReadOnlyList<IGuideSourceDriver> guideDrivers,
        IReadOnlyList<IGuideOutputDriver> guideOutputDrivers)
    {
        while (true)
        {
            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold blue]Configure sources[/]")
                    .AddChoices(
                    [
                        "Add physical device",
                        "Add virtual device",
                        "Add guide source",
                        "Add guide output",
                        "Edit configured adapter",
                        "Manage adapter access grants",
                        "Edit virtual output hosting",
                        "Remove configured adapter",
                        "Back"
                    ]));
            switch (action)
            {
                case "Add physical device":
                    await AddPhysicalAsync(settings, physicalDrivers);
                    break;
                case "Add virtual device":
                    await AddVirtualAsync(settings, virtualDrivers);
                    break;
                case "Add guide source":
                    await AddGuideAsync(settings, guideDrivers);
                    break;
                case "Add guide output":
                    await AddGuideOutputAsync(settings, guideOutputDrivers);
                    break;
                case "Edit configured adapter":
                    await EditAsync(settings, physicalDrivers, virtualDrivers, guideDrivers, guideOutputDrivers);
                    break;
                case "Manage adapter access grants":
                    await ManageAccessGrantsAsync(settings, virtualDrivers);
                    break;
                case "Edit virtual output hosting":
                    await EditVirtualOutputHostingAsync(settings, virtualDrivers);
                    break;
                case "Remove configured adapter":
                    await RemoveAsync(settings);
                    break;
                default:
                    return;
            }
        }
    }

    private static async Task EditVirtualOutputHostingAsync(
                ILineupSettingsService settings,
                IReadOnlyList<IVirtualTunerDriver> drivers)
    {
        var configuration = settings.Settings.Streaming;
        var candidates = configuration.VirtualDevices
            .Select(device => new
            {
                Device = device,
                Driver = drivers.SingleOrDefault(driver => string.Equals(driver.Kind, device.Adapter.Kind, StringComparison.OrdinalIgnoreCase))
            })
            .Where(item => item.Driver?.OutputCapabilities.RequiresExternalHosting == true)
            .ToArray();
        if (candidates.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No configured virtual outputs support dedicated hosting.[/]");
            return;
        }

        var selectedName = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select a virtual output:")
                .AddChoices(candidates.Select(item => item.Device.Name)));
        var device = candidates.First(item => string.Equals(item.Device.Name, selectedName, StringComparison.Ordinal)).Device;
        var enabled = AnsiConsole.Confirm(
            "Host this output in an isolated worker process?",
            device.Hosting is not null);
        var securityKey = GetVirtualOutputPolicyKey(device.Id, "security");
        var workerKey = GetVirtualOutputPolicyKey(device.Id, "worker");
        if (!enabled)
        {
            await settings.UpdateAsync(value => value.Streaming = value.Streaming with
            {
                VirtualDevices = value.Streaming.VirtualDevices
                    .Select(candidate => candidate.Id == device.Id ? candidate with { Hosting = null } : candidate)
                    .ToArray(),
                VirtualOutputSecurityPolicies = value.Streaming.VirtualOutputSecurityPolicies
                    .Where(policy => !string.Equals(policy.Key, securityKey, StringComparison.OrdinalIgnoreCase))
                    .ToArray(),
                VirtualOutputWorkerPolicies = value.Streaming.VirtualOutputWorkerPolicies
                    .Where(policy => !string.Equals(policy.Key, workerKey, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            });
            return;
        }

        var currentBinding = device.Hosting?.Bindings.FirstOrDefault();
        var currentSecurity = configuration.VirtualOutputSecurityPolicies.FirstOrDefault(policy =>
            string.Equals(policy.Key, currentBinding?.SecurityPolicyKey, StringComparison.OrdinalIgnoreCase));
        var currentWorker = configuration.VirtualOutputWorkerPolicies.FirstOrDefault(policy =>
            string.Equals(policy.Key, device.Hosting?.WorkerPolicyKey, StringComparison.OrdinalIgnoreCase));
        var executionMode = AnsiConsole.Prompt(
            new SelectionPrompt<VirtualOutputExecutionMode>()
                .Title("Worker execution mode:")
                .UseConverter(mode => mode == VirtualOutputExecutionMode.ManagedProcess ? "Managed process" : "External process")
                .AddChoices(Enum.GetValues<VirtualOutputExecutionMode>())
                .HighlightStyle(new Style(Color.Blue))
                .PageSize(5));
        var transport = AnsiConsole.Prompt(
            new SelectionPrompt<VirtualOutputTransport>()
                .Title("Listener transport:")
                .AddChoices(Enum.GetValues<VirtualOutputTransport>()));
        var addresses = AnsiConsole.Ask(
            "Comma-separated bind addresses:",
            currentBinding is null ? "localhost" : string.Join(", ", currentBinding.Addresses));
        var port = AnsiConsole.Ask("Listener port:", currentBinding?.Port ?? 8600);
        var automaticPublicBaseUri = BuildAutomaticPublicBaseUri(transport, addresses, port);
        var overridePublicBaseUri = AnsiConsole.Confirm(
            "Override the automatically derived client-facing base URL?",
            currentBinding is not null && !AreEquivalentRootUris(automaticPublicBaseUri, currentBinding.PublicBaseUri));
        var publicBaseUri = overridePublicBaseUri
            ? AnsiConsole.Ask("Client-facing base URL:", currentBinding?.PublicBaseUri ?? automaticPublicBaseUri)
            : automaticPublicBaseUri;
        var certificateReference = transport == VirtualOutputTransport.Https
            ? AnsiConsole.Ask("Certificate reference:", currentSecurity?.CertificateReference ?? string.Empty)
            : string.Empty;
        var allowedNetworks = AnsiConsole.Ask(
            "Allowed client CIDRs (comma-separated, empty for unrestricted):",
            currentSecurity is null ? string.Empty : string.Join(", ", currentSecurity.AllowedNetworks));
        var maximumConnections = AnsiConsole.Ask("Maximum connections (0 for host default):", currentSecurity?.MaximumConnections ?? 0);
        var requestsPerMinute = AnsiConsole.Ask("Requests per minute per client (0 to disable):", currentSecurity?.RequestsPerMinutePerClient ?? 0);
        var maximumRestarts = AnsiConsole.Ask("Maximum restarts per window:", currentWorker?.MaximumRestarts ?? 5);
        var restartWindow = AnsiConsole.Ask("Restart window in seconds:", currentWorker?.RestartWindowSeconds ?? 300);
        var drainTimeout = AnsiConsole.Ask("Drain timeout in seconds:", currentWorker?.DrainTimeoutSeconds ?? 15);
        var hosting = new VirtualOutputHostingDefinition
        {
            ExecutionMode = executionMode,
            Bindings =
            [
                new VirtualOutputBindingDefinition
                        {
                            Transport = transport,
                            Addresses = SplitList(addresses),
                            Port = port,
                            PublicBaseUri = publicBaseUri.Trim(),
                            SecurityPolicyKey = securityKey
                        }
            ],
            WorkerPolicyKey = workerKey
        };
        var securityPolicy = new VirtualOutputSecurityPolicyDefinition
        {
            Key = securityKey,
            Name = $"{device.Name} listener security",
            CertificateReference = string.IsNullOrWhiteSpace(certificateReference) ? null : certificateReference.Trim(),
            AllowedNetworks = SplitList(allowedNetworks),
            MaximumConnections = maximumConnections,
            RequestsPerMinutePerClient = requestsPerMinute
        };
        var workerPolicy = new VirtualOutputWorkerPolicyDefinition
        {
            Key = workerKey,
            Name = $"{device.Name} worker policy",
            MaximumRestarts = maximumRestarts,
            RestartWindowSeconds = restartWindow,
            DrainTimeoutSeconds = drainTimeout,
            LeaseGracePeriodSeconds = currentWorker?.LeaseGracePeriodSeconds ?? 15,
            MaximumOutstandingBrokerCalls = currentWorker?.MaximumOutstandingBrokerCalls ?? 32,
            MaximumMemoryMib = currentWorker?.MaximumMemoryMib ?? 0,
            MaximumCpuPercent = currentWorker?.MaximumCpuPercent ?? 0
        };

        await settings.UpdateAsync(value => value.Streaming = value.Streaming with
        {
            VirtualDevices = value.Streaming.VirtualDevices
                .Select(candidate => candidate.Id == device.Id
                    ? candidate with
                    {
                        Adapter = ApplyDedicatedHosting(candidate.Adapter, transport, publicBaseUri),
                        Hosting = hosting
                    }
                    : candidate)
                .ToArray(),
            VirtualOutputSecurityPolicies =
            [
                .. value.Streaming.VirtualOutputSecurityPolicies.Where(policy =>
                            !string.Equals(policy.Key, securityKey, StringComparison.OrdinalIgnoreCase)),
                        securityPolicy
            ],
            VirtualOutputWorkerPolicies =
            [
                .. value.Streaming.VirtualOutputWorkerPolicies.Where(policy =>
                            !string.Equals(policy.Key, workerKey, StringComparison.OrdinalIgnoreCase)),
                        workerPolicy
            ]
        });
    }

    /// <summary>Aligns Lineup Link protocol transport settings with its dedicated listener.</summary>
    internal static AdapterConfiguration ApplyDedicatedHosting(AdapterConfiguration adapter, VirtualOutputTransport transport, string publicBaseUri)
    {
        if (!string.Equals(adapter.Kind, LineupLinkProtocol.AdapterKind, StringComparison.OrdinalIgnoreCase))
        {
            return adapter;
        }

        var configuration = adapter.Configuration.Deserialize<LineupLinkVirtualConfiguration>()
            ?? throw new InvalidOperationException("Lineup Link virtual configuration is empty.");
        var requireEncryption = transport == VirtualOutputTransport.Https;
        return new AdapterConfiguration(adapter.Kind, JsonSerializer.SerializeToElement(configuration with
        {
            PublicBaseUrl = publicBaseUri.Trim(),
            RequireEncryption = requireEncryption,
            CertificateSha256 = requireEncryption ? configuration.CertificateSha256 : null
        }));
    }

    /// <summary>Builds a client-facing URL from one dedicated listener's transport, first address, and port.</summary>
    internal static string BuildAutomaticPublicBaseUri(VirtualOutputTransport transport, string addresses, int port)
    {
        var address = SplitList(addresses).FirstOrDefault() ?? "localhost";
        if (address is "*" or "+" or "0.0.0.0" or "::" or "[::]")
        {
            address = "localhost";
        }
        var scheme = transport == VirtualOutputTransport.Https ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        return new UriBuilder(scheme, address, port).Uri.AbsoluteUri;
    }

    private static bool AreEquivalentRootUris(string first, string second) =>
        Uri.TryCreate(first, UriKind.Absolute, out var firstUri) &&
        Uri.TryCreate(second, UriKind.Absolute, out var secondUri) &&
        firstUri.AbsoluteUri.Equals(secondUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);

    private static string GetVirtualOutputPolicyKey(VirtualDeviceId deviceId, string suffix) =>
        $"virtual-{deviceId.Value:N}-{suffix}";

    private static IReadOnlyList<string> SplitList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static async Task AddPhysicalAsync(ILineupSettingsService settings, IReadOnlyList<IPhysicalTunerDriver> drivers)
    {
        var driver = SelectDriver(drivers.Where(driver => driver.SupportsUserCreation), driver => driver.DisplayName, "physical device type");
        if (driver is null)
        {
            return;
        }

        var id = PhysicalDeviceId.New();
        var configuration = await EditConfigurationAsync(driver, driver.CreateDefaultConfiguration(id));
        if (configuration is null)
        {
            return;
        }

        var name = AnsiConsole.Ask("Device name:", driver.DisplayName);
        await settings.UpdateAsync(value => value.Streaming = value.Streaming with
        {
            PhysicalDevices =
            [
                .. value.Streaming.PhysicalDevices,
                new PhysicalDeviceDefinition
                {
                    Id = id,
                    Name = name.Trim(),
                    Order = value.Streaming.PhysicalDevices.Count,
                    Adapter = configuration
                }
            ]
        });
    }

    private static async Task AddVirtualAsync(ILineupSettingsService settings, IReadOnlyList<IVirtualTunerDriver> drivers)
    {
        var driver = SelectDriver(drivers.Where(driver => driver.SupportsUserCreation), driver => driver.DisplayName, "virtual device type");
        if (driver is null)
        {
            return;
        }

        var id = VirtualDeviceId.New();
        var configuration = await EditConfigurationAsync(driver, driver.CreateDefaultConfiguration(id));
        if (configuration is null)
        {
            return;
        }

        var name = AnsiConsole.Ask("Virtual device name:", driver.DisplayName);
        await settings.UpdateAsync(value => value.Streaming = value.Streaming with
        {
            VirtualDevices =
            [
                .. value.Streaming.VirtualDevices,
                new VirtualDeviceDefinition { Id = id, Name = name.Trim(), Adapter = configuration }
            ]
        });
    }

    private static async Task AddGuideAsync(ILineupSettingsService settings, IReadOnlyList<IGuideSourceDriver> drivers)
    {
        var driver = SelectDriver(drivers.Where(driver => driver.SupportsUserCreation), driver => driver.DisplayName, "guide source type");
        if (driver is null)
        {
            return;
        }

        var id = GuideSourceId.New();
        var configuration = await EditConfigurationAsync(driver, driver.CreateDefaultConfiguration(id));
        if (configuration is null)
        {
            return;
        }

        var name = AnsiConsole.Ask("Guide source name:", driver.DisplayName);
        await settings.UpdateAsync(value => value.Streaming = value.Streaming with
        {
            GuideSources =
            [
                .. value.Streaming.GuideSources,
                new GuideSourceDefinition { Id = id, Name = name.Trim(), Adapter = configuration }
            ]
        });
    }

    private static async Task AddGuideOutputAsync(ILineupSettingsService settings, IReadOnlyList<IGuideOutputDriver> drivers)
    {
        var driver = SelectDriver(drivers.Where(driver => driver.SupportsUserCreation), driver => driver.DisplayName, "guide output type");
        if (driver is null)
        {
            return;
        }

        var id = GuideOutputId.New();
        var defaultName = $"{driver.DisplayName} {settings.Settings.Streaming.GuideOutputs.Count(output => string.Equals(output.Adapter.Kind, driver.Kind, StringComparison.OrdinalIgnoreCase)) + 1}";
        var name = AnsiConsole.Ask("Guide output name:", defaultName).Trim();
        var key = GuideOutputKey.CreateUnique(name, settings.Settings.Streaming.GuideOutputs.Select(output => output.Key));
        var defaultOutputDirectory = Path.GetDirectoryName(Path.GetFullPath(settings.Settings.XmltvOutputPath))
            ?? Path.GetFullPath(AppConstants.DefaultGuideOutputDirectoryPath);
        var configuration = await EditConfigurationAsync(
            driver,
            driver.CreateDefaultConfiguration(
                id,
                new GuideOutputCreationContext
                {
                    DefaultOutputDirectoryPath = defaultOutputDirectory,
                    OutputKey = key
                }));
        if (configuration is null)
        {
            return;
        }

        await settings.UpdateAsync(value => value.Streaming = value.Streaming with
        {
            GuideOutputs =
            [
                .. value.Streaming.GuideOutputs,
                new GuideOutputDefinition { Id = id, Name = name, Key = key, Adapter = configuration }
            ]
        });
    }

    private static async Task EditAsync(
        ILineupSettingsService settings,
        IReadOnlyList<IPhysicalTunerDriver> physicalDrivers,
        IReadOnlyList<IVirtualTunerDriver> virtualDrivers,
        IReadOnlyList<IGuideSourceDriver> guideDrivers,
        IReadOnlyList<IGuideOutputDriver> guideOutputDrivers)
    {
        var item = SelectConfigured(settings.Settings.Streaming);
        if (item is null)
        {
            return;
        }

        AdapterConfiguration? configuration = item.Category switch
        {
            AdapterCategory.Physical => await EditConfigurationAsync(
                physicalDrivers.Single(driver => string.Equals(driver.Kind, item.Configuration.Kind, StringComparison.OrdinalIgnoreCase)),
                item.Configuration),
            AdapterCategory.Virtual => await EditConfigurationAsync(
                virtualDrivers.Single(driver => string.Equals(driver.Kind, item.Configuration.Kind, StringComparison.OrdinalIgnoreCase)),
                item.Configuration),
            AdapterCategory.Guide => await EditConfigurationAsync(
                guideDrivers.Single(driver => string.Equals(driver.Kind, item.Configuration.Kind, StringComparison.OrdinalIgnoreCase)),
                item.Configuration),
            _ => await EditConfigurationAsync(
                guideOutputDrivers.Single(driver => string.Equals(driver.Kind, item.Configuration.Kind, StringComparison.OrdinalIgnoreCase)),
                item.Configuration)
        };
        if (configuration is null)
        {
            return;
        }

        await settings.UpdateAsync(value =>
        {
            value.Streaming = item.Category switch
            {
                AdapterCategory.Physical => value.Streaming with
                {
                    PhysicalDevices = value.Streaming.PhysicalDevices
                        .Select(device => device.Id.Value == item.Id ? device with { Adapter = configuration } : device)
                        .ToArray()
                },
                AdapterCategory.Virtual => value.Streaming with
                {
                    VirtualDevices = value.Streaming.VirtualDevices
                        .Select(device => device.Id.Value == item.Id ? device with { Adapter = configuration } : device)
                        .ToArray()
                },
                AdapterCategory.Guide => value.Streaming with
                {
                    GuideSources = value.Streaming.GuideSources
                        .Select(source => source.Id.Value == item.Id ? source with { Adapter = configuration } : source)
                        .ToArray()
                },
                _ => value.Streaming with
                {
                    GuideOutputs = value.Streaming.GuideOutputs
                        .Select(output => output.Id.Value == item.Id ? output with { Adapter = configuration } : output)
                        .ToArray()
                }
            };
        });
    }

    private static async Task RemoveAsync(ILineupSettingsService settings)
    {
        var item = SelectConfigured(settings.Settings.Streaming);
        if (item is null || !item.CanRemove || !AnsiConsole.Confirm($"Remove {Markup.Escape(item.Name)}?", defaultValue: false))
        {
            return;
        }

        var updated = item.Category switch
        {
            AdapterCategory.Physical => RemovePhysicalDevice(settings.Settings.Streaming, new PhysicalDeviceId(item.Id)),
            AdapterCategory.Virtual => RemoveVirtualDevice(settings.Settings.Streaming, new VirtualDeviceId(item.Id)),
            AdapterCategory.Guide => RemoveGuideSource(settings.Settings.Streaming, new GuideSourceId(item.Id)),
            _ => RemoveGuideOutput(settings.Settings.Streaming, new GuideOutputId(item.Id))
        };
        var errors = StreamingConfigurationValidator.Validate(updated);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            }
            return;
        }

        await settings.UpdateAsync(value => value.Streaming = updated);
    }

    private static async Task ManageAccessGrantsAsync(
            ILineupSettingsService settings,
            IReadOnlyList<IVirtualTunerDriver> virtualDrivers)
    {
        while (true)
        {
            var candidates = settings.Settings.Streaming.VirtualDevices
                .Select(device => new
                {
                    Device = device,
                    Provider = virtualDrivers
                        .SingleOrDefault(driver => string.Equals(driver.Kind, device.Adapter.Kind, StringComparison.OrdinalIgnoreCase))
                        as IAdapterAccessGrantProvider
                })
                .Where(item => item.Provider is not null)
                .ToArray();
            if (candidates.Length == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No configured adapters expose access grants.[/]");
                return;
            }

            var selectedName = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Select an adapter:")
                    .AddChoices(candidates.Select(item => item.Device.Name).Append("Back")));
            if (selectedName == "Back")
            {
                return;
            }

            var selected = candidates.First(item => string.Equals(item.Device.Name, selectedName, StringComparison.Ordinal));
            var grants = selected.Provider!.GetAccessGrants(selected.Device.Adapter);
            if (grants.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No access grants. Remote access is disabled until a grant is created.[/]");
            }
            foreach (var grant in grants)
            {
                var status = grant.RevokedAtUtc.HasValue
                    ? "revoked"
                    : grant.ExpiresAtUtc <= DateTime.UtcNow
                        ? "expired"
                        : "active";
                AnsiConsole.MarkupLine(
                    $"[blue]{Markup.Escape(grant.Name)}[/] ({grant.Id:D}) - {status}");
            }

            var action = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("Access-grant action:")
                    .AddChoices(["Create", "Rotate", "Revoke", "Purge", "Back"]));
            if (action == "Back")
            {
                continue;
            }

            AdapterAccessGrantMutationResult mutation;
            if (action == "Create")
            {
                var name = AnsiConsole.Ask<string>("Consumer name:");
                var expirationText = AnsiConsole.Prompt(
                    new TextPrompt<string>("Expiration in local time (optional):").AllowEmpty());
                DateTime? expirationUtc = null;
                if (!string.IsNullOrWhiteSpace(expirationText))
                {
                    if (!DateTime.TryParse(expirationText, out var expirationLocal))
                    {
                        AnsiConsole.MarkupLine("[red]Expiration is not a valid date and time.[/]");
                        continue;
                    }
                    expirationUtc = DateTime.SpecifyKind(expirationLocal, DateTimeKind.Local).ToUniversalTime();
                }
                mutation = selected.Provider.CreateAccessGrant(
                    selected.Device.Adapter,
                    name,
                    expirationUtc,
                    DateTime.UtcNow);
            }
            else
            {
                if (grants.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]This adapter has no access grants.[/]");
                    continue;
                }
                var selectableGrants = action == "Purge"
                    ? grants.Where(grant => grant.RevokedAtUtc.HasValue).ToArray()
                    : grants;
                if (selectableGrants.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]This adapter has no revoked access grants to purge.[/]");
                    continue;
                }
                var grantName = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title($"Select a grant to {action.ToLowerInvariant()}:")
                        .AddChoices(selectableGrants.Select(grant => $"{grant.Name} ({grant.Id:D})")));
                var grant = selectableGrants.Single(candidate => grantName.EndsWith($"({candidate.Id:D})", StringComparison.Ordinal));
                var confirmation = action == "Purge"
                    ? $"Permanently purge {Markup.Escape(grant.Name)}? This cannot be undone."
                    : $"{action} {Markup.Escape(grant.Name)}?";
                if (!AnsiConsole.Confirm(confirmation, defaultValue: false))
                {
                    continue;
                }
                mutation = action switch
                {
                    "Rotate" => selected.Provider.RotateAccessGrant(selected.Device.Adapter, grant.Id, DateTime.UtcNow),
                    "Revoke" => selected.Provider.RevokeAccessGrant(selected.Device.Adapter, grant.Id, DateTime.UtcNow),
                    _ => selected.Provider.PurgeAccessGrant(selected.Device.Adapter, grant.Id)
                };
            }

            if (!mutation.Succeeded)
            {
                foreach (var error in mutation.Errors)
                {
                    AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
                }
                continue;
            }

            await settings.UpdateAsync(value => value.Streaming = value.Streaming with
            {
                VirtualDevices = value.Streaming.VirtualDevices
                    .Select(device => device.Id == selected.Device.Id ? device with { Adapter = mutation.Configuration! } : device)
                    .ToArray()
            });
            if (!string.IsNullOrWhiteSpace(mutation.GeneratedSecret))
            {
                DisplayGeneratedLineupLinkBundle(selected.Device with { Adapter = mutation.Configuration! }, mutation.GeneratedSecret);
            }
            else
            {
                AnsiConsole.MarkupLine(action == "Purge"
                    ? "[green]The revoked access grant was permanently purged.[/]"
                    : "[green]The access grant was revoked.[/]");
            }
        }
    }

    private static void DisplayGeneratedLineupLinkBundle(
        VirtualDeviceDefinition device,
        string generatedSecret)
    {
        if (!string.Equals(device.Adapter.Kind, LineupLinkProtocol.AdapterKind, StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"[yellow]Generated token (shown once):[/] {Markup.Escape(generatedSecret)}");
            return;
        }

        var configuration = device.Adapter.Configuration.Deserialize<LineupLinkVirtualConfiguration>()
            ?? throw new InvalidOperationException("Lineup Link virtual configuration is empty.");
        var bundle = LineupLinkConnectionBundleCodec.Serialize(new LineupLinkConnectionBundle
        {
            BaseUrl = string.IsNullOrWhiteSpace(configuration.PublicBaseUrl) ? null : configuration.PublicBaseUrl,
            VirtualDeviceId = device.Id.Value,
            AccessToken = generatedSecret,
            TransportSecurity = configuration.RequireEncryption ? LineupLinkTransportSecurity.Https : LineupLinkTransportSecurity.Http,
            CertificateSha256 = configuration.CertificateSha256
        });
        AnsiConsole.MarkupLine("[yellow]Connection bundle (shown once):[/]");
        AnsiConsole.WriteLine(bundle);
    }

    /// <summary>Removes a physical device and every selected subscription that references it.</summary>
    internal static StreamingConfiguration RemovePhysicalDevice(StreamingConfiguration configuration, PhysicalDeviceId id) =>
        configuration with
        {
            PhysicalDevices = configuration.PhysicalDevices.Where(device => device.Id != id).ToArray(),
            VirtualDevices = configuration.VirtualDevices
                .Select(device => device with
                {
                    Subscription = device.Subscription with
                    {
                        SelectedDeviceIds = device.Subscription.SelectedDeviceIds.Where(selected => selected != id).ToHashSet()
                    }
                })
                .ToArray(),
            GuideSources = configuration.GuideSources
                .Select(source => source with
                {
                    Subscription = source.Subscription with
                    {
                        SelectedDeviceIds = source.Subscription.SelectedDeviceIds.Where(selected => selected != id).ToHashSet()
                    }
                })
                .ToArray()
        };

    /// <summary>Removes a virtual device and every Guide Output selection that references it.</summary>
    internal static StreamingConfiguration RemoveVirtualDevice(StreamingConfiguration configuration, VirtualDeviceId id) =>
        configuration with
        {
            VirtualDevices = configuration.VirtualDevices.Where(device => device.Id != id).ToArray(),
            GuideOutputs = configuration.GuideOutputs
                .Select(output => output with
                {
                    SelectedVirtualDeviceIds = output.SelectedVirtualDeviceIds.Where(selected => selected != id).ToHashSet()
                })
                .ToArray()
        };

    /// <summary>Removes a guide source and every Guide Output selection that references it.</summary>
    internal static StreamingConfiguration RemoveGuideSource(StreamingConfiguration configuration, GuideSourceId id) =>
        configuration with
        {
            GuideSources = configuration.GuideSources.Where(source => source.Id != id).ToArray(),
            GuideOutputs = configuration.GuideOutputs
                .Select(output => output with
                {
                    SelectedGuideSourceIds = output.SelectedGuideSourceIds.Where(selected => selected != id).ToHashSet()
                })
                .ToArray()
        };

    /// <summary>Removes one custom Guide Output.</summary>
    internal static StreamingConfiguration RemoveGuideOutput(StreamingConfiguration configuration, GuideOutputId id) =>
        configuration with
        {
            GuideOutputs = configuration.GuideOutputs.Where(output => output.Id != id || output.IsBuiltIn).ToArray()
        };

    private static ConfiguredAdapter? SelectConfigured(StreamingConfiguration configuration)
    {
        var items = configuration.PhysicalDevices
            .Select(device => new ConfiguredAdapter(AdapterCategory.Physical, device.Id.Value, $"Physical: {device.Name}", device.Adapter, true))
            .Concat(configuration.VirtualDevices.Select(device =>
                new ConfiguredAdapter(AdapterCategory.Virtual, device.Id.Value, $"Virtual: {device.Name}", device.Adapter, true)))
            .Concat(configuration.GuideSources.Select(source =>
                new ConfiguredAdapter(AdapterCategory.Guide, source.Id.Value, $"Guide: {source.Name}", source.Adapter, true)))
            .Concat(configuration.GuideOutputs.Select(output =>
                new ConfiguredAdapter(AdapterCategory.GuideOutput, output.Id.Value, $"Output: {output.Name}", output.Adapter, !output.IsBuiltIn)))
            .ToArray();
        return items.Length == 0
            ? null
            : AnsiConsole.Prompt(new SelectionPrompt<ConfiguredAdapter>().Title("Select an adapter:").UseConverter(item => item.Name).AddChoices(items));
    }

    private static TDriver? SelectDriver<TDriver>(IEnumerable<TDriver> source, Func<TDriver, string> getName, string title)
        where TDriver : class
    {
        var drivers = source.OrderBy(getName, StringComparer.OrdinalIgnoreCase).ToArray();
        if (drivers.Length == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No {Markup.Escape(title)} is available.[/]");
            return null;
        }

        return AnsiConsole.Prompt(
            new SelectionPrompt<TDriver>()
                .Title($"Select a {title}:")
                .UseConverter(getName)
                .AddChoices(drivers));
    }

    private static async Task<AdapterConfiguration?> EditConfigurationAsync(object driver, AdapterConfiguration configuration)
    {
        if (driver is not IAdapterSettingsProvider provider)
        {
            AnsiConsole.MarkupLine("[red]This plugin does not expose portable settings.[/]");
            return null;
        }

        var descriptorErrors = AdapterSettingsValidator.ValidateDescriptor(provider.SettingsDescriptor);
        if (descriptorErrors.Count > 0)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(string.Join(Environment.NewLine, descriptorErrors))}[/]");
            return null;
        }

        var snapshot = provider.GetSettings(configuration);
        var current = snapshot.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        var edits = new List<AdapterSettingEdit>();
        foreach (var field in provider.SettingsDescriptor.Fields)
        {
            if (!IsVisible(field, current))
            {
                continue;
            }

            var edit = Prompt(field, current.GetValueOrDefault(field.Key));
            if (edit is not null)
            {
                edits.Add(edit);
                ApplyEditToSnapshot(current, edit);
            }
        }

        var result = provider.ApplySettings(configuration, edits);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors.SelectMany(pair => pair.Value.Select(message => $"{pair.Key}: {message}")))
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            }
            return null;
        }

        foreach (var secret in result.GeneratedSecrets)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(secret.Key)} (shown once):[/] {Markup.Escape(secret.Value)}");
        }

        await Task.CompletedTask;
        return result.Configuration;
    }

    /// <summary>Updates the working settings snapshot so later visibility conditions observe earlier edits.</summary>
    internal static void ApplyEditToSnapshot(
        IDictionary<string, AdapterSettingSnapshot> current,
        AdapterSettingEdit edit)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Action == AdapterSettingEditAction.Preserve)
        {
            return;
        }

        current[edit.Key] = edit.Action switch
        {
            AdapterSettingEditAction.Set => new AdapterSettingSnapshot
            {
                Key = edit.Key,
                Value = edit.Value?.Clone(),
                HasValue = edit.Value.HasValue
            },
            AdapterSettingEditAction.Generate => new AdapterSettingSnapshot
            {
                Key = edit.Key,
                HasValue = true
            },
            AdapterSettingEditAction.Clear => new AdapterSettingSnapshot
            {
                Key = edit.Key,
                HasValue = false
            },
            _ => throw new ArgumentOutOfRangeException(nameof(edit), edit.Action, "Unsupported adapter setting edit action.")
        };
    }

    private static AdapterSettingEdit? Prompt(AdapterSettingDescriptor field, AdapterSettingSnapshot? current)
    {
        if (field.Kind == AdapterSettingKind.GeneratedSecret)
        {
            return AnsiConsole.Confirm($"Generate a new {field.Label}?", defaultValue: !current?.HasValue ?? true)
                ? new AdapterSettingEdit { Key = field.Key, Action = AdapterSettingEditAction.Generate }
                : new AdapterSettingEdit { Key = field.Key, Action = AdapterSettingEditAction.Preserve };
        }

        if (field.Kind == AdapterSettingKind.Boolean)
        {
            var currentValue = current?.Value is JsonElement booleanValue &&
                booleanValue.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                booleanValue.GetBoolean();
            return new AdapterSettingEdit
            {
                Key = field.Key,
                Action = AdapterSettingEditAction.Set,
                Value = JsonSerializer.SerializeToElement(AnsiConsole.Confirm(field.Label, currentValue))
            };
        }

        if (field.Kind == AdapterSettingKind.Choice)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<AdapterSettingChoice>()
                    .Title(field.Label)
                    .UseConverter(value => value.Label)
                    .AddChoices(field.Choices));
            return Set(field.Key, choice.Value);
        }

        var existing = current?.Value is JsonElement value && value.ValueKind != JsonValueKind.Null ? value.ToString() : string.Empty;
        var prompt = new TextPrompt<string>($"{field.Label}:").AllowEmpty();
        if (field.Kind == AdapterSettingKind.Secret)
        {
            prompt.Secret();
        }
        else if (!string.IsNullOrWhiteSpace(existing))
        {
            prompt.DefaultValue(existing);
        }

        var submitted = AnsiConsole.Prompt(prompt).Trim();
        if (string.IsNullOrEmpty(submitted))
        {
            return current?.HasValue == true
                ? new AdapterSettingEdit { Key = field.Key, Action = AdapterSettingEditAction.Preserve }
                : field.CanClear
                    ? new AdapterSettingEdit { Key = field.Key, Action = AdapterSettingEditAction.Clear }
                    : null;
        }

        if (field.Kind == AdapterSettingKind.Integer && long.TryParse(submitted, out var integer))
        {
            return new AdapterSettingEdit { Key = field.Key, Action = AdapterSettingEditAction.Set, Value = JsonSerializer.SerializeToElement(integer) };
        }

        return Set(field.Key, submitted);
    }

    private static AdapterSettingEdit Set(string key, string value) =>
        new() { Key = key, Action = AdapterSettingEditAction.Set, Value = JsonSerializer.SerializeToElement(value) };

    internal static bool IsVisible(AdapterSettingDescriptor field, IReadOnlyDictionary<string, AdapterSettingSnapshot> current) =>
        field.VisibleWhen.All(condition =>
        {
            var actual = current.GetValueOrDefault(condition.FieldKey)?.Value?.ToString();
            var equals = string.Equals(actual, condition.Value, StringComparison.OrdinalIgnoreCase);
            return condition.Operator == AdapterSettingConditionOperator.Equals ? equals : !equals;
        });

    private enum AdapterCategory
    {
        Physical,
        Virtual,
        Guide,
        GuideOutput
    }

    private sealed record ConfiguredAdapter(
        AdapterCategory Category,
        Guid Id,
        string Name,
        AdapterConfiguration Configuration,
        bool CanRemove);
}
