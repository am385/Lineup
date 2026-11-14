namespace Lineup.Core.Guides;

/// <summary>
/// Resolves configured guide-source subscriptions against compatible active physical devices.
/// </summary>
public static class GuideSourceSubscriptionResolver
{
    /// <summary>Resolves enabled sources independently, retaining explicit diagnostics for each ineligible source.</summary>
    internal static (IReadOnlyList<EligibleGuideSource> Sources, IReadOnlyList<GuideSourceDiagnostic> Diagnostics) ResolveEligibleSources(
        IEnumerable<GuideSourceDefinition> sources,
        IReadOnlyList<PhysicalDeviceDefinition> physicalDevices,
        IStreamingAdapterRegistry adapters,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        ArgumentNullException.ThrowIfNull(adapters);
        cancellationToken.ThrowIfCancellationRequested();
        var eligible = new List<EligibleGuideSource>();
        var diagnostics = new List<GuideSourceDiagnostic>();
        foreach (var source in sources.Where(source => source.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IGuideSourceDriver driver;
            try
            {
                driver = adapters.GetGuide(source.Adapter.Kind);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                diagnostics.Add(Diagnostic(source, GuideSourceDiagnosticCode.AdapterUnavailable,
                    "The guide-source adapter is unavailable. Install or enable its plugin; this source was excluded."));
                continue;
            }

            var scope = ResolveScope(source, physicalDevices, driver, out _);
            if (scope is null)
            {
                diagnostics.Add(Diagnostic(source, GuideSourceDiagnosticCode.InvalidSubscription,
                    "The guide-source subscription could not be resolved. Review its selected physical devices; this source was excluded."));
                continue;
            }
            if (scope.PhysicalDeviceIds.Count == 0)
            {
                diagnostics.Add(Diagnostic(source, GuideSourceDiagnosticCode.NoCompatibleDevices,
                    "The guide source has no compatible enabled physical devices. Enable or select a compatible device; this source was excluded."));
                continue;
            }
            eligible.Add(new EligibleGuideSource(source, driver, scope));
        }
        return (eligible, diagnostics);
    }

    private static GuideSourceDiagnostic Diagnostic(GuideSourceDefinition source, GuideSourceDiagnosticCode code, string message) =>
        new() { SourceId = source.Id, SourceName = source.Name, Code = code, Message = message };

    /// <summary>Resolves one guide source's active physical-device scope.</summary>
    public static GuideSourceScope Resolve(GuideSourceDefinition source, IReadOnlyList<PhysicalDeviceDefinition> physicalDevices, IGuideSourceDriver driver)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(physicalDevices);
        ArgumentNullException.ThrowIfNull(driver);

        return ResolveScope(source, physicalDevices, driver, out var error) ?? throw new InvalidOperationException(error);
    }

    private static GuideSourceScope? ResolveScope(GuideSourceDefinition source, IReadOnlyList<PhysicalDeviceDefinition> physicalDevices, IGuideSourceDriver driver, out string? error)
    {
        error = null;
        var configuredById = physicalDevices.ToDictionary(device => device.Id);
        if (source.Subscription.Mode == PhysicalDeviceSubscriptionMode.Selected)
        {
            foreach (var selectedId in source.Subscription.SelectedDeviceIds)
            {
                if (!configuredById.TryGetValue(selectedId, out var selected))
                {
                    error = $"Guide source '{source.Name}' selects unknown physical device {selectedId}.";
                    return null;
                }

                if (!driver.SupportsPhysicalAdapter(selected.Adapter.Kind))
                {
                    error = $"Guide source '{source.Name}' does not support physical device '{selected.Name}' with adapter kind '{selected.Adapter.Kind}'.";
                    return null;
                }
            }
        }

        var physicalDeviceIds = physicalDevices
            .Where(device => device.Enabled && driver.SupportsPhysicalAdapter(device.Adapter.Kind))
            .Where(device => source.Subscription.Mode == PhysicalDeviceSubscriptionMode.All || source.Subscription.SelectedDeviceIds.Contains(device.Id))
            .OrderBy(device => device.Order)
            .ThenBy(device => device.Id.Value)
            .Select(device => device.Id)
            .ToArray();
        return new GuideSourceScope(source.Id, physicalDeviceIds);
    }
}

/// <summary>Contains one guide source with its resolved driver and eligible physical-device scope.</summary>
internal sealed record EligibleGuideSource(GuideSourceDefinition Definition, IGuideSourceDriver Driver, GuideSourceScope Scope);
