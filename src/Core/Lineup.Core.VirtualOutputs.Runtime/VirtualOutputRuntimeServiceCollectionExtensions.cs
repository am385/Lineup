using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lineup.Core.VirtualOutputs.Runtime;

/// <summary>Registers the trusted isolated virtual-output control-plane runtime.</summary>
public static class VirtualOutputRuntimeServiceCollectionExtensions
{
    /// <summary>Registers worker supervision, local IPC, capability brokering, and runtime status.</summary>
    public static IServiceCollection AddLineupVirtualOutputRuntime(
        this IServiceCollection services,
        VirtualOutputRuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton<Lineup.Core.Extensibility.Streaming.IVirtualOutputResourcePolicyValidator, VirtualOutputResourcePolicyValidator>();
        services.AddSingleton(options.IpcEndpoint);
        services.AddSingleton<IVirtualOutputSessionAuthority, VirtualOutputSessionAuthority>();
        services.AddSingleton<IVirtualOutputRuntimeStatus, VirtualOutputRuntimeStatus>();
        services.AddSingleton<IVirtualOutputExternalEnrollmentStore, VirtualOutputExternalEnrollmentStore>();
        services.AddSingleton<IVirtualOutputWorkerCatalog>(provider =>
            new VirtualOutputWorkerCatalog(options.WorkerPackageRoot, logger: provider.GetRequiredService<ILogger<VirtualOutputWorkerCatalog>>()));
        services.AddSingleton<IVirtualOutputCertificateResolver, FileVirtualOutputCertificateResolver>();
        services.AddSingleton<IVirtualOutputWorkerLauncher, VirtualOutputWorkerLauncher>();
        services.AddSingleton<IVirtualOutputBroker, VirtualOutputBroker>();
        services.AddHostedService<VirtualOutputIpcServer>();
        services.AddHostedService<VirtualOutputSupervisor>();
        return services;
    }
}
