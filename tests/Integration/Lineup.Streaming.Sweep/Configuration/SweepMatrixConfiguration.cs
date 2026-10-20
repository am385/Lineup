namespace Lineup.Streaming.Sweep;

internal sealed partial class SweepHarness
{
    private async Task GenerateMatrixSettingsAsync(MatrixDefinition matrix)
    {
        var sourcePhysicalId = new PhysicalDeviceId(GuidFor($"{matrix.Id}:source-physical"));
        var sourceGuideId = new GuideSourceId(GuidFor($"{matrix.Id}:source-guide"));
        var sourceVirtualId = new VirtualDeviceId(GuidFor($"{matrix.Id}:source-virtual"));
        var policies = matrix.Mode == HostingMode.Management
            ? (Array.Empty<VirtualOutputSecurityPolicyDefinition>(), Array.Empty<VirtualOutputWorkerPolicyDefinition>())
            : CreatePolicies();
        string? lineupLinkToken = null;
        AdapterConfiguration sourceAdapter;
        if (matrix.Remote)
        {
            (sourceAdapter, lineupLinkToken) = CreateLineupLinkVirtualAdapter(matrix);
        }
        else
        {
            sourceAdapter = new AdapterConfiguration(WebPlayerVirtualTunerDriver.AdapterKind, JsonSerializer.SerializeToElement(new { }));
        }
        var sourceHosting = CreateHosting(matrix, matrix.Remote);
        var sourceSettings = new LineupSettings
        {
            TargetDays = 2,
            AutoFetchInterval = TimeSpan.Zero,
            RefreshChannelsBeforeGuideFetch = true,
            DeviceRefreshIntervalMinutes = 0,
            TunerRefreshIntervalSeconds = 2,
            AutoGenerateXmltv = false,
            XmltvOutputPath = "/guide/xmltv.xml",
            Streaming = new StreamingConfiguration
            {
                PhysicalDevices =
                [
                    new PhysicalDeviceDefinition
                    {
                        Id = sourcePhysicalId,
                        Name = "HDHomeRun FLEX 4K",
                        Adapter = new AdapterConfiguration(
                            HDHomeRunPhysicalTunerDriver.DriverKind,
                            JsonSerializer.SerializeToElement(new HDHomeRunPhysicalTunerConfiguration { Address = GetTunerAddress() }))
                    }
                ],
                GuideSources =
                [
                    new GuideSourceDefinition
                    {
                        Id = sourceGuideId,
                        Name = "SiliconDust Guide",
                        Adapter = new AdapterConfiguration(
                            SiliconDustGuideSourceDriver.DriverKind,
                            JsonSerializer.SerializeToElement(new { })),
                        Subscription = Selected(sourcePhysicalId)
                    }
                ],
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = sourceVirtualId,
                        Name = matrix.Remote ? "Sweep Lineup Link" : WebPlayerVirtualTunerDriver.BuiltInName,
                        Adapter = sourceAdapter,
                        Subscription = Selected(sourcePhysicalId),
                        Hosting = sourceHosting
                    }
                ],
                VirtualOutputSecurityPolicies = policies.Item1,
                VirtualOutputWorkerPolicies = policies.Item2
            }
        };
        var sourceTemplate = Path.Combine(MatrixRoot(matrix), "templates", "source");
        await WriteSettingsAsync(sourceTemplate, sourceSettings);

        if (!matrix.Remote)
        {
            return;
        }

        var consumerPhysicalId = new PhysicalDeviceId(GuidFor($"{matrix.Id}:consumer-physical"));
        var consumerGuideId = new GuideSourceId(GuidFor($"{matrix.Id}:consumer-guide"));
        var consumerVirtualId = new VirtualDeviceId(GuidFor($"{matrix.Id}:consumer-virtual"));
        var consumerSettings = new LineupSettings
        {
            TargetDays = 2,
            AutoFetchInterval = TimeSpan.Zero,
            RefreshChannelsBeforeGuideFetch = true,
            DeviceRefreshIntervalMinutes = 0,
            TunerRefreshIntervalSeconds = 2,
            AutoGenerateXmltv = false,
            XmltvOutputPath = "/guide/xmltv.xml",
            Streaming = new StreamingConfiguration
            {
                PhysicalDevices =
                [
                    new PhysicalDeviceDefinition
                    {
                        Id = consumerPhysicalId,
                        Name = "Remote Lineup Link",
                        Adapter = new AdapterConfiguration(
                            LineupLinkProtocol.AdapterKind,
                            JsonSerializer.SerializeToElement(new LineupLinkPhysicalConfiguration
                            {
                                BaseUrl = SourceInternalBaseUrl(matrix),
                                VirtualDeviceId = sourceVirtualId.Value,
                                AccessToken = lineupLinkToken!,
                                TransportSecurity = LineupLinkTransportSecurity.Http
                            }))
                    }
                ],
                GuideSources =
                [
                    new GuideSourceDefinition
                    {
                        Id = consumerGuideId,
                        Name = "Remote Lineup Link Guide",
                        Adapter = new AdapterConfiguration(
                            LineupLinkProtocol.AdapterKind,
                            JsonSerializer.SerializeToElement(new { })),
                        Subscription = Selected(consumerPhysicalId)
                    }
                ],
                VirtualDevices =
                [
                    new VirtualDeviceDefinition
                    {
                        Id = consumerVirtualId,
                        Name = WebPlayerVirtualTunerDriver.BuiltInName,
                        Adapter = new AdapterConfiguration(
                            WebPlayerVirtualTunerDriver.AdapterKind,
                            JsonSerializer.SerializeToElement(new { })),
                        Subscription = Selected(consumerPhysicalId)
                    }
                ]
            }
        };
        await WriteSettingsAsync(Path.Combine(MatrixRoot(matrix), "templates", "consumer"), consumerSettings);
    }

    private (AdapterConfiguration Adapter, string Token) CreateLineupLinkVirtualAdapter(MatrixDefinition matrix)
    {
        var grantId = GuidFor($"{matrix.Id}:grant");
        var token = LineupLinkToken.Generate(grantId);
        var configuration = new LineupLinkVirtualConfiguration
        {
            InstanceId = GuidFor($"{matrix.Id}:instance"),
            PublicBaseUrl = SourceInternalBaseUrl(matrix),
            RequireEncryption = false,
            AccessGrants =
            [
                new LineupLinkAccessGrant
                {
                    Id = grantId,
                    Name = "Overnight sweep",
                    TokenHash = token.TokenHash,
                    CreatedAtUtc = DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc)
                }
            ]
        };
        return (
            new AdapterConfiguration(LineupLinkProtocol.AdapterKind, JsonSerializer.SerializeToElement(configuration)),
            token.Token);
    }

    private VirtualOutputHostingDefinition? CreateHosting(MatrixDefinition matrix, bool remote)
    {
        if (matrix.Mode == HostingMode.Management)
        {
            return null;
        }
        var publicBaseUri = remote
            ? "http://source:8600/"
            : $"http://127.0.0.1:{matrix.OutputPort}/";
        return new VirtualOutputHostingDefinition
        {
            ExecutionMode = matrix.Mode == HostingMode.ManagedProcess
                ? VirtualOutputExecutionMode.ManagedProcess
                : VirtualOutputExecutionMode.ExternalProcess,
            WorkerPolicyKey = "sweep-worker",
            Bindings =
            [
                new VirtualOutputBindingDefinition
                {
                    Transport = VirtualOutputTransport.Http,
                    Addresses = ["*"],
                    Port = 8600,
                    PublicBaseUri = publicBaseUri,
                    SecurityPolicyKey = "sweep-http"
                }
            ]
        };
    }

    private static (VirtualOutputSecurityPolicyDefinition[], VirtualOutputWorkerPolicyDefinition[]) CreatePolicies() =>
    (
        [
            new VirtualOutputSecurityPolicyDefinition
            {
                Key = "sweep-http",
                Name = "Sweep HTTP",
                ClientCertificateMode = VirtualOutputClientCertificateMode.None
            }
        ],
        [
            new VirtualOutputWorkerPolicyDefinition
            {
                Key = "sweep-worker",
                Name = "Sweep worker",
                MaximumRestarts = 5,
                RestartWindowSeconds = 300,
                DrainTimeoutSeconds = 15,
                LeaseGracePeriodSeconds = 15,
                MaximumOutstandingBrokerCalls = 32
            }
        ]
    );

    private static PhysicalDeviceSubscription Selected(PhysicalDeviceId id) => new()
    {
        Mode = PhysicalDeviceSubscriptionMode.Selected,
        SelectedDeviceIds = [id]
    };

    private async Task WriteSettingsAsync(string directory, LineupSettings settings)
    {
        Directory.CreateDirectory(directory);
        var settingsPath = Path.Combine(directory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings, _json));
        await File.WriteAllTextAsync(
            Path.Combine(directory, "web-settings.json"),
            JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Theme = "dark",
                UiCulture = "en",
                ActiveStreamRefreshIntervalSeconds = 1,
                RedactApiClientAddresses = false,
                RedactApiDeviceAddresses = false,
                RedactApiUrls = false,
                IsSetupComplete = true,
                EnableNetworkDiscovery = false,
                EnableFileLogging = true,
                OverrideLoggingDefaults = true,
                ApplicationLogLevel = "Information",
                FileLogLevel = "Debug",
                FileLogRetentionDays = 7,
                FileLogSizeLimitMb = 100
            }, _json));
    }

    private string BuildCompose(MatrixDefinition matrix)
    {
        var sourcePorts = new List<string> { $"      - \"{matrix.SourceManagementPort}:8080\"" };
        if (matrix.OutputPort.HasValue)
        {
            sourcePorts.Add($"      - \"{matrix.OutputPort.Value}:8600\"");
        }
        var builder = new StringBuilder();
        builder.AppendLine("services:");
        builder.AppendLine("  source:");
        builder.AppendLine($"    image: {_image}");
        builder.AppendLine("    restart: \"no\"");
        builder.AppendLine("    ports:");
        foreach (var port in sourcePorts)
        {
            builder.AppendLine(port);
        }
        builder.AppendLine("    volumes:");
        builder.AppendLine("      - ./runtime/source/appdata:/appdata");
        builder.AppendLine("      - ./runtime/source/guide:/guide");
        builder.AppendLine("      - ./runtime/source/transient:/transient");
        builder.AppendLine("    environment:");
        builder.AppendLine("      ASPNETCORE_ENVIRONMENT: Production");
        builder.AppendLine("      ASPNETCORE_HTTP_PORTS: \"\"");
        builder.AppendLine("      Lineup__AppDataPath: /appdata");
        builder.AppendLine("      Lineup__GuideOutputDir: /guide");
        builder.AppendLine("      Lineup__TransientPath: /transient");
        builder.AppendLine("      Lineup__HttpPort: \"8080\"");

        if (matrix.Mode == HostingMode.ExternalProcess)
        {
            var worker = matrix.Remote
                ? "/app/virtual-output-workers/lineup-link/Lineup.LineupLink.VirtualOutputHost.dll"
                : "/app/virtual-output-workers/web-player/Lineup.WebPlayer.VirtualOutputHost.dll";
            var virtualId = GuidFor($"{matrix.Id}:source-virtual").ToString("D");
            builder.AppendLine("  external-worker:");
            builder.AppendLine($"    image: {_image}");
            builder.AppendLine("    restart: on-failure");
            builder.AppendLine("    network_mode: service:source");
            builder.AppendLine("    depends_on:");
            builder.AppendLine("      - source");
            builder.AppendLine("    entrypoint: [\"/bin/sh\", \"-c\"]");
            builder.AppendLine("    command:");
            builder.AppendLine($"      - >-");
            builder.AppendLine($"        while true; do bootstrap=$$(curl -fsS -X POST http://127.0.0.1:8080/api/virtual-outputs/{virtualId}/enrollment 2>/dev/null) && break; sleep 2; done;");
            builder.AppendLine($"        printf '%s\\n' \"$$bootstrap\" | dotnet {worker}");
            builder.AppendLine("    volumes:");
            builder.AppendLine("      - ./runtime/source/transient:/transient");
        }

        if (matrix.Remote)
        {
            builder.AppendLine("  consumer:");
            builder.AppendLine($"    image: {_image}");
            builder.AppendLine("    restart: \"no\"");
            builder.AppendLine("    depends_on:");
            builder.AppendLine("      - source");
            builder.AppendLine("    ports:");
            builder.AppendLine($"      - \"{matrix.ConsumerManagementPort}:8080\"");
            builder.AppendLine("    volumes:");
            builder.AppendLine("      - ./runtime/consumer/appdata:/appdata");
            builder.AppendLine("      - ./runtime/consumer/guide:/guide");
            builder.AppendLine("      - ./runtime/consumer/transient:/transient");
            builder.AppendLine("    environment:");
            builder.AppendLine("      ASPNETCORE_ENVIRONMENT: Production");
            builder.AppendLine("      ASPNETCORE_HTTP_PORTS: \"\"");
            builder.AppendLine("      Lineup__AppDataPath: /appdata");
            builder.AppendLine("      Lineup__GuideOutputDir: /guide");
            builder.AppendLine("      Lineup__TransientPath: /transient");
            builder.AppendLine("      Lineup__HttpPort: \"8080\"");
        }
        return builder.ToString();
    }

    private async Task PrepareAndStartAsync(MatrixDefinition matrix, string project, RunJournal journal)
    {
        await EnsureTunerIdleAsync(TimeSpan.FromMinutes(5));
        var matrixRoot = MatrixRoot(matrix);
        var runtime = Path.Combine(matrixRoot, "runtime");
        if (Directory.Exists(runtime))
        {
            Directory.Delete(runtime, true);
        }
        Directory.CreateDirectory(runtime);
        foreach (var service in matrix.Remote ? new[] { "source", "consumer" } : new[] { "source" })
        {
            CopyDirectory(
                Path.Combine(matrixRoot, "templates", service),
                Path.Combine(runtime, service, "appdata"));
            Directory.CreateDirectory(Path.Combine(runtime, service, "guide"));
            Directory.CreateDirectory(Path.Combine(runtime, service, "transient"));
        }
        await RunProcessAsync("docker", $"compose -p {project} -f \"{Path.Combine(matrixRoot, "compose.yml")}\" up -d --remove-orphans", matrixRoot);
        await WaitReadyAsync(matrix.SourceUrl, TimeSpan.FromMinutes(3));
        if (matrix.Remote)
        {
            await WaitReadyAsync(matrix.WatchUrl, TimeSpan.FromMinutes(3));
        }
        if (matrix.Mode != HostingMode.Management)
        {
            await WaitWorkerHealthyAsync(matrix.SourceUrl, GuidFor($"{matrix.Id}:source-virtual"), TimeSpan.FromMinutes(3));
        }
        await journal.WriteEventAsync("matrix-ready", new { matrix = matrix.Id, source = matrix.SourceUrl, watch = matrix.WatchUrl });
    }
}
