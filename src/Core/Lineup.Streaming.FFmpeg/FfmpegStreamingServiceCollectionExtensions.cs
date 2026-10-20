using Lineup.Core.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Lineup.Streaming.FFmpeg;

/// <summary>Registers the provider-neutral shared MPEG-TS streaming graph.</summary>
public static class FfmpegStreamingServiceCollectionExtensions
{
    /// <summary>Registers probing, multiplexing, routing, active-stream accounting, and virtual MPEG-TS output.</summary>
    public static IServiceCollection AddLineupMpegTsStreaming(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMediaProbeService, MediaProbeService>();
        services.AddSingleton<IActiveStreamRegistry, ActiveStreamRegistry>();
        services.AddSingleton<ITunerStreamMultiplexer, TunerStreamMultiplexer>();
        services.AddSingleton<ISharedStreamShutdown>(provider => provider.GetRequiredService<ITunerStreamMultiplexer>());
        services.AddSingleton<IStreamingRouteResolver, StreamingRouteResolver>();
        services.AddSingleton<IVirtualMpegTsOutputService, VirtualMpegTsOutputService>();
        services.AddSingleton<IMediaProcessFactory, MediaProcessFactory>();
        services.AddSingleton<IChannelSlateStreamFactory, ChannelSlateStreamFactory>();
        return services;
    }
}
