using Lineup.Core.Streaming.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lineup.Core.Hosting.Streaming;

/// <summary>Registers the provider-neutral shared MPEG-TS streaming graph.</summary>
public static class FfmpegStreamingServiceCollectionExtensions
{
    /// <summary>Registers probing, multiplexing, routing, active-stream accounting, and virtual MPEG-TS output.</summary>
    public static IServiceCollection AddLineupMpegTsStreaming(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider => StreamingResourceOptions.Load(provider.GetService<Microsoft.Extensions.Configuration.IConfiguration>()));
        services.AddSingleton<IMediaProbeService, MediaProbeService>();
        services.AddSingleton<IActiveStreamRegistry, ActiveStreamRegistry>();
        services.AddSingleton<ITunerStreamMultiplexer>(provider => new TunerStreamMultiplexer(
            provider.GetRequiredService<IStreamingSessionGraph>(),
            provider.GetRequiredService<IMediaProcessFactory>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TunerStreamMultiplexer>>(),
            TimeProvider.System,
            provider.GetRequiredService<StreamingResourceOptions>()));
        services.AddSingleton<ISharedStreamShutdown>(provider => provider.GetRequiredService<ITunerStreamMultiplexer>());
        services.AddSingleton<IStreamingRouteResolver, StreamingRouteResolver>();
        services.AddSingleton<IVirtualMpegTsOutputService, VirtualMpegTsOutputService>();
        services.AddSingleton<IMediaProcessFactory, MediaProcessFactory>();
        services.AddSingleton<IChannelSlateStreamFactory, ChannelSlateStreamFactory>();
        return services;
    }
}
