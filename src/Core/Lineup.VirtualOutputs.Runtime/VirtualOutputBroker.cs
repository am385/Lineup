using Lineup.Common.Guides;
using Lineup.Common.Streaming;
using Lineup.Core.Guides;
using Lineup.Core.Settings;
using Lineup.Core.Streaming;
using Lineup.Extensibility.Streaming;
using Lineup.Streaming.FFmpeg;
using Lineup.VirtualOutputs.Protocol;

namespace Lineup.VirtualOutputs.Runtime;

/// <summary>Provides instance-scoped Core capabilities to authenticated virtual-output workers.</summary>
public interface IVirtualOutputBroker
{
    /// <summary>Gets the assigned virtual-device manifest.</summary>
    Task<VirtualOutputManifest> GetManifestAsync(VirtualOutputManifestRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets the assigned virtual-device lineup.</summary>
    Task<VirtualOutputLineupResponse> GetLineupAsync(VirtualOutputLineupRequest request, CancellationToken cancellationToken = default);

    /// <summary>Gets the assigned virtual-device guide.</summary>
    Task<VirtualOutputGuideResponse> GetGuideAsync(VirtualOutputGuideRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens one assigned MPEG-TS channel stream.</summary>
    Task<VirtualOutputBrokeredStream> OpenStreamAsync(VirtualOutputOpenStreamRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Owns one brokered stream and its active-stream registration.</summary>
public sealed class VirtualOutputBrokeredStream : IAsyncDisposable
{
    private readonly IActiveStreamRegistry _activeStreams;
    private readonly VirtualMpegTsOutput _output;
    private int _disposed;

    internal VirtualOutputBrokeredStream(
        IActiveStreamRegistry activeStreams,
        VirtualMpegTsOutput output,
        VirtualOutputOpenStreamResponse response)
    {
        _activeStreams = activeStreams;
        _output = output;
        Response = response;
    }

    /// <summary>Gets accepted stream metadata.</summary>
    public VirtualOutputOpenStreamResponse Response { get; }

    /// <summary>Gets the readable MPEG-TS stream.</summary>
    public Stream Stream => _output.Stream;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        if (Response.StreamSessionId is { } sessionId)
        {
            _activeStreams.Unregister(sessionId.ToString());
        }
        await _output.DisposeAsync();
    }
}

/// <summary>Authorizes and adapts trusted Core streaming and guide services for isolated workers.</summary>
public sealed class VirtualOutputBroker : IVirtualOutputBroker
{
    private readonly IVirtualOutputSessionAuthority _authority;
    private readonly ILineupSettingsService _settings;
    private readonly IStreamingAdapterRegistry _adapters;
    private readonly IPhysicalDeviceCatalog _physicalDevices;
    private readonly IStreamingRouteResolver _routes;
    private readonly IGuideOrchestrator _guides;
    private readonly IVirtualMpegTsOutputService _streams;
    private readonly IActiveStreamRegistry _activeStreams;

    /// <summary>Initializes the trusted virtual-output capability broker.</summary>
    public VirtualOutputBroker(
        IVirtualOutputSessionAuthority authority,
        ILineupSettingsService settings,
        IStreamingAdapterRegistry adapters,
        IPhysicalDeviceCatalog physicalDevices,
        IStreamingRouteResolver routes,
        IGuideOrchestrator guides,
        IVirtualMpegTsOutputService streams,
        IActiveStreamRegistry activeStreams)
    {
        _authority = authority;
        _settings = settings;
        _adapters = adapters;
        _physicalDevices = physicalDevices;
        _routes = routes;
        _guides = guides;
        _streams = streams;
        _activeStreams = activeStreams;
    }

    /// <inheritdoc />
    public Task<VirtualOutputManifest> GetManifestAsync(VirtualOutputManifestRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = AuthorizeAndResolve(request.Session, VirtualOutputCapability.ReadManifest);
        var driver = _adapters.GetVirtual(device.Adapter.Kind);
        var capacity = driver is IVirtualTunerCapacityProvider capacityProvider
            ? capacityProvider.GetCapacity(device, _physicalDevices.Devices.Select(runtime => runtime.Snapshot))
            : new PhysicalTunerCapacity(null);
        return Task.FromResult(new VirtualOutputManifest
        {
            VirtualDeviceId = device.Id,
            Name = device.Name,
            TunerCount = capacity.TunerCount,
            GuideAvailable = true
        });
    }

    /// <inheritdoc />
    public async Task<VirtualOutputLineupResponse> GetLineupAsync(VirtualOutputLineupRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = AuthorizeAndResolve(request.Session, VirtualOutputCapability.ReadLineup);
        return new VirtualOutputLineupResponse
        {
            Channels = await _routes.GetVirtualLineupAsync(device.Id, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async Task<VirtualOutputGuideResponse> GetGuideAsync(VirtualOutputGuideRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = AuthorizeAndResolve(request.Session, VirtualOutputCapability.ReadGuide);
        return new VirtualOutputGuideResponse
        {
            Snapshot = await _guides.BuildSnapshotAsync(device.Id, cancellationToken)
        };
    }

    /// <inheritdoc />
    public async Task<VirtualOutputBrokeredStream> OpenStreamAsync(VirtualOutputOpenStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var validation = VirtualOutputProtocolValidation.Validate(request);
        if (validation.Count > 0)
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.InvalidRequest, string.Join(" ", validation));
        }
        var device = AuthorizeAndResolve(request.Session, VirtualOutputCapability.OpenStream);
        VirtualMpegTsOutput output;
        try
        {
            output = await _streams.OpenAsync(device.Id, request.ChannelKey, cancellationToken);
        }
        catch (KeyNotFoundException exception)
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.ChannelNotFound, exception.Message, exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.Unavailable, exception.Message, exception);
        }

        var streamSessionId = StreamSessionId.New();
        var activeStream = CreateActiveStream(streamSessionId, request, output);
        if (!_activeStreams.TryRegister(activeStream, _settings.Settings.MaximumConcurrentStreams, () => output.Stream.Dispose()))
        {
            await output.DisposeAsync();
            throw new VirtualOutputBrokerException(VirtualOutputFailureCode.ResourceLimit, "The maximum number of concurrent streams is already active.");
        }

        return new VirtualOutputBrokeredStream(
            _activeStreams,
            output,
            new VirtualOutputOpenStreamResponse
            {
                Accepted = true,
                ContentType = "video/mp2t",
                StreamSessionId = streamSessionId
            });
    }

    private VirtualDeviceDefinition AuthorizeAndResolve(VirtualOutputSessionContext session, VirtualOutputCapability capability)
    {
        var authorization = _authority.Authorize(session, capability);
        if (!authorization.Authorized)
        {
            throw new VirtualOutputBrokerException(
                authorization.FailureCode ?? VirtualOutputFailureCode.Unauthorized,
                authorization.Failure ?? "The worker request is not authorized.");
        }
        return _settings.Settings.Streaming.VirtualDevices.SingleOrDefault(
            device => device.Enabled && device.Id == authorization.VirtualDeviceId)
            ?? throw new VirtualOutputBrokerException(VirtualOutputFailureCode.Unavailable, "The assigned virtual device is no longer enabled.");
    }

    private static ActiveStreamSnapshot CreateActiveStream(
        StreamSessionId sessionId,
        VirtualOutputOpenStreamRequest request,
        VirtualMpegTsOutput output)
    {
        var tracks = output.Source.Tracks.Select(track => new ActiveStreamTrack(
            track.Type,
            track.Codec,
            track.Codec,
            track.BitRate,
            track.BitRate,
            track.Width,
            track.Height,
            track.Channels,
            track.Channels,
            track.SampleRate)
        {
            SourceIndex = track.Index,
            Language = track.Language,
            Title = track.Title,
            IsDefault = track.IsDefault,
            IsForced = track.IsForced,
            IsHearingImpaired = track.IsHearingImpaired,
            HasClosedCaptions = track.HasClosedCaptions,
            IsEmbeddedClosedCaptions = track.IsEmbeddedClosedCaptions,
            SubtitlePresentation = track.SubtitlePresentation
        }).ToArray();
        return new ActiveStreamSnapshot(
            sessionId.ToString(),
            request.ChannelKey,
            HostedStreamFormat.MpegTs,
            DateTime.UtcNow,
            output.RawSource.BitRate,
            tracks)
        {
            ClientAddress = request.Client.Address,
            AuthenticatedClientId = request.Client.AuthenticatedClientId,
            AuthenticatedClientName = request.Client.AuthenticatedClientName,
            Flow = output.Flow,
            PhysicalInput = CreateMediaSnapshot(output.RawSource),
            PhysicalOutput = CreateMediaSnapshot(output.Source),
            VirtualOutput = CreateMediaSnapshot(output.Source)
        };
    }

    private static ActiveStreamMediaSnapshot CreateMediaSnapshot(MediaProbeResult source) =>
        new(
            source.BitRate,
            source.Tracks.Select(track => new ActiveStreamMediaTrack(
                track.Index,
                track.Type,
                track.Codec,
                track.BitRate,
                track.Width,
                track.Height,
                track.Channels,
                track.SampleRate)
            {
                Language = track.Language,
                Title = track.Title
            }).ToArray());
}

/// <summary>Represents a structured capability-broker rejection safe to translate at the IPC boundary.</summary>
public sealed class VirtualOutputBrokerException : InvalidOperationException
{
    /// <summary>Initializes a broker rejection.</summary>
    public VirtualOutputBrokerException(VirtualOutputFailureCode failureCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    /// <summary>Gets the structured failure code.</summary>
    public VirtualOutputFailureCode FailureCode { get; }
}
