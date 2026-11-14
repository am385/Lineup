namespace Lineup.App.Web.Services;

/// <summary>Summarizes safe operational observations separately from management liveness/readiness.</summary>
internal sealed record OperationalStatusResponse(string Status, string Consistency, DateTime ObservedAtUtc, TopologyApplicationStatusResponse? Topology, IReadOnlyList<OperationalIssueStatusResponse> Issues);

/// <summary>Names an observed condition without copying exception messages, provider payloads, endpoints or archive contents.</summary>
internal sealed record OperationalIssueStatusResponse(string Code, string Section, string? ResourceId, string? ResourceName, DateTime ObservedAtUtc, DateTime? LastSuccessUtc = null);

/// <summary>Correlates the desired configuration with independently observed application revisions.</summary>
internal sealed record TopologyApplicationStatusResponse(string DesiredRevision, string? AttemptedRevision, string? AppliedRevision, string Phase);

/// <summary>Derives reason codes from existing observations and in-memory runtime state without issuing storage or provider calls.</summary>
internal static class OperationalStatusSummary
{
    /// <summary>Builds an eventually consistent summary; timestamps identify observations, not an invented last state-transition time.</summary>
    internal static OperationalStatusResponse Create(
        LineupStatusResponse status,
        LineupSettings settings,
        StreamingTopologyApplication? application,
        IReadOnlyList<GuideSourceRefreshObservation> refreshes,
        DateTime observedAtUtc
    )
    {
        var issues = new List<OperationalIssueStatusResponse>();
        void Add(string code, string section, string? id = null, string? name = null, DateTime? lastSuccess = null, DateTime? observed = null) =>
            issues.Add(new(code, section, id, name, observed ?? observedAtUtc, lastSuccess));

        if (status.Application.StartupStatus == "factoryResetRequired")
        {
            Add("factory_reset_required", "application");
        }
        if (application is null)
        {
            Add("topology_unavailable", "topology");
        }
        else
        {
            var capturedRevision = ConfigurationRevision.Create(settings.Streaming);
            if (application.DesiredRevision != capturedRevision || application.Phase == TopologyApplicationPhase.Applied && application.AppliedRevision != capturedRevision)
            {
                Add("topology_revision_mismatch", "topology");
            }
            else if (application.Phase != TopologyApplicationPhase.Applied)
            {
                Add(application.Phase == TopologyApplicationPhase.Degraded ? "topology_degraded" : "topology_pending", "topology");
            }
            if (application.AttemptedRevision == capturedRevision)
            {
                foreach (var device in application.Devices.Where(device => !device.Succeeded))
                {
                    var definition = settings.Streaming.PhysicalDevices.SingleOrDefault(candidate => candidate.Enabled && candidate.Id == device.DeviceId);
                    if (definition is not null)
                    {
                        Add("physical_lineup_failed", "topology", definition.Id.ToString(), definition.Name);
                    }
                }
                foreach (var output in application.Outputs.Where(output => !output.Succeeded || output.IsPartial))
                {
                    var definition = settings.Streaming.GuideOutputs.SingleOrDefault(candidate => candidate.Enabled && candidate.Id == output.OutputId);
                    if (definition is not null)
                    {
                        Add(output.Succeeded ? "guide_application_partial" : "guide_application_failed", "topology", definition.Id.ToString(), definition.Name);
                    }
                }
            }
        }
        foreach (var definition in settings.Streaming.PhysicalDevices.Where(device => device.Enabled))
        {
            var device = status.PhysicalDevices.SingleOrDefault(device => definition.Id.ToString() == device.PhysicalDeviceId);
            if (device is null)
            {
                Add("physical_device_unavailable", "physicalDevices", definition.Id.ToString(), definition.Name);
            }
            else if (device.Error is not null)
            {
                Add("physical_device_failed", "physicalDevices", device.PhysicalDeviceId, device.Name, device.StatusObservedAtUtc?.UtcDateTime);
            }
        }
        foreach (var definition in settings.Streaming.VirtualDevices.Where(device => device.Enabled &&
            (device.Hosting is not null || status.VirtualOutputWorkers.Any(worker => worker.VirtualDeviceId == device.Id.ToString()))))
        {
            var worker = status.VirtualOutputWorkers.SingleOrDefault(worker => worker.VirtualDeviceId == definition.Id.ToString());
            if (worker is null)
            {
                Add("worker_unavailable", "virtualOutputWorkers", definition.Id.ToString(), definition.Name);
            }
            else if (worker.State != nameof(VirtualOutputRuntimeState.Healthy))
            {
                var code = worker.State switch
                {
                    nameof(VirtualOutputRuntimeState.Failed) => "worker_failed",
                    nameof(VirtualOutputRuntimeState.Degraded) => "worker_degraded",
                    nameof(VirtualOutputRuntimeState.RequiresConfiguration) => "worker_requires_configuration",
                    nameof(VirtualOutputRuntimeState.Disabled) => "worker_disabled",
                    _ => "worker_pending"
                };
                Add(code, "virtualOutputWorkers", worker.VirtualDeviceId, definition.Name, worker.ReadyAtUtc?.UtcDateTime);
            }
        }
        if (status.Guide.SourceImports.Status != "available" && settings.Streaming.GuideSources.Any(source => source.Enabled))
        {
            Add("guide_imports_unavailable", "guide");
        }
        if (status.Guide.Generation?.ConfigurationRevision is { } guideRevision && guideRevision != ConfigurationRevision.Create(settings.Streaming))
        {
            Add("guide_configuration_mismatch", "guide");
        }
        foreach (var source in settings.Streaming.GuideSources.Where(source => source.Enabled))
        {
            var refresh = refreshes.SingleOrDefault(item => item.SourceId == source.Id && item.SourceRevision == ConfigurationRevision.Create(source));
            if (refresh is { Succeeded: false })
            {
                Add("guide_source_failed", "guide", source.Id.ToString(), source.Name, refresh.LastSuccessUtc?.UtcDateTime, refresh.ObservedAtUtc.UtcDateTime);
            }
            var import = status.Guide.SourceImports.Items.SingleOrDefault(item => item.GuideSourceId == source.Id.ToString());
            if (status.Guide.SourceImports.Status == "available" && (import is null || import.CoverageEndUtc <= observedAtUtc))
            {
                Add(import is null ? "guide_source_missing" : "guide_source_expired", "guide", source.Id.ToString(), source.Name, import?.FetchedAtUtc);
            }
        }
        foreach (var diagnostic in status.Guide.SourceDiagnostics)
        {
            Add("guide_source_excluded", "guide", diagnostic.SourceId, diagnostic.SourceName);
        }
        if (status.Guide.AutoFetch.Status == "failed")
        {
            Add("guide_fetch_failed", "guide", lastSuccess: status.Guide.AutoFetch.LastFetchTimeUtc);
        }
        foreach (var output in status.Guide.Outputs.Where(output => output.Enabled))
        {
            if (settings.GuidePublicationRetries.Keys.Any(id => id.ToString("D") == output.GuideOutputId))
            {
                Add("guide_publication_pending", "guideOutputs", output.GuideOutputId, output.Name, output.LastWriteTimeUtc);
            }
            else if (!output.Available || output.Error is not null)
            {
                Add("guide_publication_unavailable", "guideOutputs", output.GuideOutputId, output.Name, output.LastWriteTimeUtc);
            }
        }
        if (status.MediaResources is { } media)
        {
            if (media.MaximumProducers > 0 && media.Producers >= media.MaximumProducers)
            {
                Add("media_producer_limit_reached", "mediaResources");
            }
            if (media.MaximumProcessing > 0 && media.Processing >= media.MaximumProcessing)
            {
                Add("media_processing_limit_reached", "mediaResources");
            }
        }
        if (status.PluginConnections is { } connections && (connections.IsDraining || connections.MaximumConnections > 0 && connections.ActiveConnections >= connections.MaximumConnections))
        {
            Add("plugin_connection_limit_reached", "pluginConnections");
        }
        var summary = issues.Count == 0 ? "healthy" : issues.All(issue => issue.Code.EndsWith("_pending", StringComparison.Ordinal)) ? "pending" : "degraded";
        if (issues.Count > 0 && issues.All(issue => issue.Code.EndsWith("_unavailable", StringComparison.Ordinal)))
        {
            summary = "unknown";
        }
        return new(summary, "eventual", observedAtUtc, application is null ? null :
            new(application.DesiredRevision, application.AttemptedRevision, application.AppliedRevision, application.Phase.ToString().ToLowerInvariant()), issues);
    }
}
