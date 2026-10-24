using Lineup.Common.Streaming;

namespace Lineup.VirtualOutputs.Hosting;

/// <summary>Indexes isolated virtual-output bindings by their unique local listener port.</summary>
public static class VirtualOutputBindingResolver
{
    /// <summary>Creates a local-port binding map and rejects ambiguous duplicate ports.</summary>
    public static IReadOnlyDictionary<int, VirtualOutputBindingDefinition> CreatePortMap(VirtualOutputHostingDefinition hosting)
    {
        ArgumentNullException.ThrowIfNull(hosting);
        var duplicatePort = hosting.Bindings
            .GroupBy(binding => binding.Port)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePort is not null)
        {
            throw new InvalidOperationException($"Virtual-output bindings must use unique local ports; port {duplicatePort.Key} is configured more than once.");
        }
        return hosting.Bindings.ToDictionary(binding => binding.Port);
    }

    /// <summary>Resolves the externally advertised base URI for one active local listener port.</summary>
    public static Uri ResolvePublicBaseUri(
        IReadOnlyDictionary<int, VirtualOutputBindingDefinition> bindingsByPort,
        int localPort)
    {
        ArgumentNullException.ThrowIfNull(bindingsByPort);
        if (!bindingsByPort.TryGetValue(localPort, out var binding))
        {
            throw new InvalidOperationException($"No virtual-output binding owns local port {localPort}.");
        }
        var value = binding.PublicBaseUri;
        return new Uri(value.EndsWith('/') ? value : $"{value}/", UriKind.Absolute);
    }
}
