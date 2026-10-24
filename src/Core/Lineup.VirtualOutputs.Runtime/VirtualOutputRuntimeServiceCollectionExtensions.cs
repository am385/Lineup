using Microsoft.Extensions.DependencyInjection;

namespace Lineup.VirtualOutputs.Runtime;

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
        services.AddSingleton(options.IpcEndpoint);
        services.AddSingleton<IVirtualOutputSessionAuthority, VirtualOutputSessionAuthority>();
        services.AddSingleton<IVirtualOutputRuntimeStatus, VirtualOutputRuntimeStatus>();
        services.AddSingleton<IVirtualOutputExternalEnrollmentStore, VirtualOutputExternalEnrollmentStore>();
        services.AddSingleton<IVirtualOutputWorkerCatalog>(_ => new VirtualOutputWorkerCatalog(options.WorkerPackageRoot));
        services.AddSingleton<IVirtualOutputCertificateResolver, FileVirtualOutputCertificateResolver>();
        services.AddSingleton<IVirtualOutputWorkerLauncher, VirtualOutputWorkerLauncher>();
        services.AddSingleton<IVirtualOutputBroker, VirtualOutputBroker>();
        services.AddHostedService<VirtualOutputIpcServer>();
        services.AddHostedService<VirtualOutputSupervisor>();
        return services;
    }
}
